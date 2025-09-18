using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TransactionManager.Abstraction.Repository;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Implementation.Builders;
using TransactionManager.Implementation.Dtos;
using TransactionManager.Implementation.Repository;
using TransactionManager.Implementation.Serializers;
using TransactionManager.Implementation.Services;

namespace TransactionManager.Implementation.Extentions
{
    public static class ScopeStorageServiceCollectionExtensions
    {
        private const string DefaultRootFolderName = "Storage";

        public static IServiceCollection AddLocalScopeStorage(
            this IServiceCollection services,
            Action<ScopeStorageBuilder>? configure = null)
        {
            services.TryAddSingleton<IStoragePolicyProvider, DefaultStoragePolicyProvider>();

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

            return services;
        }

        public static IServiceCollection AddDefaultObjectWriterAndReader(this IServiceCollection services)
        {
            services.AddScoped<IObjectWriter, ObjectWriter>();
            services.AddScoped<IObjectReader, ObjectReader>();
            services.AddScoped<IRepository, FileSystemRepository>();

            return services;
        }

        public static IServiceCollection AddJsonSerializer(this IServiceCollection services)
        {
            services.AddScoped<IObjectSerializer, JsonObjectSerializer>();

            return services;
        }
        public static IServiceCollection AddFileSystemRepository(this IServiceCollection services)
        {
            services.AddScoped<IRepository, FileSystemRepository>();

            return services;
        }
    }
}
