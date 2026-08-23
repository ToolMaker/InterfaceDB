using System.Globalization;
using Lifti;
using Lifti.Querying;
using Lifti.Querying.QueryParts;
using Lifti.Tokenization;
using Lifti.Tokenization.TextExtraction;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LiftiToken = Lifti.Tokenization.Token;

namespace ContainsIndexBenchmarks;

internal sealed class LiftiWildcardIndex : IContainsIndex
{
    private sealed class WholeValueTokenizer : IIndexTokenizer
    {
        public IReadOnlyCollection<LiftiToken> Process(IEnumerable<DocumentTextFragment> fragments)
        {
            var tokens = new List<LiftiToken>();
            var tokenIndex = 0;
            foreach (var fragment in fragments)
            {
                if (fragment.Text.IsEmpty)
                {
                    continue;
                }

                tokens.Add(new LiftiToken(
                    fragment.Text.ToString(),
                    new TokenLocation(tokenIndex++, fragment.Offset, checked((ushort)fragment.Text.Length))));
            }

            return tokens;
        }

        public IReadOnlyCollection<LiftiToken> Process(ReadOnlySpan<char> input)
        {
            if (input.IsEmpty)
            {
                return [];
            }

            return
            [
                new LiftiToken(input.ToString(), new TokenLocation(0, 0, checked((ushort)input.Length)))
            ];
        }

        public string Normalize(ReadOnlySpan<char> input) => input.ToString();

        public bool IsSplitCharacter(char character) => false;
    }

    private FullTextIndex<int>? _index;
    private string[] _values = [];

    public string Name => "LIFTI whole-token wildcard";

    public bool SupportsIncrementalMutation => true;

    public void Build(string[] values)
    {
        _index?.Dispose();
        _values = (string[])values.Clone();
        _index = new FullTextIndexBuilder<int>()
            .WithDuplicateKeyBehavior(DuplicateKeyBehavior.Replace)
            .WithDefaultTokenization(static builder =>
                builder.WithFactory(static _ => new WholeValueTokenizer()))
            .Build();

        _index.BeginBatchChange();
        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            _index.AddAsync(documentId, values[documentId]).GetAwaiter().GetResult();
        }

        _index.CommitBatchChangeAsync().GetAwaiter().GetResult();
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length == 0)
        {
            return QueryStats.All(_values.Length);
        }

        var query = new Lifti.Querying.Query(new WildcardQueryPart(
        [
            WildcardQueryFragment.MultiCharacter,
            WildcardQueryFragment.CreateText(needle),
            WildcardQueryFragment.MultiCharacter
        ]));

        var results = _index!.Search(query);
        var digest = MatchDigest.Empty;
        var candidateCount = 0;
        foreach (var result in results)
        {
            candidateCount++;
            if (_values[result.Key].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(result.Key);
            }
        }

        return new QueryStats(digest, candidateCount);
    }

    public void Upsert(int documentId, string value)
    {
        _index!.AddAsync(documentId, value).GetAwaiter().GetResult();
        _values[documentId] = value;
    }

    public void Remove(int documentId)
    {
        _index!.RemoveAsync(documentId).GetAwaiter().GetResult();
        _values[documentId] = string.Empty;
    }

    public void Dispose()
    {
        _index?.Dispose();
        _index = null;
    }
}

internal sealed class LuceneNGramIndex : IContainsIndex
{
    private const string GramField = "grams";
    private const string IdField = "id";
    private static readonly LuceneVersion Version = LuceneVersion.LUCENE_48;

    private sealed class ExactTrigramAnalyzer : Analyzer
    {
        protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
        {
            var tokenizer = new KeywordTokenizer(reader);
            var grams = new NGramTokenFilter(Version, tokenizer, 3, 3);
            return new TokenStreamComponents(tokenizer, grams);
        }
    }

    private Lucene.Net.Store.Directory? _directory;
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;
    private int[] _luceneToExternal = [];
    private string[] _values = [];

    public string Name => "Lucene.Net exact trigram terms";

    public void Build(string[] values)
    {
        Dispose();
        _values = (string[])values.Clone();
        _directory = new RAMDirectory();
        using var analyzer = new ExactTrigramAnalyzer();
        var configuration = new IndexWriterConfig(Version, analyzer)
        {
            OpenMode = OpenMode.CREATE
        };

        using (var writer = new IndexWriter(_directory, configuration))
        {
            for (var documentId = 0; documentId < values.Length; documentId++)
            {
                var document = new Document
                {
                    new StoredField(IdField, documentId),
                    new TextField(GramField, values[documentId], Field.Store.NO)
                };
                writer.AddDocument(document);
            }

            writer.Commit();
        }

        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader);
        _luceneToExternal = new int[_reader.MaxDoc];
        for (var luceneId = 0; luceneId < _reader.MaxDoc; luceneId++)
        {
            _luceneToExternal[luceneId] = _reader.Document(luceneId).GetField(IdField).GetInt32Value()!.Value;
        }

        BooleanQuery.MaxClauseCount = Math.Max(BooleanQuery.MaxClauseCount, 4096);
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return ContainsOracle.Query(_values, needle);
        }

        var query = new BooleanQuery();
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= needle.Length - 3; i++)
        {
            grams.Add(needle.Substring(i, 3));
        }

        foreach (var gram in grams)
        {
            query.Add(new TermQuery(new Term(GramField, gram)), Occur.MUST);
        }

        var hits = _searcher!.Search(query, _values.Length).ScoreDocs;
        var digest = MatchDigest.Empty;
        foreach (var hit in hits)
        {
            var documentId = _luceneToExternal[hit.Doc];
            if (_values[documentId].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, hits.Length);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _directory?.Dispose();
        _reader = null;
        _directory = null;
        _searcher = null;
        _luceneToExternal = [];
    }
}
