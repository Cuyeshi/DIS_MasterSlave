using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class TaskAssign 
    { 
        public string type { get; set; } = "task"; 
        public string taskId { get; set; } public TextItem[] texts { get; set; } 
    }
}
