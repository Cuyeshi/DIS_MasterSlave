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
        public event Action<string> OnStatsUpdate; // Событие для статистики


        private readonly int port;
        private TcpListener listener;
        private CancellationTokenSource cts;
        private ConcurrentDictionary<string, TcpClient> slaves = new();
        private ConcurrentQueue<TextItem> taskQueue = new();

        // Храним результаты: ID документа -> Словарь {Слово: Количество}
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
            catch { }
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
                    OnLog?.Invoke($"[Master] Client {sub.clientId} submitted {sub.texts.Length} texts.");

                    // 1. Таймер полного цикла
                    var totalSw = System.Diagnostics.Stopwatch.StartNew();

                    // Очистка и постановка в очередь
                    foreach (var txt in sub.texts) textCounts.TryRemove(txt.id, out _);
                    foreach (var titem in sub.texts)
                    {
                        taskQueue.Enqueue(titem);
                        textCompletion[titem.id] = new TaskCompletionSource<bool>();
                    }

                    var tasks = sub.texts.Select(it => textCompletion[it.id].Task).ToArray();
                    var all = Task.WhenAll(tasks);

                    // 2. Таймер ожидания Слейвов (Сетевая задержка + работа Слейвов)
                    var waitSw = System.Diagnostics.Stopwatch.StartNew();
                    if (await Task.WhenAny(all, Task.Delay(30000)) != all)
                    {
                        OnLog?.Invoke("Timeout waiting for slaves");
                    }
                    waitSw.Stop();

                    // 3. Таймер расчета математики (BM25 + Cosine)
                    OnLog?.Invoke($"[Master] Slaves finished in {waitSw.ElapsedMilliseconds} ms. Building matrix...");
                    var calcSw = System.Diagnostics.Stopwatch.StartNew();

                    // ВАЖНО: Вызов твоего калькулятора (встроенного или вынесенного)
                    var matrix = Bm25Calculator.Calculate(textCounts.ToDictionary(k => k.Key, v => v.Value));
                    // Или var matrix = BuildSimilarityMatrix(...);

                    calcSw.Stop();
                    totalSw.Stop();

                    OnLog?.Invoke($"[Master] Matrix calc time: {calcSw.ElapsedMilliseconds} ms");
                    OnLog?.Invoke($"[Master] Total Request time: {totalSw.ElapsedMilliseconds} ms");

                    string statsMsg = $"Last Run: Slaves Wait: {waitSw.ElapsedMilliseconds}ms | " +
                                  $"Math Calc: {calcSw.ElapsedMilliseconds}ms | " +
                                  $"Total: {totalSw.ElapsedMilliseconds}ms";
                    OnStatsUpdate?.Invoke(statsMsg);

                    var resp = new SimilarityResponse { clientId = sub.clientId, matrix = matrix };
                    await TcpHelpers.SendJsonAsync(stream, resp);
                    OnLog?.Invoke($"Responded to client {sub.clientId}");
                    client.Close();
                    OnMatrixReady?.Invoke(matrix);
                    return;
                }
                client.Close();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("Connection error: " + ex.Message);
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
                        }
                        OnLog?.Invoke($"Received results from {slaveId}");
                    }
                }
            }
            catch { }
            finally
            {
                OnLog?.Invoke($"Slave disconnected: {slaveId}");
                slaves.TryRemove(slaveId, out _);
                lock (slaveLock) { slaveOrder.Remove(slaveId); }
                OnSlaveListChanged?.Invoke(slaveOrder.ToList());
            }
        }

        private async Task DispatchLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (taskQueue.IsEmpty || slaveOrder.Count == 0) { await Task.Delay(100, token); continue; }

                List<string> currentSlaves;
                lock (slaveLock) { currentSlaves = slaveOrder.ToList(); }
                if (currentSlaves.Count == 0) continue;

                var batch = new Dictionary<string, List<TextItem>>();
                foreach (var s in currentSlaves) batch[s] = new List<TextItem>();

                int i = 0;
                while (i < currentSlaves.Count * 5 && taskQueue.TryDequeue(out var item))
                {
                    batch[currentSlaves[i % currentSlaves.Count]].Add(item);
                    i++;
                }

                foreach (var kv in batch)
                {
                    if (kv.Value.Count == 0) continue;
                    if (slaves.TryGetValue(kv.Key, out var client))
                    {
                        var task = new TaskAssign { taskId = Guid.NewGuid().ToString(), texts = kv.Value.ToArray() };
                        _ = TcpHelpers.SendJsonAsync(client.GetStream(), task);
                    }
                }
                await Task.Delay(50, token);
            }
        }

        // --- АЛГОРИТМ BM25 ---
        //private Dictionary<string, Dictionary<string, double>> BuildBM25Matrix(string[] ids)
        //{
        //    // Коэффициенты BM25 (стандартные значения)
        //    double k1 = 1.2;
        //    double b = 0.75;

        //    var docFreq = new Dictionary<string, int>();
        //    var docLengths = new Dictionary<string, int>();
        //    long totalWords = 0;
        //    int N = ids.Length; // Количество документов

        //    // 1. Сбор глобальной статистики (DF и длины документов)
        //    var vocab = new HashSet<string>();
        //    foreach (var id in ids)
        //    {
        //        if (textCounts.TryGetValue(id, out var counts))
        //        {
        //            // Длина документа = сумма всех вхождений слов
        //            int len = counts.Values.Sum();
        //            docLengths[id] = len;
        //            totalWords += len;

        //            foreach (var word in counts.Keys)
        //            {
        //                vocab.Add(word);
        //                if (!docFreq.ContainsKey(word)) docFreq[word] = 0;
        //                docFreq[word]++;
        //            }
        //        }
        //        else
        //        {
        //            docLengths[id] = 0;
        //        }
        //    }

        //    double avgdl = N > 0 ? (double)totalWords / N : 1;
        //    var vocabList = vocab.ToList();
        //    var wordIdx = vocabList.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);

        //    // 2. Построение векторов весов BM25
        //    var vectors = new Dictionary<string, double[]>();

        //    foreach (var id in ids)
        //    {
        //        var vector = new double[vocabList.Count];
        //        if (textCounts.TryGetValue(id, out var counts))
        //        {
        //            int docLen = docLengths[id];
        //            foreach (var kv in counts)
        //            {
        //                string word = kv.Key;
        //                int tf = kv.Value; // Term Frequency (сколько раз слово в этом документе)

        //                if (wordIdx.TryGetValue(word, out int idx))
        //                {
        //                    // IDF (Inverse Document Frequency)
        //                    // Формула IDF для BM25 (с защитой от отрицательных значений)
        //                    double df = docFreq[word];
        //                    double idf = Math.Log((double)(N + 1) / (df + 1)) + 1.0;

        //                    // Формула BM25 для веса
        //                    double numerator = tf * (k1 + 1);
        //                    double denominator = tf + k1 * (1 - b + b * (docLen / avgdl));

        //                    // Итоговый вес слова в векторе
        //                    vector[idx] = idf * (numerator / denominator);
        //                }
        //            }
        //        }
        //        vectors[id] = vector;
        //    }

        //    // 3. Косинусное сходство полученных векторов
        //    var matrix = new Dictionary<string, Dictionary<string, double>>();

        //    // Переименовали переменную цикла 'a' -> 'idA'
        //    foreach (var idA in ids)
        //    {
        //        matrix[idA] = new Dictionary<string, double>();

        //        // Переименовали переменную цикла 'b' -> 'idB', так как 'b' занято коэффициентом BM25
        //        foreach (var idB in ids)
        //        {
        //            matrix[idA][idB] = Cosine(vectors[idA], vectors[idB]);
        //        }
        //    }
        //    return matrix;
        //}

        private double Cosine(double[] a, double[] b)
        {
            double dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            if (magA == 0 || magB == 0) return 0;
            return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
        }
    }
}
