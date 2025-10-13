using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.Logging;

namespace IdbDemoStorage.ConsoleHost;

internal interface IRepositoryLoadStrategyHandler
{
    RepositoryLoadStrategyType StrategyType { get; }

    Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractOptions options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken);
}

internal static class RepositoryLoadStrategyExecutor
{
    public static async Task ExecuteAsync(
        IEnumerable<IRepositoryLoadStrategyHandler> handlers,
        IDbContractOptions options,
        Program.ContractVersionContext context,
        IRepositoryNode repository,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var handlerLookup = handlers.ToDictionary(h => h.StrategyType);

        if (!handlerLookup.TryGetValue(options.LoadStrategy, out var handler))
        {
            logger.LogWarning(
                "Repository load strategy {Strategy} for contract {Contract} is not registered. Falling back to None.",
                options.LoadStrategy,
                options.ContractName ?? options.Key);

            handler = handlerLookup.TryGetValue(RepositoryLoadStrategyType.None, out var fallback)
                ? fallback
                : handlerLookup.Values.First();
        }

        await handler.ExecuteAsync(repository, options, context, services, logger, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class NoOpRepositoryLoadStrategyHandler : IRepositoryLoadStrategyHandler
{
    public NoOpRepositoryLoadStrategyHandler(RepositoryLoadStrategyType strategyType)
    {
        StrategyType = strategyType;
    }

    public RepositoryLoadStrategyType StrategyType { get; }

    public Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractOptions options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Repository load strategy {Strategy} selected for contract {Contract}. No eager work will be performed.", StrategyType, options.ContractName ?? options.Key);
        return Task.CompletedTask;
    }
}

internal sealed class LoggingOnlyRepositoryLoadStrategyHandler : IRepositoryLoadStrategyHandler
{
    private readonly string _message;

    public LoggingOnlyRepositoryLoadStrategyHandler(RepositoryLoadStrategyType strategyType, string message)
    {
        StrategyType = strategyType;
        _message = message;
    }

    public RepositoryLoadStrategyType StrategyType { get; }

    public Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractOptions options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(_message, StrategyType, options.ContractName ?? options.Key);
        return Task.CompletedTask;
    }
}

internal sealed class EagerWarmCacheRepositoryLoadStrategyHandler : IRepositoryLoadStrategyHandler
{
    public RepositoryLoadStrategyType StrategyType => RepositoryLoadStrategyType.EagerWarmCache;

    public async Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractOptions options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var scopeService = services.GetService(typeof(ILocalScopeService)) as ILocalScopeService;
        if (scopeService is null)
        {
            logger.LogWarning("Unable to resolve ILocalScopeService. Skipping eager warm cache for contract {Contract}.", options.ContractName ?? options.Key);
            return;
        }

        var scope = scopeService.GetScope(context.ContractType);
        if (scope is null)
        {
            logger.LogWarning("No scope descriptor found for contract {Contract}. Skipping eager warm cache.", context.ContractType.FullName);
            return;
        }

        var contractDirectory = scope.Directory;
        if (!contractDirectory.Exists)
        {
            logger.LogInformation("Contract directory {Directory} does not exist. Nothing to warm for contract {Contract}.", contractDirectory.FullName, options.ContractName ?? options.Key);
            return;
        }

        logger.LogInformation("Starting eager warm cache for contract {Contract} located at {Directory}.", options.ContractName ?? options.Key, contractDirectory.FullName);

        var totalLoaded = 0;
        foreach (var versionDirectory in SafeEnumerateDirectories(contractDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versionTag = versionDirectory.Name;
            foreach (var file in SafeEnumerateFiles(versionDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = Path.GetFileNameWithoutExtension(file.Name);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (await TryLoadAsync(repository, context.ContractType, id, versionTag, cancellationToken).ConfigureAwait(false))
                {
                    totalLoaded++;
                }
            }
        }

        logger.LogInformation("Eager warm cache for contract {Contract} completed. Loaded {Count} record(s).", options.ContractName ?? options.Key, totalLoaded);
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo root)
    {
        try
        {
            return root.EnumerateDirectories();
        }
        catch (DirectoryNotFoundException)
        {
            return Enumerable.Empty<DirectoryInfo>();
        }
        catch (IOException)
        {
            return Enumerable.Empty<DirectoryInfo>();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<DirectoryInfo>();
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return Enumerable.Empty<FileInfo>();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<FileInfo>();
        }
    }

    private static async Task<bool> TryLoadAsync(
        IRepositoryNode repository,
        Type contractType,
        string id,
        string versionTag,
        CancellationToken cancellationToken)
    {
        var method = typeof(IRepository).GetMethod(nameof(IRepository.LoadAsync)) ?? typeof(IRepositoryNode).GetMethod(nameof(IRepositoryNode.LoadAsync));
        if (method is null)
        {
            return false;
        }

        var generic = method.MakeGenericMethod(contractType);
        var valueTaskObj = generic.Invoke(repository, new object?[] { id, versionTag, cancellationToken });
        if (valueTaskObj is null)
        {
            return false;
        }

        var asTaskMethod = valueTaskObj.GetType().GetMethod("AsTask");
        if (asTaskMethod is null)
        {
            return false;
        }

        var task = (Task)asTaskMethod.Invoke(valueTaskObj, null)!;
        await task.ConfigureAwait(false);
        return true;
    }
}
