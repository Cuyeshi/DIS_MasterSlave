using System.Text.RegularExpressions;

namespace MasterSlave.Backend
{
    // Упрощенный стеммер Портера для русского языка
    public static class SimpleRussianStemmer
    {
        private static readonly Regex PerfectiveGerund = new Regex("(в|вши|вшись)$");
        private static readonly Regex Adjective = new Regex("(ее|ие|ые|ое|ими|ыми|ей|ий|ый|ой|ем|им|ым|ом|его|ого|ему|ому|их|ых|ую|юю|ая|яя|ою|ею)$");
        private static readonly Regex Participle = new Regex("(ем|нн|вш|ющ|щ)");
        private static readonly Regex Reflexive = new Regex("(ся|сь)$");
        private static readonly Regex Verb = new Regex("(ила|ыла|ена|ейте|уйте|ите|или|ыли|ей|уй|ил|ыл|им|ым|ен|ило|ыло|ено|ят|ует|уют|ит|ыт|ены|ить|ыть|ишь|ую|ю)$");
        private static readonly Regex Noun = new Regex("(а|ев|ов|ие|ье|е|иями|ями|ами|еи|ии|и|ией|ей|ой|ий|й|иям|ям|ием|ем|ам|ом|о|у|ах|иях|ях|ы|ь|ию|ью|ю|ия|ья|я)$");
        private static readonly Regex Superlative = new Regex("(ейш|ейше)$");
        private static readonly Regex Derivational = new Regex("(ост|ость)$");

        

        public static string Stem(string word)
        {
            word = word.ToLowerInvariant().Replace('ё', 'е');
            var match = Regex.Match(word, @"^(.*?[аеиоуыэюя])(.*)$");
            if (match.Success)
            {
                var rv = match.Groups[2].Value;
                var head = match.Groups[1].Value;

                string temp = rv;
                if (!Replace(ref temp, PerfectiveGerund, ""))
                {
                    if (!Replace(ref temp, Reflexive, "")) { }
                    if (Replace(ref temp, Adjective, ""))
                    {
                        Replace(ref temp, Participle, "");
                    }
                    else
                    {
                        if (!Replace(ref temp, Verb, ""))
                            Replace(ref temp, Noun, "");
                    }
                }
                Replace(ref temp, "и$", "");
                if (Replace(ref temp, Derivational, "")) { }
                if (Replace(ref temp, "нн$", "н")) { }
                if (Replace(ref temp, Superlative, "")) { }

                return head + temp;
            }
            return word;
        }

        private static bool Replace(ref string str, Regex regex, string replacement)
        {
            if (regex.IsMatch(str))
            {
                str = regex.Replace(str, replacement);
                return true;
            }
            return false;
        }
        private static bool Replace(ref string str, string pattern, string replacement)
        {
            if (Regex.IsMatch(str, pattern))
            {
                str = Regex.Replace(str, pattern, replacement);
                return true;
            }
            return false;
        }
    }
}