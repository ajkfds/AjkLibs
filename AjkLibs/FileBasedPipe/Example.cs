using AjkLibs.FileBasedPipe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AjkLibs.FileBasedPipe
{
    public class Example
    {

        // 1. 通信したい独自のデータ型を定義
        public class TextMessage : BasePayload { public string Text { get; set; } = ""; }
        public class SystemAlert : BasePayload
        {
            public int Severity { get; set; }
            public string Code { get; set; } = "";
        }

        public async Task test()
        {
            // 2. リゾルバーの設定（ここで型を動的に登録）
            var resolver = new MessageJsonResolver();
            resolver.Register<TextMessage>("text");
            resolver.Register<SystemAlert>("alert");

            var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
            // 3. パイプの生成 (Windows/Linux共有フォルダ上のパスを指定)
            var pipe = new FileBasedPipe("Z:\\shared\\data.dat", "Z:\\shared\\ack.dat", options);

            // --- 送信側の動作イメージ ---
            pipe.InitializeAsSender();
            pipe.Enqueue(new TextMessage { Text = "Hello from Windows!" });
            pipe.Enqueue(new SystemAlert { Severity = 5, Code = "ERR_001" });
            await pipe.SendSyncAsync();

            // --- 受信側の動作イメージ ---
            pipe.InitializeAsReceiver();
            await pipe.ReceiveAsync(payload =>
            {
                // C#のパターンマッチングで型ごとに処理
                switch (payload)
                {
                    case TextMessage t:
                        Console.WriteLine($"[Text] {t.Text}");
                        break;
                    case SystemAlert a:
                        Console.WriteLine($"[ALERT] Severity: {a.Severity}, Code: {a.Code}");
                        break;
                }
            });

        }


    }
}
