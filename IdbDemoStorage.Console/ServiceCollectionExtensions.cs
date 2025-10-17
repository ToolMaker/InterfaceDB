using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Idb.Libraries.Abstraction.Dtos;
using Idb.Libraries.Abstraction.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace IdbDemoStorage.ConsoleHost;

internal static class ServiceCollectionExtensions
{
    public static IReadOnlyList<IDbContractConfigDto> DiscoverContractOptions(params Assembly[] assemblies)
    {
        var searchAssemblies = (assemblies is null || assemblies.Length == 0)
            ? AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray()
            : assemblies;

        return searchAssemblies
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(IDbContractConfigDto).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && !t.IsInterface)
            .Select(InstantiateOption)
            .Where(o => o is not null)
            .Cast<IDbContractConfigDto>()
            .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public static IServiceCollection AddContractOptions(this IServiceCollection services, IEnumerable<IDbContractConfigDto> options)
    {
        var optionArray = options?.ToArray() ?? Array.Empty<IDbContractConfigDto>();
        foreach (var option in optionArray)
        {
            ValidateContractOption(option);
        }

        var lookup = optionArray.ToDictionary(o => o.Key, o => o, StringComparer.OrdinalIgnoreCase);

        foreach (var option in optionArray)
        {
            services.AddSingleton(typeof(IDbContractConfigDto), option);
            services.AddSingleton(option.GetType(), option);
        }

        services.AddSingleton<IReadOnlyList<IDbContractConfigDto>>(optionArray);
        services.AddSingleton<IDictionary<string, IDbContractConfigDto>>(lookup);

        return services;
    }

    public static IServiceCollection AddContractOptions(this IServiceCollection services, params Assembly[] assemblies)
        => services.AddContractOptions(DiscoverContractOptions(assemblies));

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static IDbContractConfigDto? InstantiateOption(Type type)
        => Activator.CreateInstance(type) as IDbContractConfigDto;

    private static void ValidateContractOption(IDbContractConfigDto option)
    {
        if (option is null)
        {
            return;
        }

        var contractLabel = string.IsNullOrWhiteSpace(option.ContractName)
            ? option.Key
            : option.ContractName;

        var indexCount = option.Indexes?.Length ?? 0;
        switch (option.IndexProvisioningMode)
        {
            case ContractIndexProvisioningModeType.Mixed:
            case ContractIndexProvisioningModeType.Fixed:
                if (indexCount == 0)
                {
                    throw new InvalidOperationException(
                        $"Contract '{contractLabel}' must define at least one index when IndexProvisioningMode is set to {option.IndexProvisioningMode}.");
                }
                break;
            case ContractIndexProvisioningModeType.Dynamic:
            case ContractIndexProvisioningModeType.None:
            default:
                break;
        }

        if (option.ObjectWriter == ContractObjectWriterType.None)
        {
            throw new InvalidOperationException(
                $"Contract '{contractLabel}' must specify an object writer implementation.");
        }

        if (option.ObjectWriter != ContractObjectWriterType.Binary)
        {
            throw new NotSupportedException(
                $"Contract '{contractLabel}' requests unsupported object writer '{option.ObjectWriter}'. Only the built-in binary writer is currently available.");
        }

        if (option.ObjectReader == ContractObjectReaderType.None)
        {
            throw new InvalidOperationException(
                $"Contract '{contractLabel}' must specify an object reader implementation.");
        }

        if (option.ObjectReader != ContractObjectReaderType.Binary)
        {
            throw new NotSupportedException(
                $"Contract '{contractLabel}' requests unsupported object reader '{option.ObjectReader}'. Only the built-in binary reader is currently available.");
        }

        if (option.RepositoryType != ContractRepositoryType.FileSystem)
        {
            throw new NotSupportedException(
                $"Contract '{contractLabel}' requests repository type '{option.RepositoryType}', which is not supported by this demo host.");
        }
    }
}
