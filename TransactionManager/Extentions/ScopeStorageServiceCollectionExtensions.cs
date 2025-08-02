using Microsoft.Extensions.DependencyInjection;
using TransactionManager.Abstraction.Services;
using TransactionManager.Builders;
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

            var tree = new ScopeTreeBuilder(builder.AssembliesToScan).Build();
            var svc = new ScopeService(tree);

            services.AddSingleton<IScopeService>(svc);
            return services;
        }
    }

}
