using IdbConsole.Tests.Repository.Shared;
using Xunit;

namespace IdbConsole.Tests.Repository.Database;

public class FileSystemRepositoryDatabaseTests : FileSystemRepositoryTestBase
{
    [Fact]
    public async Task SaveAsync_ForwardsToWriter()
    {
        var writer = new StubObjectWriter(DefaultWriteResult);
        var reader = new StubObjectReader().AddResult(DefaultReadResult, exists: false);
        var repository = CreateRepository(writer, reader);

        var result = await repository.SaveAsync<ITestContract>(SampleContract, "custom-id", CancellationToken.None);

        Assert.Same(DefaultWriteResult, result);
        Assert.Equal(SampleContract, writer.LastEntity);
        Assert.Equal("custom-id", writer.LastId);
        Assert.Equal(CancellationToken.None, writer.LastCancellationToken);
    }

    [Fact]
    public async Task SaveAsync_WhenEntityIsNull_Throws()
    {
        var repository = CreateRepository(
            new StubObjectWriter(DefaultWriteResult),
            new StubObjectReader(),
            configureLengthIndex: false);

        await Assert.ThrowsAsync<ArgumentNullException>(() => repository.SaveAsync<ITestContract>(null!, null).AsTask());
    }

    [Fact]
    public async Task SaveAsync_WhenContractIsNotInterface_Throws()
    {
        var repository = CreateRepository(
            new StubObjectWriter(DefaultWriteResult),
            new StubObjectReader(),
            configureLengthIndex: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(new NonContract(), null).AsTask());
    }

    [Fact]
    public async Task LoadAsync_ForwardsToReaderAndReturnsResult()
    {
        var writer = new StubObjectWriter(DefaultWriteResult);
        var reader = new StubObjectReader().AddResult(DefaultReadResult, exists: true);
        var repository = CreateRepository(writer, reader);

        var result = await repository.LoadAsync<ITestContract>("custom-id", "v2", CancellationToken.None);

        Assert.Same(DefaultReadResult, result);
        Assert.Equal("custom-id", reader.LastReadId);
        Assert.Equal("v2", reader.LastReadVersion);
        Assert.Equal(CancellationToken.None, reader.LastReadCancellationToken);
    }

    [Fact]
    public async Task ExistsAsync_ForwardsToReader()
    {
        var writer = new StubObjectWriter(DefaultWriteResult);
        var reader = new StubObjectReader().AddResult(DefaultReadResult, exists: true);
        var repository = CreateRepository(writer, reader);

        var exists = await repository.ExistsAsync<ITestContract>("custom-id", "v3", CancellationToken.None);

        Assert.True(exists);
        Assert.Equal("custom-id", reader.LastExistsId);
        Assert.Equal("v3", reader.LastExistsVersion);
        Assert.Equal(CancellationToken.None, reader.LastExistsCancellationToken);
    }
}
