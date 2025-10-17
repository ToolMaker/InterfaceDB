using Idb.Libraries.Abstractions.Services.Repository.FileRepository;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Implementation.Builders;
using Idb.Libraries.Implementation.Dtos;
using Idb.Libraries.Implementation.Services.Repository;
using Idb.Libraries.Implementation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using Idb.Libraries.Implementation.Services.Migrations;
using Idb.Libraries.Implementation.Services.Serializers;

namespace Idb.Libraries.Implementation.Extentions;

public static class ScopeStorageServiceCollectionExtensions
{
    private const string DefaultRootFolderName = "Storage";

    public static IServiceCollection AddLocalScopeStorage(
        this IServiceCollection services,
        Action<ScopeStorageBuilder>? configure = null)
    {
        services.TryAddSingleton<IStoragePolicyProvider, DefaultStoragePolicyProvider>();
        services.TryAddSingleton<StorageContractManifestBuilder>();
        services.TryAddSingleton<StorageContractManifestStore>();
        services.TryAddSingleton<IStorageContractManifestService, StorageContractManifestService>();

        var builder = new ScopeStorageBuilder();
        configure?.Invoke(builder);

        var assemblies = (builder.AssembliesToScan?.Any() == true
                          ? builder.AssembliesToScan
                          : AppDomain.CurrentDomain.GetAssemblies())
                         .Where(a => !a.IsDynamic)
                         .Distinct()
                         .ToArray();

        var root = builder.RootDirectory ??
                   new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, DefaultRootFolderName));

        var tree = new ScopeTreeBuilder(assemblies, root).Build();

        services.AddSingleton<ILocalScopeService>(_ => new ScopeService(tree));

        RegisterMigrations(services, assemblies);

        return services;
    }

    public static IServiceCollection AddDefaultObjectWriterAndReader(this IServiceCollection services)
    {
        services.TryAddSingleton<IObjectChangeDetector, MessagePackObjectChangeDetector>();
        services.AddScoped<IObjectSerializer, MessagePackObjectSerializer>();
        services.AddScoped<IObjectWriter, ObjectWriter>();
        services.AddScoped<IObjectReader, ObjectReader>();
        services.AddScoped<IRepository, FileSystemRepository>();

        return services;
    }

    public static IServiceCollection AddFileSystemRepository(this IServiceCollection services)
    {
        services.AddScoped<IRepository, FileSystemRepository>();

        return services;
    }

    private static void RegisterMigrations(IServiceCollection services, Assembly[] assemblies)
    {
        var migrationTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(t => typeof(IStorageMigration).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Distinct()
            .ToArray();

        foreach (var migrationType in migrationTypes)
        {
            services.AddTransient(typeof(IStorageMigration), migrationType);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}


