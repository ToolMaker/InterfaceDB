using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Commercial.Repositories.Tests;

[TestClass]
public sealed class ResilientRepositoryTests
{
    [TestMethod]
    public async Task RecoverableSynchronizationFailureIsObservedAndDoesNotLosePendingWrite()
    {
        var telemetry = new CapturingTelemetry();
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        var options = Options(); options.Telemetry = telemetry;
        await using var repository = CreateRepository(preferred, peer, options);
        await repository.SaveAsync(new TestEntity { Key = "private-key", Value = "private-value" });
        var pending = ReadMetadata(preferred.Get("private-key")!);
        peer.MetadataSaveException = new ResilientEndpointUnavailableException("private");
        var result = await repository.SynchronizeAsync("private-key");
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual(pending, ReadMetadata(preferred.Get("private-key")!));
        Assert.IsTrue(telemetry.Records.Any(record => record.Component == Idb.Diagnostics.DatabaseComponent.LocalRemote
            && record.Outcome == Idb.Diagnostics.DatabaseOutcome.Failure && record.Recoverable && record.Log));
    }

    private sealed class CapturingTelemetry : Idb.Diagnostics.IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public System.Collections.Concurrent.ConcurrentQueue<Idb.Diagnostics.DatabaseObservation> Records { get; } = new();
        public void TryRecord(in Idb.Diagnostics.DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(Idb.Diagnostics.DatabaseGauge gauge, long delta) { }
    }

    private static readonly TestEntityMapper Mapper = new();

    [TestMethod]
    public async Task PreferredOnly_PreferredHit_DoesNotContactPeer()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "local", Metadata(revision: 10));

        await using var repository = CreateRepository(preferred, peer);
        var result = await repository.QueryFirstByStorageKeyAsync("one");

        Assert.IsNotNull(result);
        Assert.AreEqual("local", result.Value);
        Assert.AreEqual(1, preferred.QueryFirstByStorageKeyCallCount);
        Assert.AreEqual(0, peer.QueryFirstByStorageKeyCallCount);
    }

    [TestMethod]
    public void Constructor_RejectsEndpointWithoutAtomicMetadataSupport()
    {
        var nonMetadata = new NonMetadataRepository();
        var durable = new FakeMetadataRepository("peer");

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            new ResilientRepository<TestEntity>(nonMetadata, durable, Mapper));

        Assert.AreEqual("preferred", exception.ParamName);
    }

    [TestMethod]
    public void Constructor_RejectsEndpointWithoutDurableCommitSemantics()
    {
        var nonDurable = new FakeMetadataRepository(
            "preferred",
            StorageDurabilityMode.Performance);
        var durable = new FakeMetadataRepository("peer");

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            new ResilientRepository<TestEntity>(nonDurable, durable, Mapper));

        Assert.AreEqual("preferred", exception.ParamName);
    }

    [TestMethod]
    public async Task Save_DurablyRecordsPendingMutation_WithoutContactingPeer()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);

        var saved = await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" });
        var metadata = ReadMetadata(preferred.Get("one")!);

        AssertLogicalApplicationEnvelope(saved, "one");
        Assert.AreEqual(1, preferred.MetadataSaveCallCount);
        Assert.AreEqual(0, peer.QueryFirstByStorageKeyCallCount);
        Assert.AreEqual(0, peer.MetadataSaveCallCount);
        Assert.IsTrue(metadata.RevisionTimestampUtcMicroseconds > 0);
        Assert.AreNotEqual(Guid.Empty, metadata.PendingMutationId);
        Assert.AreEqual(PendingMutationKind.Upsert, metadata.PendingMutationKind);
        Assert.IsFalse(metadata.IsTombstone);
    }

    [TestMethod]
    public async Task Synchronize_CommitsDistinctPeerOwnedCopy_ThenAcknowledgesPreferred()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" });
        var pending = ReadMetadata(preferred.Get("one")!);

        var result = await repository.SynchronizeAsync("one");

        var preferredEntity = preferred.Get("one");
        var peerEntity = peer.Get("one");
        Assert.IsNotNull(preferredEntity);
        Assert.IsNotNull(peerEntity);
        Assert.AreNotSame(preferredEntity, peerEntity);
        Assert.AreNotSame(preferredEntity.Envelope, peerEntity.Envelope);
        StringAssert.StartsWith(preferredEntity.Envelope!.Location!, "preferred://");
        StringAssert.StartsWith(peerEntity.Envelope!.Location!, "peer://");

        var preferredMetadata = ReadMetadata(preferredEntity);
        var peerMetadata = ReadMetadata(peerEntity);
        Assert.AreEqual(pending.RevisionTimestampUtcMicroseconds, peerMetadata.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual(pending.PendingMutationId, peerMetadata.LastAppliedMutationId);
        Assert.AreEqual(Guid.Empty, peerMetadata.PendingMutationId);
        Assert.AreEqual(Guid.Empty, preferredMetadata.PendingMutationId);
        Assert.AreEqual(1, result.PeerCommits);
        Assert.AreEqual(1, result.Acknowledgements);
        Assert.AreEqual(0, result.Failures.Count);
    }

    [TestMethod]
    public async Task Synchronize_PeerFailure_LeavesCanonicalPendingMutationUnchanged()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" });
        var pending = ReadMetadata(preferred.Get("one")!);
        peer.MetadataSaveException = new ResilientEndpointUnavailableException("offline");

        var result = await repository.SynchronizeAsync("one");

        Assert.AreEqual(pending, ReadMetadata(preferred.Get("one")!));
        Assert.IsNull(peer.Get("one"));
        Assert.AreEqual(0, result.Acknowledgements);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.IsTrue(result.Failures[0].IsTransient);
    }

    [TestMethod]
    public async Task SynchronizeAll_StopsAfterFirstPeerOutageAndLeavesEveryMutationPending()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "first" });
        await repository.SaveAsync(new TestEntity { Key = "two", Value = "second" });
        peer.QueryFirstByStorageKeyException = new ResilientEndpointUnavailableException("offline");

        var result = await repository.SynchronizeAsync();

        Assert.AreEqual(1, peer.QueryFirstByStorageKeyCallCount);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.IsTrue(ReadMetadata(preferred.Get("one")!).HasPendingMutation);
        Assert.IsTrue(ReadMetadata(preferred.Get("two")!).HasPendingMutation);
    }

    [TestMethod]
    public async Task SynchronizeAll_KeyScopedTransientFailureDoesNotStarveHealthyKeys()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        var options = Options();
        options.IsEndpointWideFailureException = _ => false;
        await using var repository = CreateRepository(preferred, peer, options);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "blocked" });
        await repository.SaveAsync(new TestEntity { Key = "two", Value = "healthy" });
        peer.QueryFirstByStorageKeyOverride = storageKey => storageKey == "one"
            ? throw new TimeoutException("key-scoped timeout")
            : peer.Get(storageKey);

        var result = await repository.SynchronizeAsync();

        Assert.AreEqual(2, peer.QueryFirstByStorageKeyCallCount);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.IsTrue(ReadMetadata(preferred.Get("one")!).HasPendingMutation);
        Assert.IsFalse(ReadMetadata(preferred.Get("two")!).HasPendingMutation);
        Assert.AreEqual("healthy", peer.Get("two")!.Value);
    }

    [TestMethod]
    public async Task Synchronize_LostAcknowledgement_DetectsAppliedOperationWithoutDuplicateCommit()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" });
        var pending = ReadMetadata(preferred.Get("one")!);
        peer.Seed(
            "one",
            "value",
            pending with
            {
                PendingMutationId = Guid.Empty,
                PendingMutationKind = PendingMutationKind.None,
                LastAppliedMutationId = pending.PendingMutationId
            });

        var result = await repository.SynchronizeAsync("one");

        Assert.AreEqual(0, peer.MetadataSaveCallCount);
        Assert.AreEqual(1, result.Acknowledgements);
        Assert.AreEqual(Guid.Empty, ReadMetadata(preferred.Get("one")!).PendingMutationId);
    }

    [TestMethod]
    public async Task Synchronize_OldCommitCannotAcknowledgeNewerConcurrentLocalMutation()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        await repository.SaveAsync(new TestEntity { Key = "one", Value = "first" });
        var firstPending = ReadMetadata(preferred.Get("one")!).PendingMutationId;
        var peerCommitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPeerCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.BeforeMetadataSaveAsync = async (_, _, _, cancellationToken) =>
        {
            peerCommitStarted.TrySetResult();
            await allowPeerCommit.Task.WaitAsync(cancellationToken);
        };

        var synchronizeTask = repository.SynchronizeAsync("one").AsTask();
        await peerCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var edit = (await repository.QueryFirstByStorageKeyAsync("one"))!;
        edit.Value = "second";
        await repository.SaveAsync(edit);
        var secondPending = ReadMetadata(preferred.Get("one")!).PendingMutationId;
        allowPeerCommit.TrySetResult();

        var result = await synchronizeTask;
        var finalPreferred = preferred.Get("one");
        Assert.IsNotNull(finalPreferred);
        Assert.AreEqual("second", finalPreferred.Value);
        Assert.AreNotEqual(firstPending, secondPending);
        Assert.AreEqual(secondPending, ReadMetadata(finalPreferred).PendingMutationId);
        Assert.AreEqual(0, result.Acknowledgements);
    }

    [TestMethod]
    public async Task Delete_PersistsAndReplicatesTombstone_WithoutResurrectionAfterRestart()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using (var firstRepository = CreateRepository(preferred, peer))
        {
            var saved = await firstRepository.SaveAsync(
                new TestEntity { Key = "one", Value = "value" });
            await firstRepository.SynchronizeAsync("one");

            Assert.IsTrue(await firstRepository.DeleteAsync(saved));
            Assert.IsNull(await firstRepository.QueryFirstByStorageKeyAsync("one"));
            var pendingTombstone = ReadMetadata(preferred.Get("one")!);
            Assert.IsTrue(pendingTombstone.IsTombstone);
            Assert.AreEqual(PendingMutationKind.Delete, pendingTombstone.PendingMutationKind);
        }

        await using var restartedRepository = CreateRepository(preferred, peer);
        var result = await restartedRepository.SynchronizeAsync("one");
        Assert.AreEqual(1, result.PeerCommits);
        Assert.AreEqual(1, result.Acknowledgements);
        Assert.IsTrue(ReadMetadata(peer.Get("one")!).IsTombstone);
        Assert.IsNull(await restartedRepository.QueryFirstByStorageKeyAsync("one"));
    }

    [TestMethod]
    public async Task Delete_MissingStillPersistsTombstoneButReturnsFalse()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);

        var deleted = await repository.DeleteAsync(new TestEntity { Key = "missing" });

        Assert.IsFalse(deleted);
        var tombstone = preferred.Get("missing");
        Assert.IsNotNull(tombstone);
        var metadata = ReadMetadata(tombstone);
        Assert.IsTrue(metadata.IsTombstone);
        Assert.AreEqual(PendingMutationKind.Delete, metadata.PendingMutationKind);
        Assert.AreNotEqual(Guid.Empty, metadata.PendingMutationId);
    }

    [TestMethod]
    public async Task Delete_PreferredMissingAndPeerLive_ReturnsTrueAndSupersedesPeerRevision()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        peer.Seed("one", "peer-value", Metadata(revision: 100));
        await using var repository = CreateRepository(preferred, peer);

        var deleted = await repository.DeleteAsync(new TestEntity { Key = "one" });

        Assert.IsTrue(deleted);
        var metadata = ReadMetadata(preferred.Get("one")!);
        Assert.IsTrue(metadata.IsTombstone);
        Assert.IsTrue(metadata.RevisionTimestampUtcMicroseconds > 100);
    }

    [TestMethod]
    public async Task Delete_PreferredMissingAndPeerTombstoned_ReturnsFalse()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        peer.Seed("one", string.Empty, Metadata(revision: 100, isTombstone: true));
        await using var repository = CreateRepository(preferred, peer);

        var deleted = await repository.DeleteAsync(new TestEntity { Key = "one" });

        Assert.IsFalse(deleted);
        var metadata = ReadMetadata(preferred.Get("one")!);
        Assert.IsTrue(metadata.IsTombstone);
        Assert.IsTrue(metadata.RevisionTimestampUtcMicroseconds > 100);
    }

    [TestMethod]
    public async Task PeerAuthoritativeReadOnly_RejectsLocalSaveAndDeleteBeforeWriting()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        var options = Options(
            revisionCheckMode: RevisionCheckMode.PreferredOnly,
            mutationAuthority: MutationAuthority.PeerAuthoritativeReadOnly);
        await using var repository = CreateRepository(preferred, peer, options);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await repository.DeleteAsync(new TestEntity { Key = "one", Value = "value" }));

        Assert.AreEqual(0, preferred.MetadataSaveCallCount);
        Assert.AreEqual(0, peer.MetadataSaveCallCount);
    }

    [TestMethod]
    public async Task WhenInvalidated_CleanReadIsFast_AndDirtyReadRefreshesFromNewerPeer()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "old", Metadata(revision: 10));
        peer.Seed("one", "new", Metadata(revision: 20));
        var options = Options(
            revisionCheckMode: RevisionCheckMode.WhenInvalidated,
            mutationAuthority: MutationAuthority.PeerAuthoritativeReadOnly);
        await using var repository = CreateRepository(preferred, peer, options);

        var clean = await repository.QueryFirstByStorageKeyAsync("one");
        Assert.AreEqual("old", clean!.Value);
        Assert.AreEqual(0, peer.QueryFirstByStorageKeyCallCount);

        await repository.NotifyChangedAsync(new RepositoryChangeSignal("tests", "one", 20, 7));
        var refreshed = await repository.QueryFirstByStorageKeyAsync("one");

        Assert.AreEqual("new", refreshed!.Value);
        Assert.AreEqual(1, peer.QueryFirstByStorageKeyCallCount);
        Assert.AreEqual(20, ReadMetadata(preferred.Get("one")!).RevisionTimestampUtcMicroseconds);
        var state = await repository.GetSynchronizationStateAsync();
        Assert.AreEqual(7, state.LatestObservedCheckpoint);
        Assert.AreEqual(0, state.LastAppliedCheckpoint);
        Assert.IsFalse(state.DirtyStorageKeys.ContainsKey("one"));
    }

    [TestMethod]
    public async Task WhenInvalidated_OfflinePeerServesPreferred_AndRetainsDirtyHint()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer")
        {
            QueryFirstByStorageKeyException = new ResilientEndpointUnavailableException("offline")
        };
        preferred.Seed("one", "offline-value", Metadata(revision: 10));
        var options = Options(
            revisionCheckMode: RevisionCheckMode.WhenInvalidated,
            mutationAuthority: MutationAuthority.PeerAuthoritativeReadOnly);
        await using var repository = CreateRepository(preferred, peer, options);
        await repository.NotifyChangedAsync(new RepositoryChangeSignal("tests", "one", 20, 7));

        var result = await repository.QueryFirstByStorageKeyAsync("one");

        Assert.AreEqual("offline-value", result!.Value);
        var state = await repository.GetSynchronizationStateAsync();
        Assert.IsTrue(state.DirtyStorageKeys.ContainsKey("one"));
        Assert.AreEqual(0, state.LastAppliedCheckpoint);
    }

    [TestMethod]
    public async Task DatasetInvalidation_IsNotClearedAfterFailedPeerEnumeration()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer")
        {
            EnumerationException = new ResilientEndpointUnavailableException("scan failed")
        };
        preferred.Seed("one", "local", Metadata(revision: 10));
        var options = Options(
            revisionCheckMode: RevisionCheckMode.WhenInvalidated,
            mutationAuthority: MutationAuthority.PreferredWritable);
        await using var repository = CreateRepository(preferred, peer, options);
        await repository.NotifyChangedAsync(new RepositoryChangeSignal("tests", null, 20, 8));

        var result = await repository.SynchronizeAsync();

        Assert.AreEqual(1, result.Failures.Count);
        var state = await repository.GetSynchronizationStateAsync();
        Assert.IsTrue(state.IsDatasetDirty);
        Assert.AreEqual(8, state.DatasetDirtyThroughCheckpoint);
        Assert.AreEqual(0, state.LastAppliedCheckpoint);
    }

    [TestMethod]
    public async Task PreferredWritable_NewerPeerIsConflict_AndCannotOverwritePreferred()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "canonical", Metadata(revision: 10));
        peer.Seed("one", "foreign", Metadata(revision: 20));
        var options = Options(
            revisionCheckMode: RevisionCheckMode.AlwaysCompare,
            mutationAuthority: MutationAuthority.PreferredWritable);
        await using var repository = CreateRepository(preferred, peer, options);

        var exception = await Assert.ThrowsExactlyAsync<ResilientRevisionConflictException>(async () =>
            await repository.QueryFirstByStorageKeyAsync("one"));

        Assert.AreEqual("one", exception.StorageKey);
        Assert.AreEqual("canonical", preferred.Get("one")!.Value);
        Assert.AreEqual(0, preferred.MetadataSaveCallCount);
    }

    [TestMethod]
    public async Task Save_ReturnsLogicalEnvelope_AndPreservesProviderOwnership()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        var requested = new TestEntity
        {
            Key = "one",
            Value = "value",
            Envelope = new TestEnvelope
            {
                EnvelopeCacheKey = "foreign:key",
                Location = "foreign://one",
                ExtensionMetadata = [1, 2, 3]
            }
        };

        var applicationCopy = await repository.SaveAsync(requested);

        Assert.AreNotSame(requested, applicationCopy);
        Assert.AreNotSame(requested.Envelope, applicationCopy.Envelope);
        AssertLogicalApplicationEnvelope(applicationCopy, "one");
        Assert.AreEqual(
            PendingMutationKind.Upsert,
            ReadMetadata(preferred.Get("one")!).PendingMutationKind);
    }

    [TestMethod]
    public async Task SaveAndQuery_DeepDetachMutableValuesFromBothEndpoints()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        var requested = new TestEntity
        {
            Key = "one",
            Value = "value",
            Tags = ["persisted"]
        };

        var saved = await repository.SaveAsync(requested);
        AssertLogicalApplicationEnvelope(saved, "one");
        Assert.AreNotSame(requested.Tags, saved.Tags);

        requested.Tags[0] = "mutated-input";
        saved.Tags[0] = "mutated-save-result";
        var preferredEntity = preferred.Get("one")!;
        Assert.AreEqual("persisted", preferredEntity.Tags[0]);

        var queried = await repository.QueryFirstByStorageKeyAsync("one");
        Assert.IsNotNull(queried);
        AssertLogicalApplicationEnvelope(queried, "one");
        Assert.AreNotSame(preferredEntity, queried);
        Assert.AreNotSame(preferredEntity.Tags, queried.Tags);
        queried.Tags[0] = "mutated-query-result";
        Assert.AreEqual("persisted", preferredEntity.Tags[0]);

        await repository.SynchronizeAsync("one");
        var peerEntity = peer.Get("one");
        Assert.IsNotNull(peerEntity);
        Assert.AreEqual("persisted", peerEntity.Tags[0]);
        Assert.AreNotSame(preferredEntity.Tags, peerEntity.Tags);
    }

    [TestMethod]
    public async Task LogicalEnvelope_PreservesDeleteIdentityAfterCallerEdits()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        await using var repository = CreateRepository(preferred, peer);
        var saved = await repository.SaveAsync(
            new TestEntity { Key = "one", Value = "value" });

        saved.Key = "tampered-domain-key";
        saved.Envelope!.EnvelopeCacheKey = "tampered-envelope-key";
        var deleted = await repository.DeleteAsync(saved);

        Assert.IsTrue(deleted);
        Assert.IsNull(preferred.Get("tampered-domain-key"));
        Assert.IsTrue(ReadMetadata(preferred.Get("one")!).IsTombstone);
    }

    [TestMethod]
    public async Task PreferredFirstQueries_ShortCircuitWithoutMaterializingManyResults()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "match", Metadata(revision: 1));
        preferred.Seed("two", "match", Metadata(revision: 2));
        var options = Options();
        options.MaximumSynchronizationScanItems = 1;
        await using var repository = CreateRepository(preferred, peer, options);

        var queried = await repository.QueryFirstAsync(entity => entity.Value == "match");
        var searched = await repository.SearchFirstAsync(entity => entity.Value == "match");

        Assert.IsNotNull(queried);
        Assert.IsNotNull(searched);
        AssertLogicalApplicationEnvelope(queried, queried.Key);
        AssertLogicalApplicationEnvelope(searched, searched.Key);
        Assert.AreEqual(1, preferred.QueryFirstCallCount);
        Assert.AreEqual(1, preferred.SearchFirstCallCount);
        Assert.AreEqual(0, preferred.QueryManyCallCount);
        Assert.AreEqual(0, preferred.SearchManyCallCount);
        Assert.AreEqual(0, peer.QueryFirstCallCount);
        Assert.AreEqual(0, peer.SearchFirstCallCount);
    }

    [TestMethod]
    public async Task CleanWhenInvalidated_FirstQueryUsesPreferredShortCircuit()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "match", Metadata(revision: 1));
        var options = Options(revisionCheckMode: RevisionCheckMode.WhenInvalidated);
        await using var repository = CreateRepository(preferred, peer, options);

        var result = await repository.QueryFirstAsync(entity => entity.Value == "match");

        Assert.IsNotNull(result);
        AssertLogicalApplicationEnvelope(result, "one");
        Assert.AreEqual(1, preferred.QueryFirstCallCount);
        Assert.AreEqual(0, preferred.QueryManyCallCount);
        Assert.AreEqual(0, peer.QueryFirstCallCount);
    }

    [TestMethod]
    public async Task PeerAuthoritativeRead_PreservesButDoesNotCertifyPreferredAheadOrPeerMissing()
    {
        foreach (var peerMissing in new[] { false, true })
        {
            var preferred = new FakeMetadataRepository("preferred");
            var peer = new FakeMetadataRepository("peer");
            preferred.Seed("one", "safe-local", Metadata(revision: 20));
            if (!peerMissing)
            {
                peer.Seed("one", "older-peer", Metadata(revision: 10));
            }

            var reported = new TaskCompletionSource<ResilientRepositoryFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var options = Options(
                revisionCheckMode: RevisionCheckMode.WhenInvalidated,
                mutationAuthority: MutationAuthority.PeerAuthoritativeReadOnly);
            options.FailureHandler = failure => reported.TrySetResult(failure);
            await using var repository = CreateRepository(preferred, peer, options);
            await repository.NotifyChangedAsync(
                new RepositoryChangeSignal("tests", "one", 21, 7));

            var result = await repository.QueryFirstByStorageKeyAsync("one");
            var failure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsNotNull(result);
            Assert.AreEqual("safe-local", result.Value);
            Assert.AreEqual(0, preferred.MetadataSaveCallCount);
            Assert.AreEqual(ResilientRepositoryOperation.Read, failure.Operation);
            StringAssert.Contains(failure.Exception.Message, "cannot be certified current");
            var state = await repository.GetSynchronizationStateAsync();
            Assert.IsTrue(state.DirtyStorageKeys.ContainsKey("one"));
        }
    }

    [TestMethod]
    public async Task PeerAuthoritativeSynchronization_ConflictsWhenPreferredAheadOrPeerMissing()
    {
        foreach (var peerMissing in new[] { false, true })
        {
            var preferred = new FakeMetadataRepository("preferred");
            var peer = new FakeMetadataRepository("peer");
            preferred.Seed("one", "safe-local", Metadata(revision: 20));
            if (!peerMissing)
            {
                peer.Seed("one", "older-peer", Metadata(revision: 10));
            }

            var options = Options(
                revisionCheckMode: RevisionCheckMode.WhenInvalidated,
                mutationAuthority: MutationAuthority.PeerAuthoritativeReadOnly);
            await using var repository = CreateRepository(preferred, peer, options);
            await repository.NotifyChangedAsync(
                new RepositoryChangeSignal("tests", "one", 21, 7));

            var result = await repository.SynchronizeAsync("one");

            Assert.AreEqual(1, result.Conflicts);
            Assert.AreEqual(0, result.PreferredRefreshes);
            Assert.AreEqual("safe-local", preferred.Get("one")!.Value);
            var state = await repository.GetSynchronizationStateAsync();
            Assert.IsTrue(state.DirtyStorageKeys.ContainsKey("one"));
        }
    }

    [TestMethod]
    public async Task ExactRead_RejectsEndpointEntityWithDifferentLogicalKey()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("other", "wrong", Metadata(revision: 1));
        preferred.QueryFirstByStorageKeyOverride = _ => preferred.Get("other");
        await using var repository = CreateRepository(preferred, peer);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await repository.QueryFirstByStorageKeyAsync("requested"));

        StringAssert.Contains(exception.Message, "returned logical key 'other'");
        StringAssert.Contains(exception.Message, "requested key 'requested'");
    }

    [TestMethod]
    public async Task SaveAndSynchronize_AcceptProviderOwnedPhysicalKeysDifferentFromLogicalKey()
    {
        var preferred = new FakeMetadataRepository(
            "preferred",
            StorageDurabilityMode.Durable,
            key => $"preferred-physical-{key}");
        var peer = new FakeMetadataRepository(
            "peer",
            StorageDurabilityMode.Durable,
            key => $"peer-physical-{key}");
        await using var repository = CreateRepository(preferred, peer);

        var saved = await repository.SaveAsync(new TestEntity { Key = "one", Value = "value" });
        var synchronization = await repository.SynchronizeAsync("one");

        Assert.AreEqual("one", saved.Key);
        Assert.AreEqual("preferred-physical-one", preferred.Get("one")!.Envelope!.EnvelopeCacheKey);
        Assert.AreEqual("peer-physical-one", peer.Get("one")!.Envelope!.EnvelopeCacheKey);
        Assert.AreEqual(1, synchronization.PeerCommits);
        Assert.AreEqual(1, synchronization.Acknowledgements);
    }

    [TestMethod]
    public async Task PreferredFastPath_DelegatesQueryAndSearchWithoutEnumeratingPeer()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        preferred.Seed("one", "alpha", Metadata(revision: 1));
        preferred.Seed("two", "beta", Metadata(revision: 2));
        await using var repository = CreateRepository(preferred, peer);

        var queried = await CollectAsync(repository.QueryManyAsync(entity => entity.Value == "alpha"));
        var searched = await CollectAsync(repository.SearchManyAsync(entity => entity.Value == "beta"));

        CollectionAssert.AreEqual(new[] { "one" }, queried.Select(entity => entity.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "two" }, searched.Select(entity => entity.Key).ToArray());
        AssertLogicalApplicationEnvelope(queried[0], "one");
        AssertLogicalApplicationEnvelope(searched[0], "two");
        Assert.AreEqual(1, preferred.QueryManyCallCount);
        Assert.AreEqual(1, preferred.SearchManyCallCount);
        Assert.AreEqual(0, peer.QueryManyCallCount);
        Assert.AreEqual(0, peer.SearchManyCallCount);
    }

    [TestMethod]
    public async Task DependencyInjection_ComposesKeyedEndpoints_AndRegistersDefaultRepository()
    {
        var preferred = new FakeMetadataRepository("preferred");
        var peer = new FakeMetadataRepository("peer");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IRepository<TestEntity>>("preferred", preferred);
        services.AddKeyedSingleton<IRepository<TestEntity>>("peer", peer);
        services.AddResilientRepository(
            "resilient",
            "preferred",
            "peer",
            Mapper,
            Options());

        await using var provider = services.BuildServiceProvider();
        var defaultRepository = provider.GetRequiredService<IRepository<TestEntity>>();
        var keyedRepository = provider.GetRequiredKeyedService<IRepository<TestEntity>>("resilient");
        var resilientRepository = provider.GetRequiredService<IResilientRepository<TestEntity>>();

        Assert.AreSame(defaultRepository, keyedRepository);
        Assert.AreSame(defaultRepository, resilientRepository);
    }

    [TestMethod]
    public void MetadataCodec_RejectsForeignMalformedAndOversizedPayloads()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ResilientEnvelopeMetadataCodec.Decode([1, 2, 3, 4, 5]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ResilientEnvelopeMetadataCodec.Decode([(byte)'I', (byte)'D', (byte)'B', (byte)'R', 0xc1]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ResilientEnvelopeMetadataCodec.Decode(new byte[1025]));
    }

    private static ResilientRepository<TestEntity> CreateRepository(
        FakeMetadataRepository preferred,
        FakeMetadataRepository peer,
        ResilientRepositoryOptions<TestEntity>? options = null)
        => new(
            preferred,
            peer,
            Mapper,
            options ?? Options());

    private static ResilientRepositoryOptions<TestEntity> Options(
        RevisionCheckMode revisionCheckMode = RevisionCheckMode.PreferredOnly,
        MutationAuthority mutationAuthority = MutationAuthority.PreferredWritable)
        => new()
        {
            DatasetId = "tests",
            RevisionCheckMode = revisionCheckMode,
            MutationAuthority = mutationAuthority,
            EnableBackgroundSynchronization = false
        };

    private static ResilientEnvelopeMetadata Metadata(
        long revision,
        bool isTombstone = false,
        Guid pendingMutationId = default,
        PendingMutationKind pendingMutationKind = PendingMutationKind.None,
        Guid lastAppliedMutationId = default)
        => new(
            ResilientEnvelopeMetadata.CurrentFormatVersion,
            revision,
            isTombstone,
            pendingMutationId,
            pendingMutationKind,
            lastAppliedMutationId);

    private static ResilientEnvelopeMetadata ReadMetadata(TestEntity entity)
        => ResilientEnvelopeMetadataCodec.Decode(entity.Envelope!);

    private static void AssertLogicalApplicationEnvelope(TestEntity entity, string storageKey)
    {
        Assert.IsNotNull(entity.Envelope);
        Assert.AreEqual(storageKey, entity.Envelope.EnvelopeCacheKey);
        Assert.IsNull(entity.Envelope.Location);
        Assert.IsNull(entity.Envelope.ExtensionMetadata);
        Assert.AreSame(entity, entity.Envelope.ObjectInstance);
    }

    private static async Task<IReadOnlyList<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
