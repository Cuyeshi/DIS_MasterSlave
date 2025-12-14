using MasterSlave.Backend;
using MasterSlave.DTO;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave.Slave
{
    public class SlaveNode
    {
        private string slaveId;
        private string masterHost;
        private int masterPort;
        private CancellationTokenSource cts;
        public event Action<string> OnLog;

        public SlaveNode(string id, string host = "127.0.0.1", int port = 5000)
        {
            slaveId = id; masterHost = host; masterPort = port;
        }

        public void Start()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            _ = RunAsync(cts.Token);
        }

        public void Stop()
        {
            if (cts == null) return;
            cts.Cancel();
            cts = null;
        }

        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(masterHost, masterPort);
                var stream = client.GetStream();

                var reg = new RegisterMessage { role = "slave", slaveId = slaveId };
                await TcpHelpers.SendJsonAsync(stream, reg);
                OnLog?.Invoke("Registered as " + slaveId);

                while (client.Connected && !token.IsCancellationRequested)
                {
                    try
                    {
                        var s = await TcpHelpers.ReadJsonStringAsync(stream);
                        var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(s);
                        if (obj != null && obj.TryGetValue("type", out var t) && t.ToString() == "task")
                        {
                            var task = JsonConvert.DeserializeObject<TaskAssign>(s);
                            OnLog?.Invoke($"Received task {task.taskId} with {task.texts.Length} texts");
                            var results = new List<TaskResultItem>();
                            foreach (var txt in task.texts)
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                var counts = CountWords(txt.text);
                                sw.Stop();
                                results.Add(new TaskResultItem { id = txt.id, counts = counts, processingMs = sw.ElapsedMilliseconds });
                                await Task.Delay(10, token).ContinueWith(_ => { });
                            }
                            var resMsg = new TaskResult { slaveId = slaveId, taskId = task.taskId, results = results.ToArray() };
                            await TcpHelpers.SendJsonAsync(stream, resMsg);
                            OnLog?.Invoke($"Sent results for {task.taskId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke("Slave error: " + ex.Message);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("Slave connection failed: " + ex.Message);
            }
            finally
            {
                OnLog?.Invoke("Slave stopped");
            }
        }

        private Dictionary<string, int> CountWords(string text)
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tokens = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"\p{L}[\p{L}\p{N}]*");
            foreach (System.Text.RegularExpressions.Match m in tokens)
            {
                var w = m.Value;
                if (!dict.ContainsKey(w)) dict[w] = 0;
                dict[w]++;
            }
            return dict;
        }
    }
}
