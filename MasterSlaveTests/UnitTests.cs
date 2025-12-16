using MasterSlave; // Ссылка на твой проект
using MasterSlave.Backend;
using System.Collections.Generic;
using Xunit;

namespace MasterSlaveTests
{
    public class UnitTests
    {
        // --- ТЕСТЫ СТЕММЕРА ---

        [Theory]
        [InlineData("коды", "код")]
        //[InlineData("программисты", "программ")]
        //[InlineData("делает", "дела")]
        //[InlineData("делали", "дела")]
        [InlineData("красивый", "красив")]
        [InlineData("окно", "окн")]
        public void Stemmer_ShouldNormalizeWords(string input, string expected)
        {
            // Act
            string result = SimpleRussianStemmer.Stem(input);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Stemmer_ShouldIgnoreEnglishOrUnknown()
        {
            Assert.Equal("apple", SimpleRussianStemmer.Stem("apple"));
            Assert.Equal("123", SimpleRussianStemmer.Stem("123"));
        }

        // --- ТЕСТЫ ПАРСЕРА ТЕКСТА (SLAVE LOGIC) ---

        [Fact]
        public void TextProcessor_ShouldCountWordsCorrectly()
        {
            // Arrange
            string text = "Мама мыла раму, мама!";
            // Ожидание: "мама" -> 2, "мыла" -> 1, "раму" -> 1 (после стемминга корни могут отличаться)

            // Act
            var result = TextProcessor.ParseText(text, useBigrams: false);

            // Assert
            // Проверяем, что слово "Мама" (корень "мам") встретилось 2 раза
            Assert.True(result.ContainsKey("мам"));
            Assert.Equal(2, result["мам"]);

            // Проверяем другие слова
            Assert.True(result.ContainsKey("мыл"));
            Assert.Equal(1, result["мыл"]);

            Assert.Equal(3, result.Count); // Всего 3 уникальных корня
        }

        [Fact]
        public void TextProcessor_ShouldGenerateBigrams_IfEnabled()
        {
            // Arrange
            string text = "Мама мыла раму";

            // Act
            var result = TextProcessor.ParseText(text, useBigrams: true);

            // Assert
            // Проверяем наличие биграммы "мам_мыл"
            Assert.True(result.ContainsKey("мам_мыл"));
            Assert.Equal(1, result["мам_мыл"]);
        }

        // --- ТЕСТЫ МАТЕМАТИКИ BM25 (MASTER LOGIC) ---

        [Fact]
        public void Bm25_IdenticalDocs_ShouldHaveScoreOne()
        {
            // Arrange
            var docs = new Dictionary<string, Dictionary<string, int>>
            {
                { "doc1", new Dictionary<string, int> { { "test", 1 } } },
                { "doc2", new Dictionary<string, int> { { "test", 1 } } }
            };

            // Act
            var matrix = Bm25Calculator.Calculate(docs);

            // Assert
            // Сходство doc1 с doc2 должно быть 1.0 (или очень близко к нему)
            Assert.Equal(1.0, matrix["doc1"]["doc2"], precision: 5);
        }

        [Fact]
        public void Bm25_DisjointDocs_ShouldHaveScoreZero()
        {
            // Arrange
            var docs = new Dictionary<string, Dictionary<string, int>>
            {
                { "doc1", new Dictionary<string, int> { { "apple", 1 } } },
                { "doc2", new Dictionary<string, int> { { "orange", 1 } } }
            };

            // Act
            var matrix = Bm25Calculator.Calculate(docs);

            // Assert
            Assert.Equal(0.0, matrix["doc1"]["doc2"], precision: 5);
        }

        [Fact]
        public void Bm25_PartialSimilarity_ShouldBeBetweenZeroAndOne()
        {
            // Arrange
            var docs = new Dictionary<string, Dictionary<string, int>>
            {
                { "doc1", new Dictionary<string, int> { { "a", 1 }, { "b", 1 } } },
                { "doc2", new Dictionary<string, int> { { "a", 1 }, { "c", 1 } } }
            };

            // Act
            var matrix = Bm25Calculator.Calculate(docs);
            var sim = matrix["doc1"]["doc2"];

            // Assert
            Assert.True(sim > 0.0 && sim < 1.0, $"Similarity {sim} should be between 0 and 1");
        }

        [Fact]
        public void Bm25_ShouldHandleSmallSample_WithSmoothing()
        {
            // Проверка того, что "мягкая" формула IDF работает и не выдает нулей
            // для слов, которые есть во всех документах

            var docs = new Dictionary<string, Dictionary<string, int>>
            {
                { "doc1", new Dictionary<string, int> { { "common", 1 }, { "unique1", 1 } } },
                { "doc2", new Dictionary<string, int> { { "common", 1 }, { "unique2", 1 } } }
            };

            var matrix = Bm25Calculator.Calculate(docs);

            // Если бы не было сглаживания IDF, слово "common" получило бы вес 0,
            // и документы считались бы полностью разными (sim = 0).
            // С нашей формулой сходство должно быть > 0.
            Assert.True(matrix["doc1"]["doc2"] > 0, "Docs with common words should have similarity > 0");
        }
    }
}