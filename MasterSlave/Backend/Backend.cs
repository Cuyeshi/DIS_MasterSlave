using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MasterSlave
{
    // DTOs (как у тебя)
    public class TextItem { public string id { get; set; } public string text { get; set; } }
    public class SubmitMessage { public string type { get; set; } = "submit"; public string clientId { get; set; } public TextItem[] texts { get; set; } }
    public class RegisterMessage { public string type { get; set; } = "register"; public string role { get; set; } public string slaveId { get; set; } }
    public class TaskAssign { public string type { get; set; } = "task"; public string taskId { get; set; } public TextItem[] texts { get; set; } }
    public class TaskResultItem { public string id { get; set; } public System.Collections.Generic.Dictionary<string, int> counts { get; set; } public long processingMs { get; set; } }
    public class TaskResult { public string type { get; set; } = "result"; public string slaveId { get; set; } public string taskId { get; set; } public TaskResultItem[] results { get; set; } }
    public class SimilarityResponse { public string type { get; set; } = "similarity"; public string clientId { get; set; } public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, double>> matrix { get; set; } }

    public static class TcpHelpers
    {
        public static async Task SendJsonAsync(NetworkStream stream, object obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(len, 0, len.Length);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        public static async Task<string> ReadJsonStringAsync(NetworkStream stream)
        {
            var lenBuf = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = await stream.ReadAsync(lenBuf, read, 4 - read);
                if (r == 0) throw new IOException("Socket closed");
                read += r;
            }
            int len = BitConverter.ToInt32(lenBuf, 0);
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int r = await stream.ReadAsync(buf, got, len - got);
                if (r == 0) throw new IOException("Socket closed");
                got += r;
            }
            return Encoding.UTF8.GetString(buf);
        }
    }

    // Простая запись логов через событие
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
                await TcpHelpers.SendJsonAsync(stream, submit);
                OnLog?.Invoke($"Submitted {items.Length} texts");

                var respJson = await TcpHelpers.ReadJsonStringAsync(stream);
                var resp = JsonConvert.DeserializeObject<SimilarityResponse>(respJson);
                OnLog?.Invoke("Received similarity response");
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
