using System.Threading.Channels;
using Idb.Storage.SegmentCore;

namespace Idb.Storage.SegmentCore.ObjectLog;

public sealed class StripedObjectLogProbeWriter : IAsyncDisposable
{
    private readonly StripedObjectLogProbeOptions _options;
    private readonly StripeWorker[] _stripes;
    private int _disposed;

    private StripedObjectLogProbeWriter(
        StripedObjectLogProbeOptions options,
        StripeWorker[] stripes)
    {
        _options = options;
        _stripes = stripes;
    }

    public int StripeCount => _stripes.Length;

    public IReadOnlyList<ObjectLogWritePosition> GetWritePositions() =>
        _stripes.Select(static stripe => stripe.WritePosition).ToArray();

    public long RepairedTailBytesOnOpen => _stripes.Sum(static stripe => stripe.RepairedTailBytesOnOpen);

    public StripedObjectLogStatistics Statistics
    {
        get
        {
            long submitted = 0;
            long batches = 0;
            long flushes = 0;
            long backpressured = 0;
            long rejected = 0;
            var currentOutstanding = 0;
            var largestBatch = 0;
            var peakOutstandingPerStripe = 0;
            foreach (var stripe in _stripes)
            {
                var statistics = stripe.Statistics;
                submitted += statistics.SubmittedRecords;
                batches += statistics.DurableBatches;
                flushes += statistics.DurabilityFlushes;
                backpressured += statistics.BackpressuredRecords;
                rejected += statistics.RejectedRecords;
                currentOutstanding += statistics.CurrentOutstandingWrites;
                largestBatch = Math.Max(largestBatch, statistics.LargestBatch);
                peakOutstandingPerStripe = Math.Max(
                    peakOutstandingPerStripe,
                    statistics.PeakOutstandingWritesPerStripe);
            }

            return new StripedObjectLogStatistics(
                submitted,
                batches,
                flushes,
                largestBatch,
                backpressured,
                rejected,
                currentOutstanding,
                peakOutstandingPerStripe);
        }
    }

    public static async ValueTask<StripedObjectLogProbeWriter> OpenAsync(
        string rootDirectory,
        StripedObjectLogProbeOptions? options = null,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(
            rootDirectory,
            options,
            durableFileSystem,
            recoveryPositions: null,
            replayRecord: null,
            cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<StripedObjectLogProbeWriter> OpenFromCheckpointAsync(
        string rootDirectory,
        StripedObjectLogProbeOptions options,
        IReadOnlyList<ObjectLogWritePosition> recoveryPositions,
        Action<int, ObjectLogReplayRecord> replayRecord,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(
            rootDirectory,
            options,
            durableFileSystem,
            recoveryPositions,
            replayRecord,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<StripedObjectLogProbeWriter> OpenCoreAsync(
        string rootDirectory,
        StripedObjectLogProbeOptions? options,
        IDurableFileSystem? durableFileSystem,
        IReadOnlyList<ObjectLogWritePosition>? recoveryPositions,
        Action<int, ObjectLogReplayRecord>? replayRecord,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new StripedObjectLogProbeOptions();
        options.Validate();
        if (recoveryPositions is not null && recoveryPositions.Count != options.StripeCount)
        {
            throw new ArgumentException(
                "Checkpoint write-position count must equal the configured stripe count.",
                nameof(recoveryPositions));
        }

        durableFileSystem ??= PlatformDurableFileSystem.Instance;
        var fullRoot = Path.GetFullPath(rootDirectory);
        _ = StripedObjectLogManifest.ReadOrCreate(fullRoot, options, durableFileSystem);
        var stripes = new StripeWorker[options.StripeCount];
        var replayGate = new object();
        Action<int, ObjectLogReplayRecord>? synchronizedReplay = replayRecord is null
            ? null
            : (stripe, record) =>
            {
                lock (replayGate)
                {
                    replayRecord(stripe, record);
                }
            };
        var openTasks = new Task<StripeWorker>[options.StripeCount];
        var launchedTasks = 0;
        try
        {
            for (var stripe = 0; stripe < stripes.Length; stripe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                openTasks[stripe] = StripeWorker.OpenAsync(
                    stripe,
                    StripedObjectLogRecovery.StripeRoot(fullRoot, stripe),
                    options,
                    durableFileSystem,
                    recoveryPositions is null ? null : recoveryPositions[stripe],
                    synchronizedReplay,
                    cancellationToken).AsTask();
                launchedTasks++;
            }

            stripes = await Task.WhenAll(openTasks).ConfigureAwait(false);
            return new StripedObjectLogProbeWriter(options, stripes);
        }
        catch (Exception openException)
        {
            for (var index = 0; index < launchedTasks; index++)
            {
                StripeWorker? worker = null;
                try
                {
                    // A cancellation or synchronous launch failure can leave previously
                    // launched stripe opens in flight. Drain each task before releasing any
                    // successful writer so no lease can escape after this method returns.
                    worker = await openTasks[index].ConfigureAwait(false);
                }
                catch
                {
                    // Observe sibling failures while preserving the original open exception.
                }

                if (worker is null)
                {
                    continue;
                }

                try
                {
                    await worker.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    try
                    {
                        openException.Data[$"Striped object-log cleanup failure: stripe {index}"] =
                            cleanupException;
                    }
                    catch
                    {
                        // Cleanup diagnostics must never replace the original open failure.
                    }
                }
            }

            throw;
        }
    }

    public ValueTask<StripedObjectLogRecordAddress> AppendAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return AppendValidatedAsync(key, payload, cancellationToken);
    }

    internal ValueTask<StripedObjectLogRecordAddress> AppendValidatedAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            ObjectLogRecordKind.Put,
            key,
            payload,
            transactionId: 0,
            requiresDurabilityFlush: true,
            cancellationToken);

    public ValueTask<StripedObjectLogRecordAddress> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return DeleteValidatedAsync(key, cancellationToken);
    }

    internal ValueTask<StripedObjectLogRecordAddress> DeleteValidatedAsync(
        string key,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            ObjectLogRecordKind.Delete,
            key,
            ReadOnlyMemory<byte>.Empty,
            transactionId: 0,
            requiresDurabilityFlush: true,
            cancellationToken);

    internal ValueTask<StripedObjectLogRecordAddress> AppendTransactionalAsync(
        ObjectLogRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long transactionId,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (transactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        return EnqueueAsync(
            kind,
            key,
            payload,
            transactionId,
            requiresDurabilityFlush: true,
            cancellationToken);
    }

    internal ValueTask<StripedObjectLogRecordAddress> AppendDerivedTransactionalAsync(
        ObjectLogRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long transactionId,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (transactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        return EnqueueAsync(
            kind,
            key,
            payload,
            transactionId,
            requiresDurabilityFlush: false,
            cancellationToken);
    }

    internal ValueTask<StripedObjectLogRecordAddress> AppendMaintenanceAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            ObjectLogRecordKind.Put,
            key,
            payload,
            transactionId: 0,
            requiresDurabilityFlush: false,
            cancellationToken);

    internal async ValueTask<StripedObjectLogRecordAddress[]> AppendTransactionBatchAsync(
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        long transactionId,
        bool requiresDurabilityFlush,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transactionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count == 0)
        {
            return [];
        }

        var completion = new BatchAppendCompletion(mutations.Count);
        var submitted = 0;
        try
        {
            for (; submitted < mutations.Count; submitted++)
            {
                var mutation = mutations[submitted];
                await _stripes[SelectStripe(mutation.Key)].EnqueueBatchItemAsync(
                    mutation,
                    transactionId,
                    requiresDurabilityFlush,
                    completion,
                    submitted,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            completion.FailUnsubmitted(exception, mutations.Count - submitted);
        }

        // Drain every admitted record, even after admission fails, before releasing payloads
        // or the transaction preparation gate. Queue capacity continues to count records.
        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class BatchAppendCompletion
    {
        private readonly StripedObjectLogRecordAddress[] _addresses;
        private readonly TaskCompletionSource<StripedObjectLogRecordAddress[]> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private int _remaining;

        public BatchAppendCompletion(int count)
        {
            _addresses = new StripedObjectLogRecordAddress[count];
            _remaining = count;
        }

        public Task<StripedObjectLogRecordAddress[]> Task => _completion.Task;

        public void Succeed(int index, StripedObjectLogRecordAddress address)
        {
            _addresses[index] = address;
            Complete(1);
        }

        public void FailUnsubmitted(Exception exception, int count)
        {
            Interlocked.CompareExchange(ref _failure, exception, null);
            Complete(count);
        }

        private void Complete(int count)
        {
            if (Interlocked.Add(ref _remaining, -count) == 0)
            {
                if (_failure is { } failure)
                {
                    _completion.TrySetException(failure);
                }
                else
                {
                    _completion.TrySetResult(_addresses);
                }
            }
        }
    }

    internal async ValueTask FlushAllAsync(CancellationToken cancellationToken = default) =>
        await FlushStripesAsync(ulong.MaxValue, cancellationToken).ConfigureAwait(false);

    internal async ValueTask FlushStripesAsync(
        ulong stripeMask,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (stripeMask == 0)
        {
            return;
        }

        var flushTasks = new List<Task>(_stripes.Length);
        for (var index = 0; index < _stripes.Length; index++)
        {
            if ((stripeMask & (1UL << index)) != 0)
            {
                var stripe = _stripes[index];
                flushTasks.Add(Task.Run(
                    async () => await stripe.FlushAsync(cancellationToken).ConfigureAwait(false),
                    CancellationToken.None));
            }
        }

        await Task.WhenAll(flushTasks).ConfigureAwait(false);
    }

    internal async ValueTask ForceRotateStripesAsync(
        int firstStripe,
        int count,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (firstStripe < 0 || count < 1 || firstStripe + count > _stripes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstStripe));
        }

        var tasks = new Task[count];
        for (var offset = 0; offset < count; offset++)
        {
            var stripe = _stripes[firstStripe + offset];
            tasks[offset] = Task.Run(
                async () => await stripe.ForceRotateAsync(cancellationToken).ConfigureAwait(false),
                CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var stripe in _stripes)
        {
            stripe.Complete();
        }

        List<Exception>? failures = null;
        foreach (var stripe in _stripes)
        {
            try
            {
                await stripe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more striped-log workers failed during disposal.", failures);
        }
    }

    internal int SelectStripe(string key)
    {
        return SegmentCoreHashRouter.SelectStripe(
            key,
            _options.StripeCount,
            _options.HashAlgorithm,
            _options.HashSeed);
    }

    private ValueTask<StripedObjectLogRecordAddress> EnqueueAsync(
        ObjectLogRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long transactionId,
        bool requiresDurabilityFlush,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var stripe = SelectStripe(key);
        return _stripes[stripe].EnqueueAsync(
            kind,
            key,
            payload,
            transactionId,
            requiresDurabilityFlush,
            cancellationToken);
    }

    private sealed class StripeWorker : IAsyncDisposable
    {
        private readonly int _stripe;
        private readonly StripedObjectLogProbeOptions _options;
        private readonly ObjectLogProbeWriter _writer;
        private readonly Channel<PendingWrite> _channel;
        private readonly Task _workerTask;
        private Exception? _failure;
        private long _submitted;
        private long _batches;
        private long _flushes;
        private long _backpressured;
        private long _rejected;
        private int _outstanding;
        private int _peakOutstanding;
        private int _largestBatch;
        private int _completed;

        private StripeWorker(
            int stripe,
            StripedObjectLogProbeOptions options,
            ObjectLogProbeWriter writer)
        {
            _stripe = stripe;
            _options = options;
            _writer = writer;
            _channel = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(
                options.QueueCapacityPerStripe)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _workerTask = RunAsync();
        }

        public StripedObjectLogStatistics Statistics => new(
            Volatile.Read(ref _submitted),
            Volatile.Read(ref _batches),
            Volatile.Read(ref _flushes),
            Volatile.Read(ref _largestBatch),
            Volatile.Read(ref _backpressured),
            Volatile.Read(ref _rejected),
            Volatile.Read(ref _outstanding),
            Volatile.Read(ref _peakOutstanding));

        public ObjectLogWritePosition WritePosition => _writer.WritePosition;

        public long RepairedTailBytesOnOpen => _writer.RepairedTailBytesOnOpen;

        public static async ValueTask<StripeWorker> OpenAsync(
            int stripe,
            string rootDirectory,
            StripedObjectLogProbeOptions options,
            IDurableFileSystem durableFileSystem,
            ObjectLogWritePosition? recoveryPosition,
            Action<int, ObjectLogReplayRecord>? replayRecord,
            CancellationToken cancellationToken)
        {
            var writerOptions = new ObjectLogProbeOptions
            {
                DurabilityMode = options.DurabilityStrategy == ObjectLogDurabilityStrategy.WriteThrough
                    ? ObjectLogDurabilityMode.Durable
                    : ObjectLogDurabilityMode.Performance,
                DurabilityStrategy = options.DurabilityStrategy,
                FlushOnDispose = false,
                MaxSegmentBytes = options.MaxSegmentBytes,
                MaximumCombinedWriteBytes = options.MaximumCombinedWriteBytes
            };
            var writer = recoveryPosition is null
                ? await ObjectLogProbeWriter.OpenAsync(
                    rootDirectory,
                    writerOptions,
                    durableFileSystem,
                    cancellationToken).ConfigureAwait(false)
                : await ObjectLogProbeWriter.OpenFromCheckpointAsync(
                    rootDirectory,
                    recoveryPosition.Value,
                    record => (replayRecord ?? throw new ArgumentNullException(nameof(replayRecord)))(stripe, record),
                    writerOptions,
                    durableFileSystem,
                    cancellationToken).ConfigureAwait(false);
            return new StripeWorker(stripe, options, writer);
        }

        public ValueTask<StripedObjectLogRecordAddress> EnqueueAsync(
            ObjectLogRecordKind kind,
            string key,
            ReadOnlyMemory<byte> payload,
            long transactionId,
            bool requiresDurabilityFlush,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<StripedObjectLogRecordAddress>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingWrite(
                kind,
                key,
                payload,
                transactionId,
                requiresDurabilityFlush,
                completion);
            var admission = QueueAsync(pending, cancellationToken);
            return admission.IsCompletedSuccessfully
                ? new ValueTask<StripedObjectLogRecordAddress>(completion.Task)
                : AwaitAdmissionAsync(admission, completion.Task);
        }

        public ValueTask EnqueueBatchItemAsync(
            SegmentCorePendingMutation mutation,
            long transactionId,
            bool requiresDurabilityFlush,
            BatchAppendCompletion completion,
            int index,
            CancellationToken cancellationToken) => QueueAsync(
                new PendingWrite(mutation.Kind, mutation.Key, mutation.Payload, transactionId,
                    requiresDurabilityFlush, null, completion, index), cancellationToken);

        private static async ValueTask<StripedObjectLogRecordAddress> AwaitAdmissionAsync(
            ValueTask admission,
            Task<StripedObjectLogRecordAddress> completion)
        {
            await admission.ConfigureAwait(false);
            return await completion.ConfigureAwait(false);
        }

        private ValueTask QueueAsync(PendingWrite pending, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failure is { } priorFailure)
            {
                return ValueTask.FromException(priorFailure);
            }

            var outstanding = Interlocked.Increment(ref _outstanding);
            if (_channel.Writer.TryWrite(pending))
            {
                UpdateMaximum(ref _peakOutstanding, outstanding);
                Interlocked.Increment(ref _submitted);
                return ValueTask.CompletedTask;
            }

            var failure = Volatile.Read(ref _failure);
            if (failure is not null)
            {
                Interlocked.Decrement(ref _outstanding);
                return ValueTask.FromException(failure);
            }

            if (Volatile.Read(ref _completed) != 0)
            {
                Interlocked.Decrement(ref _outstanding);
                return ValueTask.FromException(
                    new ObjectDisposedException(nameof(StripedObjectLogProbeWriter)));
            }

            if (_options.QueueFullBehavior == ObjectLogQueueFullBehavior.Reject)
            {
                Interlocked.Decrement(ref _outstanding);
                Interlocked.Increment(ref _rejected);
                return ValueTask.FromException(
                    new ObjectLogBackpressureException(
                        $"Object-log stripe {_stripe} has reached its {_options.QueueCapacityPerStripe:N0}-write queue capacity."));
            }

            UpdateMaximum(ref _peakOutstanding, outstanding);
            Interlocked.Increment(ref _backpressured);
            return EnqueueAfterCapacityAsync(pending, cancellationToken);
        }

        private async ValueTask EnqueueAfterCapacityAsync(
            PendingWrite pending,
            CancellationToken cancellationToken)
        {
            try
            {
                await _channel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _submitted);
            }
            catch
            {
                Interlocked.Decrement(ref _outstanding);
                throw;
            }
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _channel.Writer.TryComplete();
            }
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) =>
            _writer.FlushAsync(cancellationToken);

        public ValueTask ForceRotateAsync(CancellationToken cancellationToken) =>
            _writer.ForceRotateAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Complete();
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            finally
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            var batch = new List<PendingWrite>(_options.MaximumBatchRecords);
            var requests = new ObjectLogAppendRequest[_options.MaximumBatchRecords];
            var addresses = new ObjectLogRecordAddress[_options.MaximumBatchRecords];
            try
            {
                while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    batch.Clear();
                    if (!_channel.Reader.TryRead(out var first))
                    {
                        continue;
                    }

                    batch.Add(first);
                    if (_options.GroupCommitDelay > TimeSpan.Zero
                        && batch.Count < _options.MaximumBatchRecords)
                    {
                        await Task.Delay(_options.GroupCommitDelay).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Yield();
                    }

                    while (batch.Count < _options.MaximumBatchRecords
                           && _channel.Reader.TryRead(out var pending))
                    {
                        batch.Add(pending);
                    }

                    var requiresDurabilityFlush = false;
                    for (var index = 0; index < batch.Count; index++)
                    {
                        var item = batch[index];
                        requests[index] = new ObjectLogAppendRequest(
                            item.Kind,
                            item.Key,
                            item.Payload,
                            item.TransactionId);
                        requiresDurabilityFlush |= item.RequiresDurabilityFlush;
                    }

                    await _writer.AppendBatchIntoAsync(
                        requests,
                        batch.Count,
                        addresses).ConfigureAwait(false);

                    if (_options.DurabilityStrategy == ObjectLogDurabilityStrategy.ExplicitFlush
                        && requiresDurabilityFlush)
                    {
                        await _writer.FlushAsync().ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref _batches);
                    if (_options.DurabilityStrategy == ObjectLogDurabilityStrategy.ExplicitFlush
                        && requiresDurabilityFlush)
                    {
                        Interlocked.Increment(ref _flushes);
                    }
                    UpdateMaximum(ref _largestBatch, batch.Count);

                    for (var index = 0; index < batch.Count; index++)
                    {
                        batch[index].Succeed(
                            new StripedObjectLogRecordAddress(_stripe, addresses[index]));
                        requests[index] = default;
                        Interlocked.Decrement(ref _outstanding);
                    }
                }
            }
            catch (Exception exception)
            {
                _failure = exception;
                foreach (var pending in batch)
                {
                    pending.Fail(exception);
                    Interlocked.Decrement(ref _outstanding);
                }

                while (_channel.Reader.TryRead(out var pending))
                {
                    pending.Fail(exception);
                    Interlocked.Decrement(ref _outstanding);
                }

                _channel.Writer.TryComplete(exception);
                throw;
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        private readonly record struct PendingWrite(
            ObjectLogRecordKind Kind,
            string Key,
            ReadOnlyMemory<byte> Payload,
            long TransactionId,
            bool RequiresDurabilityFlush,
            TaskCompletionSource<StripedObjectLogRecordAddress>? Completion,
            BatchAppendCompletion? Batch = null,
            int BatchIndex = 0)
        {
            public void Succeed(StripedObjectLogRecordAddress address)
            {
                if (Completion is not null)
                {
                    Completion.TrySetResult(address);
                }
                else
                {
                    Batch!.Succeed(BatchIndex, address);
                }
            }

            public void Fail(Exception exception)
            {
                if (Completion is not null)
                {
                    Completion.TrySetException(exception);
                }
                else
                {
                    Batch!.FailUnsubmitted(exception, 1);
                }
            }
        }
    }
}
