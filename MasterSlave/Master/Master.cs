using MasterSlave.Backend;
using MasterSlave.DTO;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MasterSlave
{
    public class Master
    {
        private readonly int port;
        private TcpListener listener;
        private CancellationTokenSource cts;
        private ConcurrentDictionary<string, TcpClient> slaves = new();
        private ConcurrentQueue<TextItem> taskQueue = new();
        private ConcurrentDictionary<string, Dictionary<string, int>> textCounts = new();
        private ConcurrentDictionary<string, TaskCompletionSource<bool>> textCompletion = new();
        private object slaveLock = new();
        private List<string> slaveOrder = new();

        public event Action<string> OnLog;
        public event Action<List<string>> OnSlaveListChanged;
        public event Action<Dictionary<string, Dictionary<string, double>>> OnMatrixReady;

        public Master(int port = 5000) { this.port = port; }

        public async Task StartAsync()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            OnLog?.Invoke($"Master listening on {port}");
            _ = AcceptLoop(cts.Token);
            _ = DispatchLoop(cts.Token);
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (cts == null) return;
            cts.Cancel();
            try { listener.Stop(); } catch { }
            OnLog?.Invoke("Master stopped");
            slaves.Clear();
            lock (slaveLock) { slaveOrder.Clear(); }
            OnSlaveListChanged?.Invoke(slaveOrder.ToList());
            cts = null;
            await Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = HandleConnection(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { OnLog?.Invoke("AcceptLoop error: " + ex.Message); }
        }

        private async Task HandleConnection(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var firstJson = await TcpHelpers.ReadJsonStringAsync(stream);
                var baseObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(firstJson);
                if (baseObj != null && baseObj.TryGetValue("type", out var t) && t.ToString() == "register")
                {
                    var reg = JsonConvert.DeserializeObject<RegisterMessage>(firstJson);
                    if (reg.role == "slave")
                    {
                        OnLog?.Invoke($"Slave registered: {reg.slaveId}");
                        slaves[reg.slaveId] = client;
                        lock (slaveLock) { slaveOrder.Add(reg.slaveId); }
                        OnSlaveListChanged?.Invoke(slaveOrder.ToList());
                        _ = ListenSlave(reg.slaveId, client);
                        return;
                    }
                }
                else if (baseObj != null && baseObj.TryGetValue("type", out var tt) && tt.ToString() == "submit")
                {
                    var sub = JsonConvert.DeserializeObject<SubmitMessage>(firstJson);
                    OnLog?.Invoke($"Client submitted {sub.texts.Length} texts from {sub.clientId}");
                    foreach (var titem in sub.texts)
                    {
                        taskQueue.Enqueue(titem);
                        textCompletion[titem.id] = new TaskCompletionSource<bool>();
                    }
                    var tasks = sub.texts.Select(it => textCompletion[it.id].Task).ToArray();
                    var all = Task.WhenAll(tasks);
                    if (await Task.WhenAny(all, Task.Delay(30000)) != all)
                    {
                        OnLog?.Invoke("Timeout waiting for slaves");
                    }
                    var matrix = BuildSimilarityMatrix(sub.texts.Select(x => x.id).ToArray());
                    var resp = new SimilarityResponse { clientId = sub.clientId, matrix = matrix };
                    await TcpHelpers.SendJsonAsync(stream, resp);
                    OnLog?.Invoke($"Responded to client {sub.clientId}");
                    client.Close();
                    OnMatrixReady?.Invoke(matrix);
                    return;
                }

                OnLog?.Invoke("Unknown initial message, closing.");
                client.Close();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("HandleConnection error: " + ex.Message);
                try { client.Close(); } catch { }
            }
        }

        private async Task ListenSlave(string slaveId, TcpClient client)
        {
            var stream = client.GetStream();
            try
            {
                while (client.Connected)
                {
                    var s = await TcpHelpers.ReadJsonStringAsync(stream);
                    var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(s);
                    if (obj != null && obj.TryGetValue("type", out var t) && t.ToString() == "result")
                    {
                        var res = JsonConvert.DeserializeObject<TaskResult>(s);
                        foreach (var r in res.results)
                        {
                            textCounts[r.id] = r.counts;
                            if (textCompletion.TryGetValue(r.id, out var tcs)) tcs.TrySetResult(true);
                            OnLog?.Invoke($"Received result for {r.id} from {res.slaveId} in {r.processingMs}ms");
                        }
                    }
                }
            }
            catch (IOException) { /* connection closed */ }
            catch (Exception ex) { OnLog?.Invoke("ListenSlave error: " + ex.Message); }
            finally
            {
                OnLog?.Invoke($"Slave disconnected: {slaveId}");
                slaves.TryRemove(slaveId, out _);
                lock (slaveLock) { slaveOrder.Remove(slaveId); }
                OnSlaveListChanged?.Invoke(slaveOrder.ToList());
                try { client.Close(); } catch { }
            }
        }

        private async Task DispatchLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (taskQueue.IsEmpty || slaveOrder.Count == 0)
                {
                    await Task.Delay(100, token).ContinueWith(_ => { });
                    continue;
                }

                List<string> currentSlaves;
                lock (slaveLock) { currentSlaves = slaveOrder.ToList(); }
                int n = currentSlaves.Count;
                if (n == 0) { await Task.Delay(100, token).ContinueWith(_ => { }); continue; }

                var assignments = new Dictionary<string, List<TextItem>>();
                for (int i = 0; i < n; i++) assignments[currentSlaves[i]] = new List<TextItem>();

                int assigned = 0;
                while (assigned < n && taskQueue.TryDequeue(out var titem))
                {
                    var slaveId = currentSlaves[assigned % n];
                    assignments[slaveId].Add(titem);
                    assigned++;
                }

                foreach (var kv in assignments)
                {
                    if (kv.Value.Count == 0) continue;
                    if (!slaves.TryGetValue(kv.Key, out var client)) continue;
                    try
                    {
                        var stream = client.GetStream();
                        var task = new TaskAssign { taskId = Guid.NewGuid().ToString(), texts = kv.Value.ToArray() };
                        await TcpHelpers.SendJsonAsync(stream, task);
                        OnLog?.Invoke($"Assigned {kv.Value.Count} texts to {kv.Key}");
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"Failed to send to {kv.Key}: {ex.Message}");
                    }
                }

                await Task.Delay(50, token).ContinueWith(_ => { });
            }
        }

        private Dictionary<string, Dictionary<string, double>> BuildSimilarityMatrix(string[] ids)
        {
            var vocab = new HashSet<string>();
            foreach (var id in ids)
                if (textCounts.TryGetValue(id, out var dict))
                    foreach (var w in dict.Keys) vocab.Add(w);

            var vocabList = vocab.ToList();
            var idx = vocabList.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);
            var vectors = new Dictionary<string, double[]>();
            foreach (var id in ids)
            {
                var v = new double[vocabList.Count];
                if (textCounts.TryGetValue(id, out var dict))
                {
                    double sum = dict.Values.Sum();
                    if (sum == 0) sum = 1;
                    foreach (var kv in dict)
                        v[idx[kv.Key]] = kv.Value / sum;
                }
                vectors[id] = v;
            }

            var matrix = new Dictionary<string, Dictionary<string, double>>();
            foreach (var a in ids)
            {
                matrix[a] = new Dictionary<string, double>();
                foreach (var b in ids)
                {
                    double sim = Cosine(vectors[a], vectors[b]);
                    matrix[a][b] = sim;
                }
            }
            return matrix;
        }

        private double Cosine(double[] a, double[] b)
        {
            double da = 0, db = 0, num = 0;
            for (int i = 0; i < a.Length; i++) { num += a[i] * b[i]; da += a[i] * a[i]; db += b[i] * b[i]; }
            if (da == 0 || db == 0) return 0;
            return num / (Math.Sqrt(da) * Math.Sqrt(db));
        }
    }
}
