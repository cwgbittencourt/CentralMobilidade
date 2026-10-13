using System;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Security;

public static class SecurityRegistration
{
    public static IServiceCollection AddTelemetrySecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection("Authentication"), binder => binder.ErrorOnUnknownConfiguration = true).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>());
        services.TryAddSingleton<IAuthenticationCredentialStore, ConfiguredSecurityStore>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IOperatorAuthorizer, StoredOperatorAuthorizer>();
        services.AddAuthentication(options => options.DefaultChallengeScheme = OperatorAuthorizationPolicies.OperatorScheme)
            .AddScheme<AuthenticationSchemeOptions, ProducerBearerHandler>(OperatorAuthorizationPolicies.ProducerScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, OperatorBearerHandler>(OperatorAuthorizationPolicies.OperatorScheme, _ => { });
        services.AddAuthorization(OperatorAuthorizationPolicies.Configure);
        services.AddScoped<IAuthorizationHandler, OperatorPermissionHandler>();
        services.AddScoped<IAuthorizationHandler, ProducerBatchOwnershipHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditedAuthorizationResultHandler>();
        return services;
    }
    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
