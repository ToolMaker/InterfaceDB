using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Indexes;
using Idb.Libraries.Implementation.Services.Repository;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdbConsolePlayground;

internal static class StorageDemo
{
    private const string DefaultStorageFolderName = "StorageDemo";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = StorageDemoOptions.Parse(args);
        var storageRoot = ResolveStorageRoot(options.RootDirectory);

        Console.WriteLine($"Using storage root: {storageRoot}");
        Console.WriteLine("Initializing repository...");

        using var provider = BuildServiceProvider(storageRoot);
        var repository = provider.GetRequiredService<CachingRepositoryNode>();

        Console.WriteLine("Indexes:");
        foreach (var index in repository.GetIndexConfigurations(typeof(IStoragePerson)))
        {
            Console.WriteLine($" - {index.Name} ({index.Kind})");
        }

        Console.WriteLine();
        Console.WriteLine("Interactive commands ready. Type 'help' for a list of commands.");
        Console.WriteLine("Press Ctrl+C or type 'exit' to leave.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        await RunInteractiveLoopAsync(repository, cts.Token).ConfigureAwait(false);
        return 0;
    }

    private static ServiceProvider BuildServiceProvider(string storageRoot)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        services.AddLocalScopeStorage(builder =>
        {
            builder.RootDirectory = new DirectoryInfo(storageRoot);
            builder.AssembliesToScan = new[] { typeof(IStoragePerson).Assembly };
        });

        services.AddDefaultObjectWriterAndReader();

        services.AddSingleton(sp =>
        {
            var writer = sp.GetRequiredService<IObjectWriter>();
            var reader = sp.GetRequiredService<IObjectReader>();
            var inner = new FileSystemRepository(writer, reader);

            var indexes = new IRepositoryIndexConfiguration[]
            {
                new SortedListIndexConfiguration<IStoragePerson, string>("ByLastName", p => p.LastName),
                new SortedListIndexConfiguration<IStoragePerson, string>("ByFirstName", p => p.FirstName),
                new SortedListIndexConfiguration<IStoragePerson, int>("ByAge", p => p.Age),
            };

            var cache = new CachingRepositoryNode(inner);
            cache.ConfigureIndexes(indexes);
            return cache;
        });

        return services.BuildServiceProvider();
    }

    private static async Task RunInteractiveLoopAsync(
        CachingRepositoryNode repository,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("storage> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                continue;
            }

            var parts = Tokenize(input);
            if (parts.Count == 0)
            {
                continue;
            }

            var command = parts[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "help":
                        PrintHelp();
                        break;

                    case "add":
                        await HandleAddAsync(repository, parts).ConfigureAwait(false);
                        break;

                    case "load":
                        await HandleLoadAsync(repository, parts).ConfigureAwait(false);
                        break;

                    case "exists":
                        await HandleExistsAsync(repository, parts).ConfigureAwait(false);
                        break;

                    case "query-age":
                        await HandleQueryAsync(repository, "ByAge", ParseInt(parts, 1)).ConfigureAwait(false);
                        break;

                    case "query-first":
                        await HandleQueryAsync(repository, "ByFirstName", RequireArgument(parts, 1)).ConfigureAwait(false);
                        break;

                    case "query-last":
                        await HandleQueryAsync(repository, "ByLastName", RequireArgument(parts, 1)).ConfigureAwait(false);
                        break;

                    case "snapshot":
                        HandleSnapshot(repository, parts);
                        break;

                    case "indexes":
                        PrintIndexes(repository);
                        break;

                    case "exit":
                    case "quit":
                        return;

                    default:
                        Console.WriteLine($"Unknown command '{command}'. Type 'help' for assistance.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  help                  Show this message.");
        Console.WriteLine("  add <id> <first> <last> <age>");
        Console.WriteLine("                        Add or update a person record.");
        Console.WriteLine("  load <id>             Load a person by identifier.");
        Console.WriteLine("  exists <id>           Check whether a record exists.");
        Console.WriteLine("  query-age <age>       List people by age (uses ByAge index).");
        Console.WriteLine("  query-first <name>    List people whose first-name bucket matches (ByFirstName).");
        Console.WriteLine("  query-last <name>     List people whose last-name bucket matches (ByLastName).");
        Console.WriteLine("  snapshot <index>      Dump all values for an index (ByFirstName | ByLastName | ByAge).");
        Console.WriteLine("  indexes               Display configured indexes.");
        Console.WriteLine("  exit                  Leave the demo.");
    }

    private static async Task HandleAddAsync(CachingRepositoryNode repository, IReadOnlyList<string> parts)
    {
        if (parts.Count < 5)
        {
            Console.WriteLine("Usage: add <id> <first> <last> <age>");
            return;
        }

        var id = parts[1];
        var first = parts[2];
        var last = parts[3];
        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
        {
            Console.WriteLine("Age must be an integer.");
            return;
        }

        var person = new StoragePerson(id, first, last, age);
        await repository.SaveAsync<IStoragePerson>(person, id, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Saved {first} {last} ({id})");
    }

    private static async Task HandleLoadAsync(CachingRepositoryNode repository, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: load <id>");
            return;
        }

        try
        {
            var result = await repository.LoadAsync<IStoragePerson>(parts[1], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var person = result.Value;
            Console.WriteLine($"{person.Id} => {person.FirstName} {person.LastName}, age {person.Age}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load failed: {ex.Message}");
        }
    }

    private static async Task HandleExistsAsync(CachingRepositoryNode repository, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: exists <id>");
            return;
        }

        var exists = await repository.ExistsAsync<IStoragePerson>(parts[1], cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(exists ? "Record exists." : "Record not found.");
    }

    private static async Task HandleQueryAsync(CachingRepositoryNode repository, string indexName, object key)
    {
        var query = new IndexQuery(typeof(IStoragePerson), indexName, key);
        var results = await repository.QueryAsync<IStoragePerson>(query, CancellationToken.None).ConfigureAwait(false);

        if (results.Count == 0)
        {
            Console.WriteLine("No matches.");
            return;
        }

        foreach (var person in results)
        {
            Console.WriteLine($"{person.Id} => {person.FirstName} {person.LastName}, age {person.Age}");
        }
    }

    private static void HandleSnapshot(CachingRepositoryNode repository, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: snapshot <indexName>");
            return;
        }

        var indexName = parts[1];
        if (string.Equals(indexName, "ByAge", StringComparison.OrdinalIgnoreCase))
        {
            if (repository.TryGetSortedListIndex<IStoragePerson, int>("ByAge", out var index))
            {
                foreach (var (age, bucket) in index!)
                {
                    foreach (var person in bucket)
                    {
                        Console.WriteLine($"[{age}] {person.Id} => {person.FirstName} {person.LastName}");
                    }
                }
            }
            else
            {
                Console.WriteLine("No sorted index named 'ByAge' has been configured.");
            }

            return;
        }

        if (string.Equals(indexName, "ByFirstName", StringComparison.OrdinalIgnoreCase))
        {
            PrintStringIndex(repository, "ByFirstName");
            return;
        }

        if (string.Equals(indexName, "ByLastName", StringComparison.OrdinalIgnoreCase))
        {
            PrintStringIndex(repository, "ByLastName");
            return;
        }

        Console.WriteLine($"Unknown index '{indexName}'.");
    }

    private static void PrintIndexes(CachingRepositoryNode repository)
    {
        var indexes = repository.GetIndexConfigurations(typeof(IStoragePerson));
        if (indexes.Count == 0)
        {
            Console.WriteLine("No indexes configured.");
            return;
        }

        foreach (var index in indexes)
        {
            Console.WriteLine($"- {index.Name} ({index.Kind})");
        }
    }

    private static void PrintStringIndex(CachingRepositoryNode repository, string name)
    {
        if (repository.TryGetSortedListIndex<IStoragePerson, string>(name, out var index))
        {
            foreach (var (key, bucket) in index!)
            {
                foreach (var person in bucket)
                {
                    Console.WriteLine($"[{key}] {person.Id} => {person.FirstName} {person.LastName}, age {person.Age}");
                }
            }
        }
        else
        {
            Console.WriteLine($"No sorted index named '{name}' has been configured.");
        }
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Count > 0)
                {
                    tokens.Add(new string(current.ToArray()));
                    current.Clear();
                }

                continue;
            }

            current.Add(ch);
        }

        if (current.Count > 0)
        {
            tokens.Add(new string(current.ToArray()));
        }

        return tokens;
    }

    private static string RequireArgument(IReadOnlyList<string> parts, int index)
    {
        if (parts.Count <= index)
        {
            throw new ArgumentException("Missing argument.");
        }

        return parts[index];
    }

    private static int ParseInt(IReadOnlyList<string> parts, int index)
    {
        var value = RequireArgument(parts, index);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"'{value}' is not a valid integer.");
        }

        return parsed;
    }

    private static string ResolveStorageRoot(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            Directory.CreateDirectory(overrideRoot);
            return Path.GetFullPath(overrideRoot);
        }

        var path = Path.Combine(AppContext.BaseDirectory, DefaultStorageFolderName);
        Directory.CreateDirectory(path);
        return path;
    }

    [StorageScope(VersionTag = "v1")]
    public interface IStoragePerson
    {
        string Id { get; }
        string FirstName { get; }
        string LastName { get; }
        int Age { get; }
    }

    [MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
    public sealed record StoragePerson(
        [property: Key(0)] string Id,
        [property: Key(1)] string FirstName,
        [property: Key(2)] string LastName,
        [property: Key(3)] int Age) : IStoragePerson;

    private sealed record StorageDemoOptions(string? RootDirectory)
    {
        public static StorageDemoOptions Parse(string[] args)
        {
            string? root = null;

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --root option.");
                    }

                    root = args[++i];
                }
                else
                {
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            return new StorageDemoOptions(root);
        }
    }
}
