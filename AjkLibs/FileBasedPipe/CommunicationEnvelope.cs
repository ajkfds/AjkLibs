using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AjkLib.FileBasedPipe
{
    public class CommunicationEnvelope
    {
        // 送信側が「現在保持している未送信リスト」を格納
        public List<MessageItem> Messages { get; set; } = new();
    }
}
