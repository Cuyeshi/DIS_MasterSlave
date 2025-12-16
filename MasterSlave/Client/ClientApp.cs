using MasterSlave.DTO;
using MasterSlave.Backend;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.Client
{
    public class ClientApp
    {
        public event Action<string> OnLog;

        public async Task<Dictionary<string, Dictionary<string, double>>> SubmitAsync(string host, int port, string clientId, List<string> texts)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                var stream = client.GetStream();

                var items = texts.Select((t, i) => new TextItem { id = "t" + (i + 1), text = t }).ToArray();
                var submit = new SubmitMessage { clientId = clientId, texts = items };

                // --- НАЧАЛО ЗАМЕРА ---
                var sw = System.Diagnostics.Stopwatch.StartNew();

                await TcpHelpers.SendJsonAsync(stream, submit);
                OnLog?.Invoke($"Submitted {items.Length} texts. Waiting...");

                var respJson = await TcpHelpers.ReadJsonStringAsync(stream);
                var resp = JsonConvert.DeserializeObject<SimilarityResponse>(respJson);

                sw.Stop();
                // --- КОНЕЦ ЗАМЕРА ---

                OnLog?.Invoke($"Response received in {sw.ElapsedMilliseconds} ms"); // Логируем время
                return resp.matrix;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("Client error: " + ex.Message);
                return null;
            }
        }
    }
}
