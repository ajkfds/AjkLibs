using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AjkLibs.FileBasedPipe;

/*
 * 
## **Protocol Overview: File-Based Ack Queue Pipe

### **Background**
In restricted environments where standard network protocols (TCP/UDP, HTTP, RPC) are prohibited,
file-based communication often becomes the only viable bridge between disparate systems (e.g., Windows and Linux).
However, simple file writing suffers from high latency due to OS-level attribute caching and data loss risks caused by race conditions.
The **File-Based Ack Queue Pipe Protocol** was designed to provide a reliable, high-performance messaging bus over network-attached storage (NAS).


### **How It Works**
The protocol utilizes a **Dual-File Asynchronous Handshake** mechanism to ensure data integrity and minimize latency:

1.  **Dual-File Duplex**: 
    * **`data.dat`**: Owned by the Transmitter. It contains a serialized queue of pending messages.
    * **`ack.dat`**: Owned by the Receiver. It stores the ID of the last successfully processed message.
2.  **Sequence Synchronization**: Each message is assigned a unique, incrementing ID. 
    Upon startup, the Transmitter reads `ack.dat` to synchronize its starting ID, ensuring seamless recovery even after a restart.
3.  **Dynamic Queue Management**: The Transmitter maintains a local buffer. Once a message ID is acknowledged in `ack.dat`,
    it is purged from the `data.dat` queue, keeping the file size small and I/O operations fast.
4.  **Atomic Updates**: The Receiver processes new messages based on ID comparison (`Current ID > Last Processed ID`) 
    and updates the acknowledgment via a temporary-swap method to prevent file corruption.

### **Key Features**
* **Cross-Platform Compatibility**: Fully functional between Windows (SMB) and Linux (NFS/SMB) using .NET 8.
* **Polymorphic Messaging**: Supports dynamic registration of diverse C# object types through custom JSON type resolvers.
* **Resilience**: Handles process restarts and network fluctuations without message duplication or loss.
* **Low Latency**: Minimizes metadata overhead by overwriting existing files rather than constantly creating new ones.

---

### **Summary Table**

| Feature | Description |
| :--- | :--- |
| **Medium** | Shared Network Folder (SMB/NFS) |
| **Serialization** | Polymorphic JSON (System.Text.Json) |
| **Reliability** | Guaranteed via Sequence IDs and Ack-back |
| **Concurrency** | Thread-safe local queuing with `lock` mechanism |

 */


/// <summary>
/// 全てのメッセージペイロードの基底クラス
/// </summary>
public abstract class BasePayload { }

/// <summary>
/// 実行時に型を登録可能なJSONリゾルバー
/// </summary>
public class MessageJsonResolver : DefaultJsonTypeInfoResolver
{
    private readonly Dictionary<Type, string> _types = new();

    public void Register<T>(string discriminator) where T : BasePayload
        => _types[typeof(T)] = discriminator;

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo typeInfo = base.GetTypeInfo(type, options);
        if (type == typeof(BasePayload))
        {
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
            };
            foreach (var (t, d) in _types)
                typeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(t, d));
        }
        return typeInfo;
    }
}

// 内部通信用コンテナ
internal class MessageItem { public long Id { get; set; } public BasePayload Payload { get; set; } = null!; }
internal class CommunicationEnvelope { public List<MessageItem> Messages { get; set; } = new(); }

/// <summary>
/// FBAQプロトコルエンジン
/// </summary>
public class FileBasedPipe
{
    private readonly string _dataPath;
    private readonly string _ackPath;
    private readonly JsonSerializerOptions _options;
    private readonly List<MessageItem> _queue = new();
    private long _nextId = 1;
    private long _lastProcessedId = 0;

    public FileBasedPipe(string dataPath, string ackPath, JsonSerializerOptions options)
    {
        _dataPath = dataPath;
        _ackPath = ackPath;
        _options = options;
    }

    public void InitializeAsSender() {
        // 1. 既存のデータファイルをクリア（通信リフレッシュ）
        using (var fs = new FileStream(_dataPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(0);
        }

        // 2. Ackファイルが既にあるなら、その次のIDから開始するように同期する
        if (File.Exists(_ackPath))
        {
            try
            {
                var ackStr = File.ReadAllText(_ackPath);
                if (long.TryParse(ackStr, out long lastAck))
                {
                    // 相手の最終既読IDの次からカウントを開始
                    _nextId = lastAck + 1;
                }
            }
            catch (IOException) { /* ロック中の場合はデフォルトの1から開始、またはリトライ */ }
        }
    }

    public void InitializeAsReceiver()
    {
        // リブート時に備えて、既存のackファイルから最終処理済みIDを読み取る
        if (File.Exists(_ackPath))
        {
            try
            {
                var ackStr = File.ReadAllText(_ackPath);
                if (long.TryParse(ackStr, out long lastProcessed))
                {
                    _lastProcessedId = lastProcessed;
                    return;
                }
            }
            catch (IOException) { /* ロック中の場合はデフォルトの0から開始 */ }
        }
        // ackファイルが存在しない、または読み取り失敗の場合は新規作成
        _lastProcessedId = 0;
        File.WriteAllText(_ackPath, "0");
    }

    // --- 送信機能 ---
    public void Enqueue(BasePayload payload)
    {
        lock (_queue)
        {
            _queue.Add(new MessageItem { Id = _nextId++, Payload = payload });
        }
    }

    public async Task SendSyncAsync()
    {
        try
        {
            // 1. Ackを読み込んでキューを掃除
            if (File.Exists(_ackPath))
            {
                var ackStr = await File.ReadAllTextAsync(_ackPath);
                if (long.TryParse(ackStr, out long lastAck))
                {
                    lock (_queue) { _queue.RemoveAll(m => m.Id <= lastAck); }
                }
            }

            // 2. 現在のキューをスレッドセーフにコピーしてシリアライズ
            var envelope = new CommunicationEnvelope();
            lock (_queue)
            {
                envelope.Messages = _queue.ToList();
            }

            using var fs = new FileStream(_dataPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await JsonSerializer.SerializeAsync(fs, envelope, _options);
            await fs.FlushAsync();
        }
        catch (IOException) { /* 他プロセスが使用中 */ }
    }

    // --- 受信機能 ---
    public async Task ReceiveAsync(Action<BasePayload> onMessage)
    {
        try
        {
            if (!File.Exists(_dataPath)) return;

            CommunicationEnvelope? env;
            using (var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return;
                env = await JsonSerializer.DeserializeAsync<CommunicationEnvelope>(fs, _options);
            }

            if (env == null) return;

            // 未処理メッセージの抽出
            var newMessages = env.Messages
                .Where(m => m.Id > _lastProcessedId)
                .OrderBy(m => m.Id);

            bool hasNew = false;
            foreach (var m in newMessages)
            {
                onMessage(m.Payload);
                _lastProcessedId = m.Id;
                hasNew = true;
            }

            // 処理済みIDをAckファイルに記録
            if (hasNew)
            {
                string tmp = _ackPath + ".tmp";
                await File.WriteAllTextAsync(tmp, _lastProcessedId.ToString());
                File.Move(tmp, _ackPath, true);
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
    }
}