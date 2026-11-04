using System.Text;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class ReviewRegressionAtlasRawTests
{
    [TestMethod]
    public async Task ReviewRegression_Atlas_RejectedKeyInsideTransactionFaultsStore()
    {
        using var root = new HybridPartitionTests.Temp();
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, FixedLayoutTests.Options());
        var lines = new List<string>();
        try { await store.SaveAsync("ZZZ/single", new byte[] { 1 }); }
        catch (Exception ex) { lines.Add("single save with unknown prefix: " + ex.GetType().Name + " (expected rejection)"); }
        try { await store.SaveAsync("000/after-single", new byte[] { 1 }); lines.Add("next save after single rejection: OK"); }
        catch (Exception ex) { lines.Add("next save after single rejection FAILED: " + ex.Message); }
        try
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("000/ok", new byte[] { 1 });
            await transaction.SaveAsync("ZZZ/bad", new byte[] { 2 });
            await transaction.CommitAsync();
            lines.Add("transaction committed (unexpected)");
        }
        catch (Exception ex) { lines.Add("transaction with unknown prefix: " + ex.GetType().Name); }
        try { await store.SaveAsync("001/after-transaction", new byte[] { 1 }); lines.Add("next save after transaction rejection: OK"); }
        catch (Exception ex) { lines.Add("next save after transaction rejection FAILED: " + ex.GetType().Name + ": " + ex.Message); }
        Assert.IsFalse(lines.Any(line => line.Contains("FAILED")), string.Join("\n", lines));

    }
}
