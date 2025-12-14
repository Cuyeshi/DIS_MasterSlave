using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class SubmitMessage 
    { 
        public string type { get; set; } = "submit"; 
        public string clientId { get; set; } public TextItem[] texts { get; set; } 
    }
}
