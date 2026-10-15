using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Batches;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Batches;

public sealed partial class BatchInspectionTests
{
    [Fact]
    public async Task Processing_status_preserves_unknown_total_and_absent_completion()
    {
        await using var test = Create();
        var batch = await test.Seed(finalize: false);
        var result = await test.Services.GetRequiredService<GetBatchStatusHandler>()
            .HandleAsync(batch.Id, Reader("producer"), default);
        result.ShouldNotBeNull().State.ShouldBe(BatchState.Processing);
        result.TotalLines.ShouldBeNull();
        result.CompletedAt.ShouldBeNull();
        result.ProcessingStartedAt.ShouldNotBeNull();
        result.AcceptedLines.ShouldBe(2);
        result.RejectedLines.ShouldBe(5);
        result.ReplayedLines.ShouldBe(1);
    }

    [Theory]
    [InlineData("producer", true)]
    [InlineData("other-producer", false)]
    [InlineData("operator", true)]
    [InlineData("scoped", false)]
    [InlineData("cross", true)]
    [InlineData("noperm", false)]
    [InlineData("viewer", false)]
    [InlineData("disabled", false)]
    public async Task Status_projects_only_the_public_contract_for_authorized_readers(string name, bool visible)
    {
        await using var test = Create();
        var batch = await test.Seed();
        var handler = test.Services.GetRequiredService<GetBatchStatusHandler>();
        var result = await handler.HandleAsync(batch.Id, Reader(name), default);
        (result is not null).ShouldBe(visible);
        if (result is null)
            return;
        result.BatchId.ShouldBe(batch.Id.Value);
        result.State.ShouldBe(BatchState.CompletedWithRejections);
        result.AcceptedLines.ShouldBe(2);
        result.RejectedLines.ShouldBe(5);
        result.ReplayedLines.ShouldBe(1);
        result.TotalLines.ShouldBe(8);
        result.ProcessingStartedAt.ShouldNotBeNull();
        result.CompletedAt.ShouldNotBeNull();
        result.ErrorSummary!.ShouldHaveSingleItem().Count.ShouldBe(5);
        result.Links.ShouldBeNull(); // Administrative links are wired by the HTTP endpoint.
        var names = JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        names.ShouldNotContain("producerId");
        names.ShouldNotContain("companies");
        names.ShouldNotContain("version");
    }

    [Theory]
    [InlineData(BatchLifecycle.Accepted, BatchState.Accepted)]
    [InlineData(BatchLifecycle.Processing, BatchState.Processing)]
    [InlineData(BatchLifecycle.Retrying, BatchState.Retrying)]
    [InlineData(BatchLifecycle.Completed, BatchState.Completed)]
    [InlineData(BatchLifecycle.CompletedWithRejections, BatchState.CompletedWithRejections)]
    [InlineData(BatchLifecycle.Failed, BatchState.Failed)]
    public async Task Status_maps_each_public_lifecycle_state_explicitly(BatchLifecycle lifecycle, BatchState expected)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var db = database.CreateContext();
        await db.Batches.Where(x => x.BatchId == batch.Id.Value).ExecuteUpdateAsync(set => set.SetProperty(x => x.State, lifecycle).SetProperty(x => x.RowVersion, x => x.RowVersion + 1));
        var result = await test.Services.GetRequiredService<GetBatchStatusHandler>().HandleAsync(batch.Id, Reader("producer"), default);
        result.ShouldNotBeNull().State.ShouldBe(expected);
    }

    [Theory]
    [InlineData(BatchLifecycle.Receiving)]
    [InlineData(BatchLifecycle.Publishing)]
    [InlineData(BatchLifecycle.Rejected)]
    [InlineData(BatchLifecycle.PublishFailed)]
    [InlineData(BatchLifecycle.Expired)]
    public async Task Status_does_not_expose_unacknowledged_or_expired_batches(BatchLifecycle state)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var db = database.CreateContext();
        await db.Batches.Where(x => x.BatchId == batch.Id.Value).ExecuteUpdateAsync(set => set.SetProperty(x => x.State, state).SetProperty(x => x.RowVersion, x => x.RowVersion + 1));
        (await test.Services.GetRequiredService<GetBatchStatusHandler>().HandleAsync(batch.Id, Reader("producer"), default)).ShouldBeNull();
    }

    [Fact]
    public async Task Status_missing_expired_unauthorized_and_incomplete_scope_are_non_disclosing()
    {
        await using var test = Create();
        var batch = await test.Seed(unknownCompany: true);
        var handler = test.Services.GetRequiredService<GetBatchStatusHandler>();
        (await handler.HandleAsync(new(Guid.NewGuid()), Reader("operator"), default)).ShouldBeNull();
        (await handler.HandleAsync(batch.Id, Reader("operator"), default)).ShouldBeNull();
        (await handler.HandleAsync(batch.Id, Reader("cross"), default)).ShouldNotBeNull();
        (await handler.HandleAsync(batch.Id, Reader("producer"), default)).ShouldNotBeNull();
        test.Clock.UtcNow = test.Clock.UtcNow.AddDays(30);
        (await handler.HandleAsync(batch.Id, Reader("producer"), default)).ShouldBeNull();
    }
}
