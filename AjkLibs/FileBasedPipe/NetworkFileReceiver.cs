using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AjkLib.FileBasedPipe
{
    public class NetworkFileReceiver
    {
        private readonly string _dataPath;
        private readonly string _ackPath;
        private long _lastProcessedId = 0;

        public NetworkFileReceiver(string dataPath, string ackPath)
        {
            _dataPath = dataPath;
            _ackPath = ackPath;
        }

        public void Initialize()
        {
            // 自分のAckファイルをリセット
            File.WriteAllText(_ackPath, "0");
            Console.WriteLine("Receiver initialized and ack file reset to 0.");
        }

        public async Task ReceiveAsync(Action<Dictionary<string,string>> onMessageReceived)
        {
            try
            {
                if (!File.Exists(_dataPath)) return;

                CommunicationEnvelope? envelope = null;
                using (var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length > 0)
                    {
                        envelope = await JsonSerializer.DeserializeAsync<CommunicationEnvelope>(fs);
                    }
                }

                if (envelope == null || envelope.Messages.Count == 0) return;

                // 未処理のIDのみを抽出して昇順に処理
                var newMessages = envelope.Messages
                    .Where(m => m.Id > _lastProcessedId)
                    .OrderBy(m => m.Id);

                bool hasNew = false;
                foreach (var msg in newMessages)
                {
                    onMessageReceived(msg.Data);
                    _lastProcessedId = msg.Id;
                    hasNew = true;
                }

                // 新しく処理したものがあればAckを更新
                if (hasNew)
                {
                    // Linux環境を想定し、アトミックな書き換えのために一時ファイルを使用
                    string tempAck = _ackPath + ".tmp";
                    await File.WriteAllTextAsync(tempAck, _lastProcessedId.ToString());
                    File.Move(tempAck, _ackPath, true);
                }
            }
            catch (IOException) { /* 読み取り中の衝突 */ }
            catch (JsonException) { /* 不完全なファイル読み込み */ }
        }
    }
}
