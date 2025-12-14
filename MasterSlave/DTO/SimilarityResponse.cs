using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.DTO
{
    public class SimilarityResponse 
    { 
        public string type { get; set; } = "similarity"; 
        public string clientId { get; set; } 
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, double>> matrix { get; set; } 
    }
}
