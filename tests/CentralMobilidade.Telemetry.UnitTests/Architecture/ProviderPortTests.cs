using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Abstractions;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Architecture;

public sealed class ProviderPortTests
{
    [Fact]
    public void ProviderFailuresPreserveRetryClassificationWithoutSdkDiagnostics()
    {
        var failure = new ProviderOperationException("DURABILITY_UNCONFIRMED", ProviderFailureKind.Ambiguous);
        failure.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        failure.ErrorCode.ShouldBe("DURABILITY_UNCONFIRMED");
        failure.InnerException.ShouldBeNull();
        Should.Throw<ArgumentException>(() => new ProviderOperationException("sensitive SDK error", ProviderFailureKind.Transient))
            .Message.ShouldNotContain("sensitive SDK error");
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderOperationException("STORAGE_REJECTED", (ProviderFailureKind)99));
    }

    [Fact]
    public void ApplicationPortsDoNotReferenceProviderSdksOrApiInfrastructure()
    {
        var references = typeof(IDurableBatchQueue).Assembly.GetReferencedAssemblies();
        foreach (var name in references.Select(reference => reference.Name!))
        {
            name.ShouldNotContain("Infrastructure");
            name.ShouldNotContain("Telemetry.Api");
            name.ShouldNotContain("RabbitMQ");
            name.ShouldNotContain("Npgsql");
            name.ShouldNotContain("EntityFrameworkCore");
            name.ShouldNotContain("Google.Cloud");
        }
    }

    [Fact]
    public void EveryExternalOperationRequiresCancellation()
    {
        Type[] ports =
        [
            typeof(IDurableBatchQueue), typeof(IQueueDelivery), typeof(IBatchStateStore),
            typeof(IQuarantineStore), typeof(IEventIdentityGuard), typeof(ITelemetryWriter),
            typeof(ICompanyVisibilityResolver), typeof(IDriverResolver),
            typeof(IOperatorAuthorizer), typeof(IAuditWriter)
        ];
        foreach (var port in ports)
        {
            var methods = port.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).ToArray();
            methods.Length.ShouldBeGreaterThan(0);
            foreach (var method in methods)
            {
                method.GetParameters().Last().ParameterType.ShouldBe(typeof(CancellationToken));
                method.GetParameters().Last().IsOptional.ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void QueueAndQuarantineUseSeparateOwnedStreams()
    {
        typeof(IQueueDelivery).GetProperty(nameof(IQueueDelivery.Body))!.PropertyType.ShouldBe(typeof(Stream));
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(IQueueDelivery)).ShouldBeTrue();
        typeof(IQuarantineContent).GetProperty(nameof(IQuarantineContent.Content))!.PropertyType.ShouldBe(typeof(Stream));
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(IQuarantineContent)).ShouldBeTrue();
        var publish = typeof(IDurableBatchQueue).GetMethod(nameof(IDurableBatchQueue.PublishAsync))!;
        publish.GetParameters().Any(parameter => parameter.ParameterType == typeof(Stream)).ShouldBeTrue();
        publish.GetParameters().Any(parameter => parameter.ParameterType == typeof(byte[]) || parameter.ParameterType == typeof(string)).ShouldBeFalse();
    }
}
