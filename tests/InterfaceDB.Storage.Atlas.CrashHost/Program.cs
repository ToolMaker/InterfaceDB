using System.Diagnostics;
using Idb.Storage.AtlasCore;

// Test-only abrupt process termination: no store disposal/checkpoint is allowed at this cut.
if (args.Length != 2 || args[1] is not ("children-durable" or "ownership-published")) return 2;
var options = new AtlasCoreOptions
{
    Partitioning = new() { EnableAutomaticSplitting = false, SplitAboveLiveRecords = 4,
        MinimumChildRecords = 1, MinimumFreeSpaceReserveBytes = 0 },
    SplitTestHook = stage =>
    {
        if (stage != args[1]) return;
        Console.WriteLine("CUT:" + stage); Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
};
await using var store = await AtlasCoreStore.OpenAsync(args[0], options);
await store.SplitBucketAsync("ABCF000");
return 3; // Reaching this means the intended crash cut was not exercised.
