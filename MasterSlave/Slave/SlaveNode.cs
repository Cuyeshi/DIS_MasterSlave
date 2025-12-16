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
                        // Внутри цикла while, когда пришел type == "task"
                        if (obj != null && obj.TryGetValue("type", out var t) && t.ToString() == "task")
                        {
                            var task = JsonConvert.DeserializeObject<TaskAssign>(s);
                            OnLog?.Invoke($"[Slave] Got task {task.taskId} ({task.texts.Length} texts)");

                            var results = new List<TaskResultItem>();
                            long totalProcessingTime = 0;

                            foreach (var txt in task.texts)
                            {
                                // Замеряем время обработки ОДНОГО текста
                                var sw = System.Diagnostics.Stopwatch.StartNew();

                                // Твой метод обработки (парсинг + стемминг + биграммы)
                                var counts = TextProcessor.ParseText(txt.text, useBigrams: true); // Или CountWords...

                                sw.Stop();

                                results.Add(new TaskResultItem
                                {
                                    id = txt.id,
                                    counts = counts,
                                    processingMs = sw.ElapsedMilliseconds
                                });

                                totalProcessingTime += sw.ElapsedMilliseconds;
                            }

                            var resMsg = new TaskResult { slaveId = slaveId, taskId = task.taskId, results = results.ToArray() };
                            await TcpHelpers.SendJsonAsync(stream, resMsg);

                            // Логируем суммарную нагрузку на CPU этого слейва
                            OnLog?.Invoke($"[Slave] Processed batch in {totalProcessingTime} ms (CPU time). Sent results.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke("Slave error processing: " + ex.Message);
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

        //private Dictionary<string, int> CountWordsWithStemmingAndBigrams(string text)
        //{
        //    var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        //    // 1. Разбиваем на слова (только буквы)
        //    var rawTokens = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"\p{L}+")
        //        .Cast<System.Text.RegularExpressions.Match>()
        //        .Select(m => m.Value)
        //        .ToList();

        //    if (rawTokens.Count == 0) return dict;

        //    // 2. Стемминг
        //    var stemmedTokens = rawTokens.Select(w => SimpleRussianStemmer.Stem(w)).ToList();

        //    // 3. Собираем Униграммы (отдельные слова)
        //    foreach (var token in stemmedTokens)
        //    {
        //        if (!dict.ContainsKey(token)) dict[token] = 0;
        //        dict[token]++;
        //    }

        //    // 4. Собираем Биграммы (пары слов) для учета контекста
        //    // Например: "не люблю" -> "не_люб" (после стемминга)
        //    //for (int i = 0; i < stemmedTokens.Count - 1; i++)
        //    //{
        //    //    string bigram = stemmedTokens[i] + "_" + stemmedTokens[i + 1];
        //    //    if (!dict.ContainsKey(bigram)) dict[bigram] = 0;
        //    //    dict[bigram]++; // Биграммы часто имеют тот же вес, что и слова, или чуть меньше. Здесь считаем равноправно.
        //    //}

        //    return dict;
        //}
    }
}
