using System.Threading.Tasks;

namespace IdbConsolePlayground;

internal static class Program
{
    private static async Task<int> Main(string[] args)
        => await StorageDemo.RunAsync(args).ConfigureAwait(false);
}
