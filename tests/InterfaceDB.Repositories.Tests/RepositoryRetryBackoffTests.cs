using Idb.Commercial.Repositories;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class RepositoryRetryBackoffTests
{
    [TestMethod]
    [DataRow(5)]
    [DataRow(30)]
    [DataRow(300)]
    public void Retries_grow_are_bounded_and_reset_to_configured_initial_delay(int seconds)
    {
        var initial = TimeSpan.FromSeconds(seconds);
        var schedule = new RepositoryRetryBackoff(initial, () => 1);
        Assert.AreEqual(initial, schedule.NextDelay());
        for (var n=1; n<100; n++)
            Assert.AreEqual(TimeSpan.FromSeconds(Math.Min(Math.Max(120,seconds),seconds*Math.Pow(2,Math.Min(n,30)))),schedule.NextDelay());
        schedule.Reset(); Assert.AreEqual(initial,schedule.NextDelay());
    }

    [TestMethod]
    public void Jitter_never_creates_zero_delay_or_exceeds_the_cap()
    {
        var schedule = new RepositoryRetryBackoff(TimeSpan.FromSeconds(5), () => 0);
        Assert.AreEqual(TimeSpan.FromSeconds(5),schedule.NextDelay());
        Assert.AreEqual(TimeSpan.FromSeconds(8),schedule.NextDelay());
        for(var n=0;n<100;n++) Assert.IsTrue(schedule.NextDelay() is var delay && delay >= TimeSpan.FromSeconds(16) && delay <= TimeSpan.FromSeconds(120));
    }
}
