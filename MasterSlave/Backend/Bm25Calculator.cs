using System;
using System.Collections.Generic;
using System.Linq;

namespace MasterSlave.Backend
{
    public static class Bm25Calculator
    {
        public static Dictionary<string, Dictionary<string, double>> Calculate(Dictionary<string, Dictionary<string, int>> docs)
        {
            // Коэффициенты
            double k1 = 1.2;
            double b = 0.75;

            var ids = docs.Keys.ToArray();
            var N = ids.Length;

            var docFreq = new Dictionary<string, int>();
            var docLengths = new Dictionary<string, int>();
            long totalWords = 0;

            // 1. Статистика
            foreach (var id in ids)
            {
                var counts = docs[id];
                int len = counts.Values.Sum();
                docLengths[id] = len;
                totalWords += len;

                foreach (var word in counts.Keys)
                {
                    if (!docFreq.ContainsKey(word)) docFreq[word] = 0;
                    docFreq[word]++;
                }
            }

            double avgdl = N > 0 ? (double)totalWords / N : 1;

            // Собираем словарь всех слов
            var vocab = docFreq.Keys.ToList();
            var wordIdx = vocab.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);
            var vectors = new Dictionary<string, double[]>();

            // 2. Векторизация
            foreach (var id in ids)
            {
                var vector = new double[vocab.Count];
                var counts = docs[id];
                int docLen = docLengths[id];

                foreach (var kv in counts)
                {
                    string word = kv.Key;
                    int tf = kv.Value;

                    if (wordIdx.TryGetValue(word, out int idx))
                    {
                        // Сглаженный IDF
                        double df = docFreq[word];
                        double idf = Math.Log((double)(N + 1) / (df + 1)) + 1.0;

                        double numerator = tf * (k1 + 1);
                        double denominator = tf + k1 * (1 - b + b * (docLen / avgdl));

                        vector[idx] = idf * (numerator / denominator);
                    }
                }
                vectors[id] = vector;
            }

            // 3. Косинусное сходство
            var matrix = new Dictionary<string, Dictionary<string, double>>();
            foreach (var idA in ids)
            {
                matrix[idA] = new Dictionary<string, double>();
                foreach (var idB in ids)
                {
                    matrix[idA][idB] = Cosine(vectors[idA], vectors[idB]);
                }
            }
            return matrix;
        }

        private static double Cosine(double[] a, double[] b)
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