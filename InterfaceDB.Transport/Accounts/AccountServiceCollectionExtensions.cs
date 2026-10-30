using Idb.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idb.Transport;

public static class AccountServiceCollectionExtensions
{
    /// <summary>Register the credential store separately; the provider does not own it. Custom hashers/policies can be registered first.</summary>
    public static IServiceCollection AddLocalAccountAuthentication(this IServiceCollection services, LocalAccountOptions options)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options); options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<IAccountCreationPolicy, ConfiguredAccountCreationPolicy>();
        services.AddSingleton<LocalAccountAuthenticationProvider>();
        services.AddSingleton<IAuthenticationProvider>(p => p.GetRequiredService<LocalAccountAuthenticationProvider>());
        services.AddSingleton<IDatabaseAccountManager>(p => p.GetRequiredService<LocalAccountAuthenticationProvider>());
        return services;
    }
}
