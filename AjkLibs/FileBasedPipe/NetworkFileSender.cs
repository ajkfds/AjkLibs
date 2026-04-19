using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace AjkLib.FileBasedPipe
{

    public class NetworkFileSender
    {
        private readonly string _dataPath;
        private readonly string _ackPath;
        private List<MessageItem> _pendingQueue = new();
        private long _nextId = 1;

        public NetworkFileSender(string dataPath, string ackPath)
        {
            _dataPath = dataPath;
            _ackPath = ackPath;
        }

        // 起動時の初期化：古いファイルをクリアし、相手のAck状態を確認
        public void Initialize()
        {
            using (var fs = new FileStream(_dataPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.SetLength(0); // 内容をクリア
            }
            Console.WriteLine("Sender initialized and data file cleared.");
        }

        // メッセージをキューに追加
        public void Enqueue(Dictionary<string, string> data)
        {
            _pendingQueue.Add(new MessageItem { Id = _nextId++, Data = data });
        }

        // 同期処理：Ackを読み取り、未送信分を書き込む
        public async Task SyncAsync()
        {
            try
            {
                // 1. 受信側からのAckを確認
                if (File.Exists(_ackPath))
                {
                    string ackContent = await File.ReadAllTextAsync(_ackPath);
                    if (long.TryParse(ackContent, out long lastAckId))
                    {
                        // Ack済みのメッセージを削除
                        _pendingQueue.RemoveAll(m => m.Id <= lastAckId);
                    }
                }

                // 2. data.dat を上書き更新
                var envelope = new CommunicationEnvelope { Messages = _pendingQueue.ToList() };
                using (var fs = new FileStream(_dataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(0);
                    await JsonSerializer.SerializeAsync(fs, envelope);
                    await fs.FlushAsync(); // OSキャッシュに強制書き込み
                }
            }
            catch (IOException) { /* 他プロセスが使用中の場合は次回のサイクルでリトライ */ }
        }
    }
}
