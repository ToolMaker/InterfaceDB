using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class CommittedRevisionObservationTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(42L)]
    public async Task MaterializedRevisionSeedsUnknownKeyIncludingLegacyZero(long revision)
    {
        await using var fixture = new Fixture(true, enabled: false);
        var coordinator = Create(fixture);
        coordinator.ObserveCommittedRevision("key", revision);
        var result = await coordinator.ReadCommittedRevisionAsync("key", UnexpectedRead, CancellationToken.None);
        Assert.AreEqual<long?>(revision, result);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateReadCannotReplacePublishedRevisionOrDeletion(bool deleted)
    {
        await using var fixture = new Fixture(true, enabled: false);
        var coordinator = Create(fixture);
        coordinator.ObserveCommittedRevision("key", 10);
        coordinator.Committed("key", deleted ? null : 20);
        Parallel.For(0, 64, _ => coordinator.ObserveCommittedRevision("key", 10));
        var result = await coordinator.ReadCommittedRevisionAsync("key", UnexpectedRead, CancellationToken.None);
        Assert.AreEqual<long?>(deleted ? null : 20, result);
    }

    private static ValueTask<byte[]?> UnexpectedRead(CancellationToken token)
        => throw new AssertFailedException("A seeded revision must not reread the stored payload.");

    [TestMethod]
    public async Task NegativeRevisionCannotBeMistakenForDeletion()
    {
        await using var fixture = new Fixture(true, enabled: false);
        var coordinator = Create(fixture);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => coordinator.ObserveCommittedRevision("key", -1));
        coordinator.ObserveCommittedRevision("key", 42);
        Assert.AreEqual<long?>(42, await coordinator.ReadCommittedRevisionAsync("key", UnexpectedRead, CancellationToken.None));
    }

    [TestMethod]
    public async Task MetadataPreparationDoesNotMutateTheBorrowedCommittedSnapshot()
    {
        await using var fixture = new Fixture(true, enabled: false);
        var metadata = (Idb.Libraries.Abstractions.Services.Repository.IEnvelopeMetadataRepository<IRecord>)fixture.Repository;
        var saved = await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "metadata", Value = 7 }, null, [1]);
        var committed = saved.Envelope!;
        var lifecycle = fixture.Services.GetRequiredService<IEnvelopeLifecycle<IRecord>>();
        var candidate = await lifecycle.WrapAsync(saved);
        candidate.ExtensionMetadata = [2];
        EnvelopeWriteCoordinator<IRecord>.SetCondition(candidate,
            new(committed.RevisionTimestampUtcMicroseconds, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
        var coordinator = Create(fixture);
        using var guard = await coordinator.AcquireAsync(committed.EnvelopeCacheKey, CancellationToken.None);

        var prepared = await coordinator.PrepareWithCurrentAsync(candidate, false, committed.RevisionTimestampUtcMicroseconds,
            _ => ValueTask.FromResult<IRecord?>(saved), CancellationToken.None);

        // No commit follows: abandoning this prepared write must leave the shared snapshot intact.
        Assert.AreNotSame(committed, prepared);
        CollectionAssert.AreEqual(new byte[] { 1 }, committed.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 2 }, prepared.ExtensionMetadata);
        Assert.AreSame(committed, saved.Envelope);
        prepared.ObjectInstance!.Value = 99;
        Assert.AreEqual(7, saved.Value);
    }

    private static EnvelopeWriteCoordinator<IRecord> Create(Fixture fixture) => new(fixture.Root,
        fixture.Services.GetRequiredService<IObjectSerializer<IRecord>>(),
        fixture.Services.GetRequiredService<IEnvelopeLifecycle<IRecord>>());
}
