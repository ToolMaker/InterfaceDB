using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using RepoTcpConsoleApp.Local;
using RepoTcpHost.Configuration;
using RepoTcpHost.Domain;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;

namespace RepoTcpConsoleApp;

internal static class TcpConsoleApp
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 5051;

    public static async Task RunAsync(string[] args)
    {
        var options = TcpConsoleOptions.Parse(args);
        var serializer = new MsgPackSerializer(new SerializationConfig());

        LocalRepository localRepository = LocalRepository.Create(options.StorageRoot);
        await using var session = new TcpClientSession(serializer);

        var defaultHost = string.IsNullOrWhiteSpace(options.Host) ? DefaultHost : options.Host!;
        var defaultPort = options.Port ?? DefaultPort;

        Console.WriteLine($"Default server endpoint: {defaultHost}:{defaultPort}");
        Console.WriteLine($"Local cache root: {localRepository.RootPath}");
        Console.WriteLine("Type 'help' for available commands.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        if (options.AutoConnect)
        {
            await TryConnectAsync(session, defaultHost, defaultPort, cts.Token).ConfigureAwait(false);
        }

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Console.Write("tcp> ");
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

                        case "status":
                            PrintStatus(session);
                            break;

                        case "connect":
                        {
                            var host = parts.Count > 1 ? parts[1] : defaultHost;
                            var port = parts.Count > 2
                                ? ParseInt(parts[2])
                                : defaultPort;
                            await TryConnectAsync(session, host, port, cts.Token).ConfigureAwait(false);
                            break;
                        }

                        case "disconnect":
                            await session.DisconnectAsync().ConfigureAwait(false);
                            Console.WriteLine("Disconnected.");
                            break;

                        case "ping":
                            await HandlePingAsync(session, cts.Token).ConfigureAwait(false);
                            break;

                        case "push":
                            await HandlePushAsync(session, parts, cts.Token).ConfigureAwait(false);
                            break;

                        case "seed":
                            await HandleSeedAsync(session, parts, cts.Token).ConfigureAwait(false);
                            break;

                        case "fetch":
                            await HandleFetchAsync(session, localRepository, parts, cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-indexes":
                            PrintCacheIndexes(localRepository);
                            break;

                        case "cache-load":
                            await HandleCacheLoadAsync(localRepository, parts, cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-exists":
                            await HandleCacheExistsAsync(localRepository, parts, cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-query-age":
                            await HandleCacheQueryAsync(localRepository, "ByAge", ParseInt(Require(parts, 1)), cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-query-first":
                            await HandleCacheQueryAsync(localRepository, "ByFirstName", Require(parts, 1), cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-query-last":
                            await HandleCacheQueryAsync(localRepository, "ByLastName", Require(parts, 1), cts.Token).ConfigureAwait(false);
                            break;

                        case "cache-snapshot":
                            HandleCacheSnapshot(localRepository, parts);
                            break;

                        case "cache-list":
                            HandleCacheSnapshot(localRepository, new List<string> { "cache-snapshot", "ByLastName" });
                            break;

                        case "cache-clear":
                            localRepository = await ResetLocalCacheAsync(localRepository, options.StorageRoot).ConfigureAwait(false);
                            break;

                        case "exit":
                        case "quit":
                            cts.Cancel();
                            break;

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
        finally
        {
            await localRepository.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Network commands:");
        Console.WriteLine("  help                       Show this message.");
        Console.WriteLine("  status                     Display connection state.");
        Console.WriteLine("  connect [host] [port]      Connect to the TCP host (defaults applied when omitted).");
        Console.WriteLine("  disconnect                 Close the active connection.");
        Console.WriteLine("  ping                       Round-trip test request.");
        Console.WriteLine("  push <id> <first> <last> <age>");
        Console.WriteLine("                             Send a single person to the server.");
        Console.WriteLine("  seed [count]               Generate and send sample people (default count = 5).");
        Console.WriteLine("  fetch <count>              Retrieve people and cache them locally.");
        Console.WriteLine();
        Console.WriteLine("Local cache commands:");
        Console.WriteLine("  cache-indexes              List configured indexes.");
        Console.WriteLine("  cache-load <id>            Load a cached person by identifier.");
        Console.WriteLine("  cache-exists <id>          Check local cache for a person.");
        Console.WriteLine("  cache-query-age <age>");
        Console.WriteLine("  cache-query-first <name>");
        Console.WriteLine("  cache-query-last <name>");
        Console.WriteLine("                             Query the cache using a specific index bucket.");
        Console.WriteLine("  cache-snapshot <index>     Dump all entries for a given index.");
        Console.WriteLine("  cache-list                 List cached people ordered by last name.");
        Console.WriteLine("  cache-clear                Wipe the local cache directory.");
        Console.WriteLine("  exit                       Quit the console.");
    }

    private static void PrintStatus(TcpClientSession session)
    {
        if (session.IsConnected)
        {
            Console.WriteLine($"Connected to {session.Endpoint}");
        }
        else
        {
            Console.WriteLine("Not connected.");
        }
    }

    private static async Task TryConnectAsync(TcpClientSession session, string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            await session.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Connected to {host}:{port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to connect to {host}:{port} - {ex.Message}");
        }
    }

    private static async Task HandlePingAsync(TcpClientSession session, CancellationToken cancellationToken)
    {
        EnsureConnected(session);
        var response = await session.SendAsync("PING", ReadOnlyMemory<byte>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        DisplayResponse(response, session.Serializer);
    }

    private static async Task HandlePushAsync(TcpClientSession session, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        EnsureConnected(session);

        if (parts.Count < 5)
        {
            Console.WriteLine("Usage: push <id> <first> <last> <age>");
            return;
        }

        var id = parts[1];
        var first = parts[2];
        var last = parts[3];
        var age = ParseInt(parts[4]);

        var person = new Person(id, first, last, age);
        var payload = session.Serializer.Pack(new List<Person> { person });

        var response = await session.SendAsync("PERSON_STORE", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        DisplayResponse(response, session.Serializer);
    }

    private static async Task HandleSeedAsync(TcpClientSession session, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        EnsureConnected(session);
        var count = parts.Count > 1 ? Math.Max(1, ParseInt(parts[1])) : 5;
        var people = GenerateSamplePeople(count);
        var payload = session.Serializer.Pack(people);
        var response = await session.SendAsync("PERSON_STORE", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        DisplayResponse(response, session.Serializer);
    }

    private static async Task HandleFetchAsync(
        TcpClientSession session,
        LocalRepository repository,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        EnsureConnected(session);
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: fetch <count>");
            return;
        }

        var count = Math.Max(1, ParseInt(parts[1]));
        var query = new PersonQuery { Count = count };
        var payload = session.Serializer.Pack(query);
        var response = await session.SendAsync("PERSON_GET", payload, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.Envelope.Status != 0)
        {
            DisplayResponse(response, session.Serializer);
            return;
        }

        if (response.Payload.Length == 0)
        {
            Console.WriteLine("Server returned no data.");
            return;
        }

        List<Person> people;
        try
        {
            people = session.Serializer.Unpack<List<Person>>(response.Payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to decode payload: {ex.Message}");
            return;
        }

        foreach (var person in people)
        {
            await repository.SaveAsync(person, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Fetched {people.Count} person(s) and cached them locally.");
    }

    private static void PrintCacheIndexes(LocalRepository repository)
    {
        var indexes = repository.Indexes;
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

    private static async Task HandleCacheLoadAsync(LocalRepository repository, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: cache-load <id>");
            return;
        }

        try
        {
            var result = await repository.LoadAsync(parts[1], cancellationToken).ConfigureAwait(false);
            var person = result.Value;
            Console.WriteLine($"{person.Id} => {person.FirstName} {person.LastName}, age {person.Age}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load failed: {ex.Message}");
        }
    }

    private static async Task HandleCacheExistsAsync(LocalRepository repository, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: cache-exists <id>");
            return;
        }

        var exists = await repository.ExistsAsync(parts[1], cancellationToken).ConfigureAwait(false);
        Console.WriteLine(exists ? "Cached." : "Not cached.");
    }

    private static async Task HandleCacheQueryAsync(LocalRepository repository, string indexName, object key, CancellationToken cancellationToken)
    {
        var results = await repository.QueryAsync(indexName, key, cancellationToken).ConfigureAwait(false);
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

    private static void HandleCacheSnapshot(LocalRepository repository, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            Console.WriteLine("Usage: cache-snapshot <index>");
            return;
        }

        var indexName = parts[1];

        if (string.Equals(indexName, "ByAge", StringComparison.OrdinalIgnoreCase))
        {
            if (repository.TryGetSortedListIndex<int>("ByAge", out var index))
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
                Console.WriteLine("ByAge index has not been configured.");
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

    private static void PrintStringIndex(LocalRepository repository, string indexName)
    {
        if (repository.TryGetSortedListIndex<string>(indexName, out var index))
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
            Console.WriteLine($"Index '{indexName}' is not available.");
        }
    }

    private static async Task<LocalRepository> ResetLocalCacheAsync(LocalRepository repository, string? rootOverride)
    {
        var path = repository.RootPath;
        await repository.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to clear cache directory '{path}': {ex.Message}");
        }

        var fresh = LocalRepository.Create(rootOverride);
        Console.WriteLine($"Cache reset. Root: {fresh.RootPath}");
        return fresh;
    }

    private static void DisplayResponse((MsgEnvelope Envelope, byte[] Payload) response, ISerializer serializer)
    {
        if (response.Envelope.Status == 0)
        {
            Console.WriteLine("Status: 0 (OK)");
            if (response.Payload.Length > 0)
            {
                Console.WriteLine($"Payload: {response.Payload.Length} bytes");
            }

            return;
        }

        Console.WriteLine($"Status: {response.Envelope.Status}");
        if (response.Payload.Length == 0)
        {
            return;
        }

        try
        {
            var error = serializer.Unpack<Dictionary<string, object?>>(response.Payload);
            foreach (var (key, value) in error)
            {
                Console.WriteLine($"  {key}: {value}");
            }
        }
        catch
        {
            Console.WriteLine($"  Payload ({response.Payload.Length} bytes)");
        }
    }

    private static void EnsureConnected(TcpClientSession session)
    {
        if (!session.IsConnected)
        {
            throw new InvalidOperationException("Not connected to a server. Use 'connect' first.");
        }
    }

    private static List<Person> GenerateSamplePeople(int count)
    {
        var random = Random.Shared;
        string[] firstNames = { "Alice", "Bob", "Cleo", "Diego", "Eve", "Faris", "Grace", "Hana", "Isaac", "Jade" };
        string[] lastNames = { "Anderson", "Brown", "Chen", "Diaz", "Evans", "Fisher", "Ghosh", "Hughes", "Ivanov", "Jackson" };

        var list = new List<Person>(count);
        for (var i = 0; i < count; i++)
        {
            var id = $"person-{Guid.NewGuid():N}".Substring(0, 12);
            var first = firstNames[random.Next(firstNames.Length)];
            var last = lastNames[random.Next(lastNames.Length)];
            var age = random.Next(18, 75);
            list.Add(new Person(id, first, last, age));
        }

        return list;
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

    private static string Require(IReadOnlyList<string> parts, int index)
    {
        if (parts.Count <= index)
        {
            throw new ArgumentException("Missing argument.");
        }

        return parts[index];
    }

    private static int ParseInt(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"'{value}' is not a valid integer.");
        }

        return result;
    }

    private sealed record TcpConsoleOptions(string? Host, int? Port, string? StorageRoot, bool AutoConnect)
    {
        public static TcpConsoleOptions Parse(string[] args)
        {
            string? host = null;
            int? port = null;
            string? storage = null;
            var autoConnect = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--host":
                        host = ExpectValue(args, ref i, nameof(host));
                        break;

                    case "--port":
                        var value = ExpectValue(args, ref i, nameof(port));
                        port = ParseInt(value);
                        break;

                    case "--storage":
                        storage = ExpectValue(args, ref i, nameof(storage));
                        break;

                    case "--connect":
                        autoConnect = true;
                        break;

                    default:
                        throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            return new TcpConsoleOptions(host, port, storage, autoConnect);
        }

        private static string ExpectValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for option associated with {name}.");
            }

            return args[++index];
        }
    }
}
