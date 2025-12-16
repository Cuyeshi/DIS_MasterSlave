using MasterSlave.Backend;
using System.Collections.Generic;
using System.Linq;

namespace MasterSlave.Backend
{
    public static class TextProcessor
    {
        public static Dictionary<string, int> ParseText(string text, bool useBigrams = false)
        {
            var dict = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

            var rawTokens = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"\p{L}+")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Value)
                .ToList();

            if (rawTokens.Count == 0) return dict;

            var stemmedTokens = rawTokens.Select(w => SimpleRussianStemmer.Stem(w)).ToList();

            // Униграммы
            foreach (var token in stemmedTokens)
            {
                if (!dict.ContainsKey(token)) dict[token] = 0;
                dict[token]++;
            }

            // Биграммы (опционально)
            if (useBigrams)
            {
                for (int i = 0; i < stemmedTokens.Count - 1; i++)
                {
                    string bigram = stemmedTokens[i] + "_" + stemmedTokens[i + 1];
                    if (!dict.ContainsKey(bigram)) dict[bigram] = 0;
                    dict[bigram]++;
                }
            }
            return dict;
        }
    }
}