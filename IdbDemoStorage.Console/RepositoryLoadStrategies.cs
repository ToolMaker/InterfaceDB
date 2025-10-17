using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Dtos;
using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.Logging;

namespace IdbDemoStorage.ConsoleHost;

internal interface IRepositoryLoadStrategyHandler
{
    RepositoryLoadStrategyType StrategyType { get; }

    Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractConfigDto options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken);
}

internal static class RepositoryLoadStrategyExecutor
{
    public static async Task ExecuteAsync(
        IEnumerable<IRepositoryLoadStrategyHandler> handlers,
        IDbContractConfigDto options,
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
        IDbContractConfigDto options,
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
        IDbContractConfigDto options,
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

    public Task ExecuteAsync(
        IRepositoryNode repository,
        IDbContractConfigDto options,
        Program.ContractVersionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Repository load strategy {Strategy} for contract {Contract} is not yet implemented. Defaulting to lazy behaviour.",
            StrategyType,
            options.ContractName ?? options.Key);

        return Task.CompletedTask;
    }
}
