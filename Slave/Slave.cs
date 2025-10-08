using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Newtonsoft.Json;

class Slave
{
    private string slaveId;
    private string masterHost;
    private int masterPort;

    public Slave(string id, string host = "127.0.0.1", int port = 5000)
    {
        slaveId = id; masterHost = host; masterPort = port;
    }

    public async Task RunAsync()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(masterHost, masterPort);
        var stream = client.GetStream();

        // register
        var reg = new RegisterMessage { role = "slave", slaveId = slaveId };
        await TcpHelpers.SendJsonAsync(stream, reg);
        Console.WriteLine("Registered as " + slaveId);

        while (client.Connected)
        {
            try
            {
                var s = await TcpHelpers.ReadJsonStringAsync(stream);
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(s);
                if (obj != null && obj.TryGetValue("type", out var t) && t.ToString() == "task")
                {
                    var task = JsonConvert.DeserializeObject<TaskAssign>(s);
                    Console.WriteLine($"Received task {task.taskId} with {task.texts.Length} texts");
                    var results = new List<TaskResultItem>();
                    foreach (var txt in task.texts)
                    {
                        var sw = Stopwatch.StartNew();
                        var counts = CountWords(txt.text);
                        sw.Stop();
                        results.Add(new TaskResultItem { id = txt.id, counts = counts, processingMs = sw.ElapsedMilliseconds });
                        // simulate small delay
                        await Task.Delay(10);
                    }
                    var resMsg = new TaskResult { slaveId = slaveId, taskId = task.taskId, results = results.ToArray() };
                    await TcpHelpers.SendJsonAsync(stream, resMsg);
                    Console.WriteLine($"Sent results for {task.taskId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Slave error: " + ex.Message);
                break;
            }
        }
    }

    private Dictionary<string, int> CountWords(string text)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // simple tokenization: letters and digits only
        var tokens = Regex.Matches(text.ToLowerInvariant(), @"\p{L}[\p{L}\p{N}]*");
        foreach (Match m in tokens)
        {
            var w = m.Value;
            if (!dict.ContainsKey(w)) dict[w] = 0;
            dict[w]++;
        }
        return dict;
    }

    static async Task Main(string[] args)
    {
        var id = args.Length > 0 ? args[0] : ("slave-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        var slave = new Slave(id);
        await slave.RunAsync();
    }
}
