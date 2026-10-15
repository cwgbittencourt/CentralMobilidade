using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Batches;

// Construct only from an authenticated server-side principal.
public sealed record BatchReadIdentity
{
    private BatchReadIdentity(string? producer, OperatorActor? actor)
    {
        ProducerId = producer;
        Operator = actor;
    }
    public string? ProducerId
    {
        get;
    }
    public OperatorActor? Operator
    {
        get;
    }
    public static BatchReadIdentity ForProducer(string producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        return new(producer, null);
    }
    public static BatchReadIdentity ForOperator(OperatorActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(null, actor);
    }
}

public sealed class BatchVisibility(IBatchStateStore batches, IOperatorAuthorizer authorizer, IClock clock)
{
    public async ValueTask<BatchSnapshot?> GetVisibleAsync(BatchId id, BatchReadIdentity identity, CancellationToken token)
    {
        var batch = await batches.GetAsync(id, token);
        if (batch is null || IsExpired(batch))
            return null;
        if (identity.ProducerId is { } producer)
            return StringComparer.Ordinal.Equals(producer, batch.ProducerId) ? batch : null;
        return identity.Operator is { } actor && await CanReadAsync(batch, actor, OperatorPermission.BatchRead, token) ? batch : null;
    }
    public bool IsExpired(BatchSnapshot batch) => batch.State == BatchLifecycle.Expired || batch.ExpiresAt <= clock.UtcNow;
    public async ValueTask<bool> HasPermissionAsync(OperatorActor actor, OperatorPermission permission, CancellationToken token)
    {
        if (actor.Kind != OperatorActorKind.Human)
            return false;
        try
        {
            return await authorizer.AuthorizeAsync(actor, permission, [], token) == OperatorAuthorizationDecision.Allowed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public async ValueTask<bool> CanReadAsync(BatchSnapshot batch, OperatorActor actor, OperatorPermission permission, CancellationToken token)
    {
        if (!await HasPermissionAsync(actor, permission, token))
            return false;
        if (!batch.CompanyScopeComplete && !await HasPermissionAsync(actor, OperatorPermission.CrossCompanyRead, token))
            return false;
        try
        {
            return await authorizer.AuthorizeAsync(actor, permission, batch.Companies, token) == OperatorAuthorizationDecision.Allowed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}

public sealed class ListBatchRejectionsHandler(IBatchStateStore batches, BatchVisibility visibility)
{
    public async ValueTask<RejectionResultPage?> HandleAsync(BatchId id, BatchReadIdentity identity, string? cursor, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (identity.Operator is null)
            throw new ProviderOperationException(TelemetryErrorCodes.AuthenticationRequired, ProviderFailureKind.Permanent);
        if (await visibility.GetVisibleAsync(id, identity, token) is null)
            return null;
        RejectionResultPage page;
        try
        {
            page = await batches.GetRejectionsAsync(id, cursor, limit, token);
        }
        catch (ProviderOperationException error) when (error.ErrorCode == TelemetryErrorCodes.ResourceNotFound) { return null; }
        return await visibility.GetVisibleAsync(id, identity, token) is null ? null : page;
    }
}

