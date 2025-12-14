using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class TaskResult 
    { 
        public string type { get; set; } = "result"; 
        public string slaveId { get; set; } public string taskId { get; set; } 
        public TaskResultItem[] results { get; set; } 
    }
}
