using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class RegisterMessage 
    { 
        public string type { get; set; } = "register"; 
        public string role { get; set; } public string slaveId { get; set; } 
    }
}
