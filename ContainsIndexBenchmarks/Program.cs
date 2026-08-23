using BenchmarkDotNet.Running;
using ContainsIndexBenchmarks;

var arguments = args.Select(static value => value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
if (arguments.Count == 0)
{
    arguments.Add("--validate");
    arguments.Add("--matrix");
}

var failed = false;
if (arguments.Contains("--validate") || arguments.Contains("--all"))
{
    failed |= ValidationSuite.Run(Console.Out) != 0;
}

if (arguments.Contains("--matrix") || arguments.Contains("--all"))
{
    var report = BenchmarkSuite.RunMatrix(Console.Out, includeMillion: !arguments.Contains("--no-million"));
    BenchmarkSuite.WriteReport(report);
    Console.WriteLine();
    Console.WriteLine("Results written to ContainsIndexBenchmarks/results/RESULTS.md and companion CSV files.");
}

if (arguments.Contains("--bdn") || arguments.Contains("--all"))
{
    BenchmarkRunner.Run<FocusedQueryBenchmarks>();
    BenchmarkRunner.Run<KeyEncodingBenchmarks>();
}

Environment.ExitCode = failed ? 1 : 0;
