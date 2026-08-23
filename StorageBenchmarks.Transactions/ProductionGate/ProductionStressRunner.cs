using System.Collections.Concurrent;
using System.Diagnostics;

internal static class ProductionStressRunner
{
    public static async ValueTask<GateStressMetric> RunAsync(
        IProductionGateStore store,
        int concurrency,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var initial = await store.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var expected = new ConcurrentDictionary<string, GateRecord>(initial, StringComparer.Ordinal);
        var stableIds = initial.Keys.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var insertedIds = new ConcurrentQueue<string>();
        var stripes = Enumerable.Range(0, 4_096).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();
        var failures = new ConcurrentQueue<Exception>();
        var runToken = Guid.NewGuid().ToString("N")[..8];
        long insertSequence = 0;

        using var durationSource = new CancellationTokenSource(duration);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            durationSource.Token,
            cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => Task.Run(async () =>
            {
                var random = new Random(unchecked(Environment.TickCount * 397) ^ worker);
                var stats = new WorkerStressStats();
                while (!linkedSource.IsCancellationRequested)
                {
                    var operation = random.Next(100);
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        if (operation < 70)
                        {
                            var queryKind = (operation % 6) switch
                            {
                                0 => GateQueryKind.Exact,
                                1 => GateQueryKind.Range,
                                2 => GateQueryKind.Prefix,
                                3 => GateQueryKind.Suffix,
                                4 => GateQueryKind.Contains,
                                _ => GateQueryKind.BroadConjunction
                            };
                            var exactId = stableIds[random.Next(stableIds.Length)];
                            _ = await store.QueryAsync(queryKind, exactId).ConfigureAwait(false);
                            stats.Reads++;
                        }
                        else if (operation < 85)
                        {
                            var sequence = Interlocked.Increment(ref insertSequence);
                            var record = GateRecord.Create((int)(sequence % int.MaxValue), $"stress-{runToken}");
                            await store.InsertAsync(record).ConfigureAwait(false);
                            if (!expected.TryAdd(record.Id, record))
                            {
                                throw new InvalidOperationException($"Stress insert generated duplicate id '{record.Id}'.");
                            }

                            insertedIds.Enqueue(record.Id);
                            stats.Inserts++;
                        }
                        else if (operation < 95)
                        {
                            var id = stableIds[random.Next(stableIds.Length)];
                            var stripe = stripes[GetStripe(id, stripes.Length)];
                            await stripe.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (!expected.TryGetValue(id, out var current))
                                {
                                    continue;
                                }

                                var revision = current.Revision + 1;
                                var updated = current with
                                {
                                    Age = current.Age == 80 ? 18 : current.Age + 1,
                                    Content = revision % 2 == 0
                                        ? $"updated revision {revision} with rare-needle"
                                        : $"updated revision {revision} without marker",
                                    Revision = revision
                                };
                                if (!await store.UpdateAsync(updated).ConfigureAwait(false))
                                {
                                    throw new InvalidOperationException($"Stress update could not find '{id}'.");
                                }

                                expected[id] = updated;
                                stats.Updates++;
                            }
                            finally
                            {
                                stripe.Release();
                            }
                        }
                        else if (insertedIds.TryDequeue(out var id))
                        {
                            var stripe = stripes[GetStripe(id, stripes.Length)];
                            await stripe.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (expected.ContainsKey(id))
                                {
                                    if (!await store.DeleteAsync(id).ConfigureAwait(false))
                                    {
                                        throw new InvalidOperationException($"Stress delete could not find '{id}'.");
                                    }

                                    expected.TryRemove(id, out _);
                                    stats.Deletes++;
                                }
                            }
                            finally
                            {
                                stripe.Release();
                            }
                        }
                        else
                        {
                            var exactId = stableIds[random.Next(stableIds.Length)];
                            _ = await store.QueryAsync(GateQueryKind.Exact, exactId).ConfigureAwait(false);
                            stats.Reads++;
                        }

                        stats.Latency.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds, random);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failures.Enqueue(exception);
                        if (failures.Count >= 20)
                        {
                            linkedSource.Cancel();
                        }
                    }
                }

                return stats;
            }, CancellationToken.None))
            .ToArray();

        var workerStats = await Task.WhenAll(workers).ConfigureAwait(false);
        stopwatch.Stop();
        foreach (var stripe in stripes)
        {
            stripe.Dispose();
        }

        var actual = await store.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var integrityPassed = AreEquivalent(expected, actual, out var integrityError);
        if (!integrityPassed && integrityError is not null)
        {
            failures.Enqueue(new InvalidOperationException(integrityError));
        }

        var samples = workerStats.SelectMany(static stats => stats.Latency.Samples).Order().ToArray();
        var reads = workerStats.Sum(static stats => stats.Reads);
        var inserts = workerStats.Sum(static stats => stats.Inserts);
        var updates = workerStats.Sum(static stats => stats.Updates);
        var deletes = workerStats.Sum(static stats => stats.Deletes);
        var operations = reads + inserts + updates + deletes;

        if (!failures.IsEmpty)
        {
            Console.Error.WriteLine($"{store.EngineName} stress failures:");
            foreach (var failure in failures.Take(5))
            {
                Console.Error.WriteLine(failure.Message);
            }
        }

        return new GateStressMetric(
            store.EngineName,
            initial.Count,
            store.CacheModeName,
            store.DurabilityModeName,
            concurrency,
            stopwatch.Elapsed.TotalSeconds,
            operations,
            stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : operations / stopwatch.Elapsed.TotalSeconds,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            reads,
            inserts,
            updates,
            deletes,
            actual.Count,
            failures.Count,
            integrityPassed);
    }

    private static int GetStripe(string id, int length) =>
        (id.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % length;

    private static bool AreEquivalent(
        IReadOnlyDictionary<string, GateRecord> expected,
        IReadOnlyDictionary<string, GateRecord> actual,
        out string? error)
    {
        if (expected.Count != actual.Count)
        {
            error = $"Integrity count mismatch: expected {expected.Count}, found {actual.Count}.";
            return false;
        }

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var candidate))
            {
                error = $"Integrity validation did not find '{pair.Key}'.";
                return false;
            }

            if (pair.Value != candidate)
            {
                error = $"Integrity validation found different data for '{pair.Key}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    internal static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Count == 0)
        {
            return 0;
        }

        var rank = percentile * (sortedSamples.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedSamples[lower];
        }

        var weight = rank - lower;
        return sortedSamples[lower] + (sortedSamples[upper] - sortedSamples[lower]) * weight;
    }

    private sealed class WorkerStressStats
    {
        public long Reads;
        public long Inserts;
        public long Updates;
        public long Deletes;
        public ReservoirSampler Latency { get; } = new(100_000);
    }

    private sealed class ReservoirSampler
    {
        private readonly int _capacity;
        private long _observed;

        public ReservoirSampler(int capacity)
        {
            _capacity = capacity;
        }

        public List<double> Samples { get; } = [];

        public void Add(double value, Random random)
        {
            var observed = ++_observed;
            if (Samples.Count < _capacity)
            {
                Samples.Add(value);
                return;
            }

            var replacement = random.NextInt64(observed);
            if (replacement < _capacity)
            {
                Samples[(int)replacement] = value;
            }
        }
    }
}
