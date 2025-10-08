using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading;

class Master
{
    private readonly int port;
    private TcpListener listener;
    private ConcurrentDictionary<string, TcpClient> slaves = new(); // slaveId -> TcpClient
    private ConcurrentQueue<TextItem> taskQueue = new();
    private ConcurrentDictionary<string, Dictionary<string, int>> textCounts = new(); // textId -> counts
    private ConcurrentDictionary<string, TaskCompletionSource<bool>> textCompletion = new();
    private object slaveLock = new();
    private List<string> slaveOrder = new();

    public Master(int port = 5000) { this.port = port; }

    public async Task StartAsync()
    {
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"Master listening on {port}");
        _ = AcceptLoop();

        // simple loop to check queue and dispatch when slaves available
        _ = DispatchLoop();
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleConnection(client);
        }
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
                    Console.WriteLine($"Slave registered: {reg.slaveId}");
                    slaves[reg.slaveId] = client;
                    lock (slaveLock) { slaveOrder.Add(reg.slaveId); }
                    _ = ListenSlave(reg.slaveId, client);
                    return;
                }
            }
            else if (baseObj != null && baseObj.TryGetValue("type", out var tt) && tt.ToString() == "submit")
            {
                var sub = JsonConvert.DeserializeObject<SubmitMessage>(firstJson);
                Console.WriteLine($"Client submitted {sub.texts.Length} texts from {sub.clientId}");
                // enqueue texts
                foreach (var titem in sub.texts)
                {
                    taskQueue.Enqueue(titem);
                    textCompletion[titem.id] = new TaskCompletionSource<bool>();
                }
                // wait until all texts processed (with timeout)
                var tasks = sub.texts.Select(it => textCompletion[it.id].Task).ToArray();
                var all = Task.WhenAll(tasks);
                if (await Task.WhenAny(all, Task.Delay(30000)) != all)
                {
                    Console.WriteLine("Timeout waiting for slaves");
                }
                // build matrix and respond
                var matrix = BuildSimilarityMatrix(sub.texts.Select(x => x.id).ToArray());
                var resp = new SimilarityResponse { clientId = sub.clientId, matrix = matrix };
                await TcpHelpers.SendJsonAsync(stream, resp);
                client.Close();
                return;
            }

            Console.WriteLine("Unknown initial message, closing.");
            client.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("HandleConnection error: " + ex.Message);
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
                    // store counts and mark texts as done
                    foreach (var r in res.results)
                    {
                        textCounts[r.id] = r.counts;
                        if (textCompletion.TryGetValue(r.id, out var tcs)) tcs.TrySetResult(true);
                        Console.WriteLine($"Received result for {r.id} from {res.slaveId} in {r.processingMs}ms");
                    }
                }
            }
        }
        catch (IOException) { /* connection closed */ }
        catch (Exception ex) { Console.WriteLine("ListenSlave error: " + ex.Message); }
        finally
        {
            Console.WriteLine($"Slave disconnected: {slaveId}");
            slaves.TryRemove(slaveId, out _);
            lock (slaveLock) { slaveOrder.Remove(slaveId); }
            try { client.Close(); } catch { }
        }
    }

    private async Task DispatchLoop()
    {
        int rr = 0;
        while (true)
        {
            if (taskQueue.IsEmpty || slaveOrder.Count == 0)
            {
                await Task.Delay(100);
                continue;
            }

            // gather next batch equal to number of slaves to distribute fairly (or send 1 per slave)
            List<string> currentSlaves;
            lock (slaveLock) { currentSlaves = slaveOrder.ToList(); }
            int n = currentSlaves.Count;
            if (n == 0) { await Task.Delay(100); continue; }

            // distribute: take up to n items from queue and assign one to each slave
            var assignments = new Dictionary<string, List<TextItem>>();
            for (int i = 0; i < n; i++) assignments[currentSlaves[i]] = new List<TextItem>();

            int assigned = 0;
            while (assigned < n && taskQueue.TryDequeue(out var titem))
            {
                var slaveId = currentSlaves[assigned % n];
                assignments[slaveId].Add(titem);
                assigned++;
            }

            // send tasks
            foreach (var kv in assignments)
            {
                if (kv.Value.Count == 0) continue;
                if (!slaves.TryGetValue(kv.Key, out var client)) continue;
                try
                {
                    var stream = client.GetStream();
                    var task = new TaskAssign { taskId = Guid.NewGuid().ToString(), texts = kv.Value.ToArray() };
                    await TcpHelpers.SendJsonAsync(stream, task);
                    Console.WriteLine($"Assigned {kv.Value.Count} texts to {kv.Key}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send to {kv.Key}: {ex.Message}");
                }
            }

            await Task.Delay(50);
        }
    }

    private Dictionary<string, Dictionary<string, double>> BuildSimilarityMatrix(string[] ids)
    {
        // Build vocabulary
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
                    v[idx[kv.Key]] = kv.Value / sum; // normalized TF
            }
            vectors[id] = v;
        }

        // cosine similarity
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

    static async Task Main(string[] args)
    {
        var master = new Master(5000);
        await master.StartAsync();
        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}
