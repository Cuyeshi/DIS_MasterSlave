using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class TaskResultItem 
    { 
        public string id { get; set; } 
        public System.Collections.Generic.Dictionary<string, int> counts { get; set; } 
        public long processingMs { get; set; } 
    }
}
