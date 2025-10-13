using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Idb.Libraries.Implementation.Extentions;

public static class ContractRegistrationExtensions
{
    public static IServiceCollection RegisterContractTransient<TOptions>(
        this IServiceCollection services,
        TOptions options,
        ILogger? logger = null)
        where TOptions : IDbContractOptions
        => RegisterContractInternal(services, options, ServiceLifetime.Transient, logger);

    public static IServiceCollection RegisterContractScoped<TOptions>(
        this IServiceCollection services,
        TOptions options,
        ILogger? logger = null)
        where TOptions : IDbContractOptions
        => RegisterContractInternal(services, options, ServiceLifetime.Scoped, logger);

    public static IServiceCollection RegisterContractSingleton<TOptions>(
        this IServiceCollection services,
        TOptions options,
        ILogger? logger = null)
        where TOptions : IDbContractOptions
        => RegisterContractInternal(services, options, ServiceLifetime.Singleton, logger);

    private static IServiceCollection RegisterContractInternal<TOptions>(
        IServiceCollection services,
        TOptions options,
        ServiceLifetime lifetime,
        ILogger? logger)
        where TOptions : IDbContractOptions
    {
        logger ??= NullLogger.Instance;

        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Key))
        {
            throw new ArgumentException("Contract option key cannot be null or whitespace.", nameof(options));
        }

        var contractName = string.IsNullOrWhiteSpace(options.ContractName)
            ? options.Key
            : options.ContractName;

        if (string.IsNullOrWhiteSpace(contractName))
        {
            throw new ArgumentException("Contract name cannot be null or whitespace.", nameof(options));
        }

        var interfaceName = $"I{contractName}";

        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Distinct()
            .ToArray();

        var allTypes = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t is not null)
            .Cast<Type>()
            .ToArray();

        var contractInterfaces = allTypes
            .Where(t => t.IsInterface
                        && string.Equals(t.Name, interfaceName, StringComparison.Ordinal)
                        && t.GetCustomAttribute<StorageScopeAttribute>(inherit: false) is not null)
            .ToArray();

        if (contractInterfaces.Length == 0)
        {
            logger.LogWarning("No contract interface named {InterfaceName} with [StorageScope] was discovered for key {ContractKey}.", interfaceName, options.Key);
            return services;
        }

        var candidates = allTypes
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToArray();

        foreach (var contractInterface in contractInterfaces)
        {
            var implementations = candidates
                .Where(contractInterface.IsAssignableFrom)
                .ToArray();

            if (implementations.Length == 0)
            {
                logger.LogWarning("Contract interface {ContractInterface} registered for key {ContractKey} but no implementations were discovered.", contractInterface.FullName, options.Key);
            }
            else
            {
                logger.LogInformation(
                    "Registering contract {ContractInterface} for key {ContractKey} with {ImplementationCount} implementation(s): {Implementations}",
                    contractInterface.FullName,
                    options.Key,
                    implementations.Length,
                    string.Join(", ", implementations.Select(t => t.FullName ?? t.Name)));
            }

            foreach (var implementation in implementations)
            {
                var descriptor = ServiceDescriptor.Describe(contractInterface, implementation, lifetime);
                services.TryAddEnumerable(descriptor);
            }
        }

        return services;
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types;
        }
    }
}
