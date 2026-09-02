using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class ObjectLogProbeTests
{
    [TestMethod]
    public void Crc32C_MatchesStandardCheckVector()
    {
        Assert.AreEqual(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    [TestMethod]
    public async Task DurableAppend_RecoversAcknowledgedRecords()
    {
        using var root = TestRoot.Create();
        var options = DurableOptions();
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, options))
        {
            _ = await writer.AppendAsync("student/1", "Ada"u8.ToArray());
            _ = await writer.AppendAsync("employee/2", "Grace"u8.ToArray());
        }

        var recovered = ObjectLogRecovery.Recover(root.Path);

        Assert.AreEqual(2, recovered.Records.Count);
        CollectionAssert.AreEqual("Ada"u8.ToArray(), recovered.Records["student/1"].Payload);
        CollectionAssert.AreEqual("Grace"u8.ToArray(), recovered.Records["employee/2"].Payload);
        Assert.AreEqual(2, recovered.LastLsn);
        Assert.AreEqual(0, recovered.RepairedTailBytes);
    }

    [TestMethod]
    public async Task Recovery_TruncatesEveryPossibleShortFinalFrame()
    {
        using var sourceRoot = TestRoot.Create();
        ObjectLogRecordAddress finalAddress;
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(sourceRoot.Path, DurableOptions()))
        {
            _ = await writer.AppendAsync("first", Enumerable.Repeat((byte)0x11, 37).ToArray());
            finalAddress = await writer.AppendAsync("second", Enumerable.Repeat((byte)0x22, 73).ToArray());
        }

        var sourceSegment = SegmentPath(sourceRoot.Path, finalAddress.SegmentId);
        var sourceBytes = await File.ReadAllBytesAsync(sourceSegment);
        var frameEnd = checked((int)(finalAddress.Offset + finalAddress.FrameLength));
        Assert.AreEqual(sourceBytes.Length, frameEnd);

        for (var retained = 0; retained < finalAddress.FrameLength; retained++)
        {
            using var trialRoot = TestRoot.Create();
            var trialSegments = Directory.CreateDirectory(System.IO.Path.Combine(trialRoot.Path, "segments"));
            var trialSegment = System.IO.Path.Combine(trialSegments.FullName, System.IO.Path.GetFileName(sourceSegment));
            await File.WriteAllBytesAsync(
                trialSegment,
                sourceBytes.AsMemory(0, checked((int)finalAddress.Offset + retained)).ToArray());

            var recovered = ObjectLogRecovery.Recover(trialRoot.Path, repairActiveTail: true);

            Assert.AreEqual(1, recovered.Records.Count, $"Retained final-frame bytes: {retained}");
            Assert.IsTrue(recovered.Records.ContainsKey("first"), $"Retained final-frame bytes: {retained}");
            Assert.AreEqual(finalAddress.Offset, new FileInfo(trialSegment).Length);
            Assert.AreEqual(retained, recovered.RepairedTailBytes);
        }
    }

    [TestMethod]
    public async Task Recovery_RejectsInteriorPayloadCorruption()
    {
        using var root = TestRoot.Create();
        ObjectLogRecordAddress middle;
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions()))
        {
            _ = await writer.AppendAsync("first", "one"u8.ToArray());
            middle = await writer.AppendAsync("middle", "two"u8.ToArray());
            _ = await writer.AppendAsync("last", "three"u8.ToArray());
        }

        FlipPayloadByte(SegmentPath(root.Path, middle.SegmentId), middle.Offset, "middle");

        _ = Assert.ThrowsExactly<ObjectLogCorruptionException>(() => ObjectLogRecovery.Recover(root.Path));
    }

    [TestMethod]
    public async Task Recovery_RejectsFullyWrittenFinalFrameCorruption()
    {
        using var root = TestRoot.Create();
        ObjectLogRecordAddress address;
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions()))
        {
            address = await writer.AppendAsync("only", "payload"u8.ToArray());
        }

        FlipPayloadByte(SegmentPath(root.Path, address.SegmentId), address.Offset, "only");

        _ = Assert.ThrowsExactly<ObjectLogCorruptionException>(() => ObjectLogRecovery.Recover(root.Path));
    }

    [TestMethod]
    public async Task Rotation_RecoversLinkedChain_AndIgnoresOrphanSegment()
    {
        using var root = TestRoot.Create();
        var options = DurableOptions() with { MaxSegmentBytes = 4 * 1024 };
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 7; index++)
            {
                _ = await writer.AppendAsync($"key-{index}", Enumerable.Repeat((byte)index, 1_300).ToArray());
            }

            Assert.IsGreaterThan(1L, writer.ActiveSegmentId);
        }

        var firstRecovery = ObjectLogRecovery.Recover(root.Path);
        var activePath = firstRecovery.ActiveSegmentPath!;
        var orphanPath = SegmentPath(root.Path, 999);
        File.Copy(activePath, orphanPath);

        var recovered = ObjectLogRecovery.Recover(root.Path);

        Assert.AreEqual(7, recovered.Records.Count);
        Assert.AreEqual(1, recovered.IgnoredOrphanSegments);
    }

    [TestMethod]
    public async Task FreshSegmentBatch_RotatesBeforeCombinedFramesExceedSegmentBound()
    {
        using var root = TestRoot.Create();
        var options = DurableOptions() with { MaxSegmentBytes = 4 * 1024 };
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, options))
        {
            var requests = Enumerable.Range(0, 7)
                .Select(index => new ObjectLogAppendRequest(
                    ObjectLogRecordKind.Put,
                    $"batch-{index}",
                    Enumerable.Repeat((byte)index, 1_300).ToArray(),
                    TransactionId: 0))
                .ToArray();

            _ = await writer.AppendBatchAsync(requests);
        }

        var recovered = ObjectLogRecovery.Recover(root.Path);
        Assert.HasCount(7, recovered.Records);
        Assert.IsGreaterThan(
            1,
            Directory.EnumerateFiles(
                    Path.Combine(root.Path, "segments"),
                    "segment-*.idbseg",
                    SearchOption.TopDirectoryOnly)
                .Count());
    }

    [TestMethod]
    public async Task Delete_WritesTombstone_AndRemovesRecoveredValue()
    {
        using var root = TestRoot.Create();
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions()))
        {
            _ = await writer.AppendAsync("gone", "value"u8.ToArray());
            _ = await writer.AppendAsync("kept", "value"u8.ToArray());
            _ = await writer.DeleteAsync("gone");
        }

        var recovered = ObjectLogRecovery.Recover(root.Path);

        Assert.AreEqual(1, recovered.Records.Count);
        Assert.IsTrue(recovered.Records.ContainsKey("kept"));
        Assert.AreEqual(3, recovered.LastLsn);
    }

    [TestMethod]
    public async Task DurabilityModes_OnlyFlushAcknowledgedWritesInDurableMode()
    {
        using var durableRoot = TestRoot.Create();
        var durableFileSystem = new RecordingDurableFileSystem();
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(
                         durableRoot.Path,
                         DurableOptions(),
                         durableFileSystem))
        {
            Assert.AreEqual(1, durableFileSystem.FileFlushCount);
            _ = await writer.AppendAsync("key", "value"u8.ToArray());
            Assert.AreEqual(2, durableFileSystem.FileFlushCount);
        }

        using var performanceRoot = TestRoot.Create();
        var performanceFileSystem = new RecordingDurableFileSystem();
        var performanceOptions = DurableOptions() with
        {
            DurabilityMode = ObjectLogDurabilityMode.Performance,
            FlushOnDispose = false
        };
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(
                         performanceRoot.Path,
                         performanceOptions,
                         performanceFileSystem))
        {
            Assert.AreEqual(1, performanceFileSystem.FileFlushCount);
            _ = await writer.AppendAsync("key", "value"u8.ToArray());
            Assert.AreEqual(1, performanceFileSystem.FileFlushCount);
        }

        Assert.AreEqual(1, performanceFileSystem.FileFlushCount);
        Assert.AreEqual(1, performanceFileSystem.PublishCount);
    }

    [TestMethod]
    public async Task WriteThroughDurability_DoesNotAddRedundantExplicitFlush()
    {
        using var root = TestRoot.Create();
        var fileSystem = new RecordingDurableFileSystem();
        var options = DurableOptions() with
        {
            DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
        };
        await using (var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, options, fileSystem))
        {
            Assert.AreEqual(0, fileSystem.FileFlushCount);
            _ = await writer.AppendAsync("key", "value"u8.ToArray());
            Assert.AreEqual(0, fileSystem.FileFlushCount);
        }

        Assert.AreEqual(1, fileSystem.PublishCount);
    }

    [TestMethod]
    public async Task SegmentPublishFailure_FaultsWriter_AndPreservesPriorAcknowledgement()
    {
        using var root = TestRoot.Create();
        var fileSystem = new RecordingDurableFileSystem { FailOnPublish = 2 };
        var options = DurableOptions() with { MaxSegmentBytes = 4 * 1024 };
        await using var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, options, fileSystem);
        _ = await writer.AppendAsync("first", Enumerable.Repeat((byte)0x11, 2_000).ToArray());

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            () => writer.AppendAsync("rotation", Enumerable.Repeat((byte)0x22, 2_000).ToArray()).AsTask());
        Assert.IsTrue(writer.IsFaulted);

        var recovered = ObjectLogRecovery.Recover(root.Path);
        Assert.AreEqual(1, recovered.Records.Count);
        Assert.IsTrue(recovered.Records.ContainsKey("first"));
    }

    [TestMethod]
    public async Task FlushFailure_PermanentlyFaultsWriter()
    {
        using var root = TestRoot.Create();
        var fileSystem = new RecordingDurableFileSystem { FailOnFileFlush = 2 };
        await using var writer = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions(), fileSystem);

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            () => writer.AppendAsync("uncertain", "value"u8.ToArray()).AsTask());
        Assert.IsTrue(writer.IsFaulted);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.AppendAsync("rejected", "value"u8.ToArray()).AsTask());
    }

    [TestMethod]
    public async Task WriterLease_RejectsSecondWriter()
    {
        using var root = TestRoot.Create();
        await using var first = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions());

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            () => ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions()).AsTask());
    }

    [TestMethod]
    public async Task WriterLease_RejectsSymlinkAliasAndReopensAfterDispose()
    {
        using var root = TestRoot.Create();
        var alias = root.Path + "-alias";
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(alias, root.Path);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or IOException
                                              or PlatformNotSupportedException
                                              or NotSupportedException)
            {
                Assert.Inconclusive($"Directory symlinks are unavailable: {exception.Message}");
                return;
            }

            var owner = await ObjectLogProbeWriter.OpenAsync(root.Path, DurableOptions());
            try
            {
                _ = await owner.AppendAsync("aliased", "value"u8.ToArray());
                _ = await Assert.ThrowsExactlyAsync<IOException>(
                    () => ObjectLogProbeWriter.OpenAsync(alias, DurableOptions()).AsTask());
            }
            finally
            {
                await owner.DisposeAsync();
            }

            await using var reopened = await ObjectLogProbeWriter.OpenAsync(alias, DurableOptions());
            _ = await reopened.AppendAsync("after-reopen", "value"u8.ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(alias);
            }
            catch
            {
                // Symlink cleanup should not hide the ownership assertion.
            }
        }
    }

    private static ObjectLogProbeOptions DurableOptions() => new()
    {
        DurabilityMode = ObjectLogDurabilityMode.Durable,
        FlushOnDispose = false,
        MaxSegmentBytes = 1024 * 1024
    };

    private static string SegmentPath(string root, long segmentId) =>
        System.IO.Path.Combine(root, "segments", $"segment-{segmentId:D20}.idbseg");

    private static void FlipPayloadByte(string path, long frameOffset, string key)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var payloadOffset = frameOffset + ObjectLogFormat.FrameHeaderSize + Encoding.UTF8.GetByteCount(key);
        stream.Position = payloadOffset;
        var value = stream.ReadByte();
        Assert.AreNotEqual(-1, value);
        stream.Position = payloadOffset;
        stream.WriteByte((byte)(value ^ 0x5A));
        stream.Flush(flushToDisk: true);
    }

    private sealed class RecordingDurableFileSystem : IDurableFileSystem
    {
        public int FileFlushCount { get; private set; }

        public int DirectoryFlushCount { get; private set; }

        public int PublishCount { get; private set; }

        public int? FailOnFileFlush { get; init; }

        public int? FailOnPublish { get; init; }

        public string DirectoryFlushStrategy => "test recorder";

        public void FlushFile(SafeFileHandle handle)
        {
            FileFlushCount++;
            if (FileFlushCount == FailOnFileFlush)
            {
                throw new IOException("Injected durable-flush failure.");
            }

            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }

        public void FlushDirectory(string directoryPath)
        {
            DirectoryFlushCount++;
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);
        }

        public void PublishFile(string temporaryPath, string finalPath)
        {
            PublishCount++;
            if (PublishCount == FailOnPublish)
            {
                throw new IOException("Injected durable-publication failure.");
            }

            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);
        }

        public void ReplaceFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
    }

    private sealed class TestRoot : IDisposable
    {
        private static readonly string Parent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "InterfaceDbObjectLogTests");

        private TestRoot(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestRoot Create()
        {
            Directory.CreateDirectory(Parent);
            var path = System.IO.Path.Combine(Parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
        }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var fullParent = System.IO.Path.GetFullPath(Parent) + System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to clean unexpected test path '{fullPath}'.");
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
