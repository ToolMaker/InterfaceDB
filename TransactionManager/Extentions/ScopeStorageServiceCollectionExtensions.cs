using Microsoft.Extensions.DependencyInjection;
using TransactionManager.Abstraction.Services;
using TransactionManager.Builders;
using TransactionManager.Dtos;
using TransactionManager.Services;

namespace TransactionManager.Extentions
{
    public static class ScopeStorageServiceCollectionExtensions
    {
        public static IServiceCollection AddScopeStorage(
            this IServiceCollection services,
            Action<ScopeStorageBuilder>? configure = null)
        {
            var builder = new ScopeStorageBuilder();
            configure?.Invoke(builder);

            var assemblies = (builder.AssembliesToScan?.Any() == true
                              ? builder.AssembliesToScan
                              : AppDomain.CurrentDomain.GetAssemblies())
                             .Where(a => !a.IsDynamic)
                             .Distinct()
                             .ToArray();

            var tree = new ScopeTreeBuilder(assemblies).Build();
            services.AddSingleton<IScopeService>(new ScopeService(tree));

            return services;
        }
    }


}
