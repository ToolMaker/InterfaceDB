using Idb.Libraries.Implementation.Cache;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class EnvelopeConcurrentDictionaryTests
{
    [TestMethod]
    public void AddOrUpdate_WhenPhysicalKeyChangesGroup_RemovesOldMembership()
    {
        var cache = new EnvelopeConcurrentDictionary<string, int>();
        cache.AddOrUpdate("physical", "old-group", 1);

        cache.AddOrUpdate("physical", "new-group", 2);

        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(0, cache.GetGroupCount("old-group"));
        Assert.IsFalse(cache.TryGetGroup("old-group", out _));
        Assert.AreEqual(1, cache.GetGroupCount("new-group"));
        CollectionAssert.AreEqual(new[] { 2 }, cache.GetGroup("new-group").ToArray());
    }

    [TestMethod]
    public void LargeGroup_PromotesAndShrinksWithoutLosingMembership()
    {
        var cache = new EnvelopeConcurrentDictionary<string, int>();
        for (var index = 0; index < 64; index++)
        {
            cache.AddOrUpdate($"physical-{index}", "shared", index);
        }

        for (var index = 0; index < 64; index += 2)
        {
            Assert.IsTrue(cache.Remove($"physical-{index}"));
        }

        Assert.AreEqual(32, cache.GetGroupCount("shared"));
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 64).Where(static index => (index & 1) != 0).ToArray(),
            cache.GetGroup("shared").ToArray());

        for (var index = 1; index < 64; index += 2)
        {
            Assert.IsTrue(cache.Remove($"physical-{index}"));
        }

        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, cache.GetGroupCount("shared"));
        Assert.IsFalse(cache.TryGetGroup("shared", out _));
    }

    [TestMethod]
    public void ParallelIndependentGroups_AddAndRemoveRemainConsistent()
    {
        const int itemCount = 5_000;
        var cache = new EnvelopeConcurrentDictionary<string, int>();

        Parallel.For(0, itemCount, index =>
            cache.AddOrUpdate($"physical-{index}", $"group-{index}", index));

        Assert.AreEqual(itemCount, cache.Count);
        Parallel.For(0, itemCount, index => Assert.IsTrue(cache.Remove($"physical-{index}")));

        Assert.AreEqual(0, cache.Count);
        for (var index = 0; index < itemCount; index++)
        {
            Assert.AreEqual(0, cache.GetGroupCount($"group-{index}"));
        }
    }
}
