using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AjkLib.FileBasedPipe
{
    public class MessageItem
    {
        public long Id { get; set; }
        public Dictionary<string, string> Data { get; set; } = new(); 
    }
}
