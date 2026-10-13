using System;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Security;

public abstract class BearerHandlerBase(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IAuthenticationCredentialStore store, IClock clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected IAuthenticationCredentialStore Store { get; } = store;
    protected IClock CredentialClock { get; } = clock;
    protected abstract Task<ClaimsIdentity?> ResolveIdentityAsync(string token);
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }
        if (header.Count != 1 || !AuthenticationHeaderValue.TryParse(header[0], out var value)
            || !value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || value.Parameter is null || value.Parameter.Length > 48)
        {
            return AuthenticateResult.Fail("Authentication failed.");
        }
        try
        {
            var identity = await ResolveIdentityAsync(value.Parameter);
            return identity is null ? AuthenticateResult.Fail("Authentication failed.")
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return AuthenticateResult.Fail("Authentication failed."); }
    }
    protected bool IsActive(bool enabled, DateTimeOffset? expiresAt) => enabled && (expiresAt is null || expiresAt > CredentialClock.UtcNow);
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return SecurityProblemResponses.WriteAsync(Context, TelemetryErrorCodes.AuthenticationRequired);
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        SecurityProblemResponses.WriteAsync(Context, TelemetryErrorCodes.AccessDenied);
}

internal static class SecurityProblemResponses
{
    internal static Task WriteAsync(HttpContext context, string code)
    {
        var problem = TelemetryErrorCodes.GetProblem(code);
        context.Response.StatusCode = problem.Status;
        return context.Response.WriteAsJsonAsync(new
        {
            code = problem.Code,
            type = problem.Type,
            title = problem.Title,
            status = problem.Status,
            detail = problem.Detail,
            traceId = context.TraceIdentifier
        },
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
