using System.Collections.Generic;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using IdbConsole.Tests.Repository.Shared;
using Xunit;

namespace IdbConsole.Tests.Repository.Indexes;

public class FileSystemRepositoryIndexTests : FileSystemRepositoryTestBase
{
    [Fact]
    public async Task SaveAsync_PublishesIndexChange()
    {
        var writer = new StubObjectWriter(DefaultWriteResult);
        var reader = new StubObjectReader().AddResult(DefaultReadResult, exists: false);
        var repository = CreateRepository(writer, reader);

        IndexChange? observed = null;
        repository.Subscribe(new IndexSubscription(typeof(ITestContract), "Length"), change => observed = change);

        var result = await repository.SaveAsync<ITestContract>(SampleContract, "custom-id", CancellationToken.None);

        Assert.Same(DefaultWriteResult, result);
        Assert.NotNull(observed);
        Assert.Equal(IndexChangeKind.Updated, observed!.Kind);
        Assert.Equal("custom-id", writer.LastId);
    }

    [Fact]
    public async Task QueryAsync_UsesRegisteredIndex()
    {
        var repository = CreateRepositoryWithLengthIndex();
        var personA = CreateContract("alpha");
        var personB = CreateContract("beta");

        await repository.SaveAsync<ITestContract>(personA);
        await repository.SaveAsync<ITestContract>(personB);

        var query = new IndexQuery(typeof(ITestContract), "Length", personA.Id.Length);
        var results = await repository.QueryAsync<ITestContract>(query);

        Assert.Single(results);
        Assert.Equal(personA.Id, results[0].Id);
    }

    [Fact]
    public void ConfigureIndexes_AddsMultipleConfigurations()
    {
        var repository = CreateRepositoryWithLengthIndex();
        var configs = new IRepositoryIndexConfiguration[]
        {
            CreateLengthIndex(),
            new TestIndexConfiguration(typeof(ITestContract), "MiscIndex", RepositoryIndexKind.Custom)
        };

        repository.ConfigureIndexes(configs);

        var retrieved = repository.GetIndexConfigurations(typeof(ITestContract));

        Assert.Equal(2, retrieved.Count);
        Assert.Contains(configs[0], retrieved);
        Assert.Contains(configs[1], retrieved);
    }

    [Fact]
    public async Task Subscribe_WithKeyFiltersEvents()
    {
        var repository = CreateRepositoryWithLengthIndex();

        IndexChange? observed = null;
        repository.Subscribe(
            new IndexSubscription(typeof(ITestContract), "Length", SampleContract.Id.Length),
            change => observed = change);

        await repository.SaveAsync<ITestContract>(SampleContract);

        Assert.NotNull(observed);
        Assert.Equal(SampleContract.Id, ((ITestContract)observed!.Entity!).Id);
    }

    [Fact]
    public async Task TryGetSortedListIndex_ReturnsTypedIndex()
    {
        var repository = CreateRepositoryWithLengthIndex();

        var shortId = CreateContract("a");
        var longerId = CreateContract("three");

        await repository.SaveAsync<ITestContract>(shortId);
        await repository.SaveAsync<ITestContract>(longerId);

        var success = repository.TryGetSortedListIndex<ITestContract, int>("Length", out var index);

        Assert.True(success);
        Assert.NotNull(index);
        Assert.Equal(2, index!.Count);
        Assert.Equal(shortId.Id, index.Values[0][0].Id);
        Assert.Equal(longerId.Id, index.Values[1][0].Id);
    }

    [Fact]
    public void TryGetSortedListIndex_WhenIndexIsMissing_ReturnsFalse()
    {
        var repository = CreateRepository(
            new StubObjectWriter(DefaultWriteResult),
            new StubObjectReader().AddResult(DefaultReadResult, exists: true),
            configureLengthIndex: false);

        var success = repository.TryGetSortedListIndex<ITestContract, int>("Length", out var index);

        Assert.False(success);
        Assert.Null(index);
    }

    private sealed class TestIndexConfiguration : IRepositoryIndexConfiguration
    {
        public TestIndexConfiguration(
            Type contractType,
            string name,
            RepositoryIndexKind kind,
            IReadOnlyDictionary<string, object?>? settings = null)
        {
            ContractType = contractType;
            Name = name;
            Kind = kind;
            Settings = settings ?? new Dictionary<string, object?>();
        }

        public Type ContractType { get; }
        public string Name { get; }
        public RepositoryIndexKind Kind { get; }
        public IReadOnlyDictionary<string, object?> Settings { get; }
    }
}
