using System.Globalization;
using System.Text;

namespace ContainsIndexBenchmarks;

internal sealed record QuerySpec(string Name, string Needle);

internal sealed record CorpusDataset(string Name, string[] Values, QuerySpec[] Queries)
{
    public long CharacterCount => Values.Sum(static value => (long)value.Length);
}

internal enum CorpusKind
{
    Random,
    NamesAndEmails,
    Urls,
    Logs,
    Repetitive,
    Unicode
}

internal static class CorpusFactory
{
    private const string RareMarker = "Qz7Kp9Mx";
    private const string MediumMarker = "Vv8Jr2Ls";
    private const string LongMarker = "Xq4Nz8Hm2Pk7Rt5W";
    private const string AbsentMarker = "\uE000\uE001\uE002";

    private static readonly string[] FirstNames =
    [
        "alice", "bob", "charlie", "diana", "ernest", "fatima", "grace", "hassan",
        "isabel", "james", "kabelo", "lerato", "maria", "nathan", "olivia", "peter"
    ];

    private static readonly string[] LastNames =
    [
        "smith", "nguyen", "patel", "dlamini", "williams", "muller", "garcia", "kim",
        "naidoo", "brown", "mokoena", "wilson", "khan", "martin", "singh", "jones"
    ];

    private static readonly string[] Domains =
    [
        "example.com", "interface.dev", "contoso.net", "northwind.io", "radixforge.org",
        "fabrikam.co.za", "adventure-works.com", "example.org"
    ];

    private static readonly string[] UnicodePieces =
    [
        "東京", "数据库", "αλγόριθμος", "данные", "مرحبا", "שלום", "café", "naïve",
        "👩\u200D💻", "🚀", "e\u0301", "𝄞", "🙂", "résumé", "Straße", "हिन्दी"
    ];

    public static CorpusDataset Create(CorpusKind kind, int count, int averageLength, int seed = 1447)
    {
        var random = new Random(seed + ((int)kind * 7919) + count + averageLength);
        var values = new string[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = kind switch
            {
                CorpusKind.Random => RandomAscii(random, JitterLength(random, averageLength)),
                CorpusKind.NamesAndEmails => NameAndEmail(random, i, averageLength),
                CorpusKind.Urls => Url(random, i, averageLength),
                CorpusKind.Logs => LogLine(random, i, averageLength),
                CorpusKind.Repetitive => Repetitive(random, i, averageLength),
                CorpusKind.Unicode => UnicodeText(random, i, averageLength),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        Inject(values, RareMarker, divisor: 1000, offset: 17);
        Inject(values, MediumMarker, divisor: 10, offset: 3);
        Inject(values, LongMarker, divisor: 100, offset: 41);

        var broadNeedle = kind switch
        {
            CorpusKind.NamesAndEmails => ".com",
            CorpusKind.Urls => "https://",
            CorpusKind.Logs => "INFO",
            CorpusKind.Repetitive => "aaaa",
            CorpusKind.Unicode => "東京",
            _ => "abc"
        };

        var shortNeedle = kind switch
        {
            CorpusKind.Repetitive => "aa",
            CorpusKind.Unicode => "東",
            CorpusKind.Logs => "IN",
            _ => "er"
        };

        return new CorpusDataset(
            $"{kind.ToString().ToLowerInvariant()}-{count.ToString(CultureInfo.InvariantCulture)}x{averageLength.ToString(CultureInfo.InvariantCulture)}",
            values,
            [
                new QuerySpec("empty", string.Empty),
                new QuerySpec("one-char", shortNeedle[..1]),
                new QuerySpec("two-char", shortNeedle),
                new QuerySpec("absent-3", AbsentMarker),
                new QuerySpec("rare-8", RareMarker),
                new QuerySpec("one-percent-16", LongMarker),
                new QuerySpec("ten-percent-8", MediumMarker),
                new QuerySpec("natural-broad", broadNeedle)
            ]);
    }

    public static CorpusDataset CreateValidation(int count = 512)
    {
        var datasets = Enum.GetValues<CorpusKind>()
            .Select(kind => Create(kind, count, 72, seed: 8821))
            .ToArray();

        var values = datasets.SelectMany(static dataset => dataset.Values).ToArray();
        values[0] = string.Empty;
        values[1] = "a";
        values[2] = "ab";
        values[3] = "abcabc";
        values[4] = "\0embedded-null";
        values[5] = "high\uD83D\uDE80surrogate";
        values[6] = "e\u0301 and é are ordinally different";
        values[7] = "line one\r\nline two";

        return new CorpusDataset(
            "validation-mixed",
            values,
            [
                new QuerySpec("empty", string.Empty),
                new QuerySpec("a", "a"),
                new QuerySpec("ab", "ab"),
                new QuerySpec("abc", "abc"),
                new QuerySpec("overlap", "bca"),
                new QuerySpec("absent", "\uE010\uE011\uE012"),
                new QuerySpec("embedded-null", "\0emb"),
                new QuerySpec("surrogate-pair", "\uD83D\uDE80"),
                new QuerySpec("combining", "e\u0301"),
                new QuerySpec("precomposed", "é"),
                new QuerySpec("cross-space", "one\r\nline"),
                new QuerySpec("rare", RareMarker),
                new QuerySpec("medium", MediumMarker),
                new QuerySpec("long", LongMarker)
            ]);
    }

    private static int JitterLength(Random random, int averageLength)
    {
        var minimum = Math.Max(1, averageLength / 2);
        var maximum = Math.Max(minimum + 1, averageLength + (averageLength / 2));
        return random.Next(minimum, maximum + 1);
    }

    private static string RandomAscii(Random random, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789_-./";
        return string.Create(length, random, static (span, rng) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[rng.Next(alphabet.Length)];
            }
        });
    }

    private static string NameAndEmail(Random random, int id, int targetLength)
    {
        var first = FirstNames[random.Next(FirstNames.Length)];
        var last = LastNames[random.Next(LastNames.Length)];
        var domain = Domains[random.Next(Domains.Length)];
        var value = $"{first} {last} <{first}.{last}{id % 10000:D4}@{domain}>";
        return Pad(random, value, targetLength, ' ');
    }

    private static string Url(Random random, int id, int targetLength)
    {
        var domain = Domains[random.Next(Domains.Length)];
        var value = $"https://{domain}/api/v{1 + (id % 4)}/customers/{id}/orders/{random.Next(1_000_000):D6}?region=za&active=true";
        return Pad(random, value, targetLength, '/');
    }

    private static string LogLine(Random random, int id, int targetLength)
    {
        string[] levels = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR"];
        string[] operations = ["load", "save", "query", "compact", "replicate", "checkpoint"];
        var value = $"2026-08-23T12:{id % 60:D2}:{random.Next(60):D2}.{random.Next(1000):D3}+02:00 {levels[id % levels.Length]} shard={id % 32:D2} operation={operations[random.Next(operations.Length)]} duration={random.Next(1, 5000)}ms correlation={id:X8}";
        return Pad(random, value, targetLength, ' ');
    }

    private static string Repetitive(Random random, int id, int targetLength)
    {
        var builder = new StringBuilder(targetLength + 32);
        var motifs = new[] { "aaaaaaaaab", "ababababac", "mississippi", "bananabanana", "0000000001" };
        while (builder.Length < targetLength)
        {
            builder.Append(motifs[(id + random.Next(motifs.Length)) % motifs.Length]);
        }

        return builder.ToString(0, targetLength);
    }

    private static string UnicodeText(Random random, int id, int targetLength)
    {
        var builder = new StringBuilder(targetLength + 32);
        while (builder.Length < targetLength)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(UnicodePieces[(id + random.Next(UnicodePieces.Length)) % UnicodePieces.Length]);
        }

        if (builder.Length > targetLength)
        {
            builder.Length = targetLength;
            if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
            {
                builder.Length--;
            }
        }

        return builder.ToString();
    }

    private static string Pad(Random random, string value, int targetLength, char separator)
    {
        if (value.Length >= targetLength)
        {
            return value;
        }

        var builder = new StringBuilder(targetLength);
        builder.Append(value);
        while (builder.Length < targetLength)
        {
            builder.Append(separator);
            builder.Append(RandomAscii(random, Math.Min(12, targetLength - builder.Length)));
        }

        if (builder.Length > targetLength)
        {
            builder.Length = targetLength;
        }

        return builder.ToString();
    }

    private static void Inject(string[] values, string marker, int divisor, int offset)
    {
        if (values.Length == 0)
        {
            return;
        }

        var targetCount = Math.Max(1, values.Length / divisor);
        var step = Math.Max(1, values.Length / targetCount);
        for (var hit = 0; hit < targetCount; hit++)
        {
            var index = (offset + (hit * step)) % values.Length;
            values[index] += marker;
        }
    }
}
