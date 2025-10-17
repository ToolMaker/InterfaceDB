using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Implementation.Dtos;
using Idb.Libraries.Implementation.Services;

namespace Idb.Libraries.Implementation.Builders;

internal sealed class ScopeTreeBuilder
{
    private static readonly Type FileNameFactoryInterface = typeof(IFileNameFactory);
    private readonly Assembly[] _assemblies;
    private readonly DirectoryInfo _rootDirectory;

    public ScopeTreeBuilder(IEnumerable<Assembly> assemblies, DirectoryInfo rootDirectory)
    {
        if (assemblies is null)
        {
            throw new ArgumentNullException(nameof(assemblies));
        }

        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _assemblies = assemblies
            .Where(static assembly => assembly is not null)
            .Distinct()
            .ToArray();
    }

    public IReadOnlyDictionary<Type, IScopeDescriptor> Build()
    {
        if (_assemblies.Length == 0)
        {
            return new Dictionary<Type, IScopeDescriptor>(0);
        }

        var allTypes = _assemblies
            .SelectMany(SafeGetTypes)
            .Where(static type => type is not null)
            .Cast<Type>()
            .ToArray();

        var contracts = allTypes
            .Where(IsStorageContract)
            .ToArray();

        var descriptors = new Dictionary<Type, IScopeDescriptor>(contracts.Length);

        foreach (var contract in contracts)
        {
            var descriptor = CreateDescriptor(contract, allTypes);
            descriptors[contract] = descriptor;
        }

        return descriptors;
    }

    private IScopeDescriptor CreateDescriptor(Type contractType, IReadOnlyCollection<Type> allTypes)
    {
        var attribute = contractType.GetCustomAttribute<StorageScopeAttribute>(inherit: false)
                        ?? throw new InvalidOperationException($"Contract type '{contractType.FullName}' must be decorated with [StorageScope].");

        var versionTag = string.IsNullOrWhiteSpace(attribute.VersionTag)
            ? "1.0"
            : attribute.VersionTag.Trim();

        var fileNameFactoryType = attribute.FileNameFactory ?? typeof(DefaultFileNameFactory);
        if (!FileNameFactoryInterface.IsAssignableFrom(fileNameFactoryType))
        {
            throw new InvalidOperationException(
                $"File name factory type '{fileNameFactoryType.FullName}' must implement {nameof(IFileNameFactory)}.");
        }

        var implementations = allTypes
            .Where(type =>
                type is { IsClass: true, IsAbstract: false } &&
                contractType.IsAssignableFrom(type))
            .Distinct()
            .ToArray();

        var contractDirectory = new DirectoryInfo(Path.Combine(_rootDirectory.FullName, contractType.Name));

        return new ScopeDescriptor(
            contractType,
            contractDirectory,
            implementations,
            versionTag,
            fileNameFactoryType);
    }

    private static bool IsStorageContract(Type type)
    {
        if (type is null)
        {
            return false;
        }

        if (!type.IsInterface)
        {
            return false;
        }

        return type.GetCustomAttribute<StorageScopeAttribute>(inherit: false) is not null;
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly assembly)
    {
        if (assembly is null)
        {
            yield break;
        }

        Type[]? types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch
        {
            yield break;
        }

        foreach (var type in types)
        {
            yield return type;
        }
    }
}
