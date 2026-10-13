using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Application.Configuration;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public long MaxCompressedBytes { get; set; } = 8 * 1024 * 1024;
    public long MaxDecompressedBytes { get; set; } = 64 * 1024 * 1024;
    public double MaxExpansionRatio { get; set; } = 100;
    public int MaxLineBytes { get; set; } = 16 * 1024;
    public int MaxLinesPerBatch { get; set; } = 50000;
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan QueueRetention { get; set; } = TimeSpan.FromDays(7);
    public int WriteBatchMaxPoints { get; set; } = 5000;
    public long WriteBatchMaxBytes { get; set; } = 2 * 1024 * 1024;
    public TimeSpan WriteBatchFlushInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BatchStatusRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan QuarantineRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan FingerprintEvidenceRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan LocalTelemetryRetention { get; set; } = TimeSpan.FromDays(60);
    public ViewerOptions Viewer { get; set; } = new();
}

public sealed class ViewerOptions
{
    public int LatestPositionMaxVehicles { get; set; } = 500;
    public TimeSpan TripMaxDuration { get; set; } = TimeSpan.FromHours(24);
    public int TripMaxPoints { get; set; } = 10000;
    public TimeSpan HealthMaxDuration { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan HealthMinGranularity { get; set; } = TimeSpan.FromMinutes(1);
    public int HealthMaxPoints { get; set; } = 10000;
}

public sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        void Require(bool valid, string message)
        {
            if (!valid)
            {
                failures.Add(message);
            }
        }

        Require(options.MaxCompressedBytes > 0, "MaxCompressedBytes must be positive.");
        Require(options.MaxDecompressedBytes > 0, "MaxDecompressedBytes must be positive.");
        Require(options.MaxDecompressedBytes >= options.MaxCompressedBytes,
            "MaxDecompressedBytes must be at least MaxCompressedBytes.");
        Require(double.IsFinite(options.MaxExpansionRatio) && options.MaxExpansionRatio >= 1,
            "MaxExpansionRatio must be finite and at least one.");
        Require(options.MaxLineBytes > 0 && options.MaxLineBytes <= options.MaxDecompressedBytes,
            "MaxLineBytes must be positive and no larger than MaxDecompressedBytes.");
        Require(options.MaxLinesPerBatch > 0, "MaxLinesPerBatch must be positive.");
        Require(options.UploadTimeout > TimeSpan.Zero, "UploadTimeout must be positive.");
        Require(options.QueueRetention >= TimeSpan.FromDays(7),
            "QueueRetention must cover the required seven-day recovery window.");
        Require(options.WriteBatchMaxPoints > 0, "WriteBatchMaxPoints must be positive.");
        Require(options.WriteBatchMaxBytes > 0, "WriteBatchMaxBytes must be positive.");
        Require(options.WriteBatchFlushInterval > TimeSpan.Zero, "WriteBatchFlushInterval must be positive.");
        Require(options.BatchStatusRetention == TimeSpan.FromDays(30),
            "BatchStatusRetention must be 30 days as required by the v1 contract.");
        Require(options.QuarantineRetention > TimeSpan.Zero && options.QuarantineRetention <= TimeSpan.FromDays(30),
            "QuarantineRetention must be positive and at most 30 days.");
        Require(options.FingerprintEvidenceRetention == TimeSpan.FromDays(30),
            "FingerprintEvidenceRetention must be 30 days for exact replay/conflict classification.");
        Require(options.LocalTelemetryRetention == TimeSpan.FromDays(60),
            "LocalTelemetryRetention must match the local databases' fixed 60-day retention.");
        Require(options.LocalTelemetryRetention >= options.FingerprintEvidenceRetention
            && options.LocalTelemetryRetention >= options.QueueRetention,
            "LocalTelemetryRetention must cover FingerprintEvidenceRetention and QueueRetention.");

        if (options.Viewer is null)
        {
            failures.Add("Viewer limits are required.");
        }
        else
        {
            var viewer = options.Viewer;
            Require(viewer.LatestPositionMaxVehicles is > 0 and <= 500,
                "LatestPositionMaxVehicles must be between 1 and 500.");
            Require(viewer.TripMaxDuration > TimeSpan.Zero && viewer.TripMaxDuration <= TimeSpan.FromHours(24),
                "TripMaxDuration must be positive and at most 24 hours.");
            Require(viewer.TripMaxPoints is > 0 and <= 10000, "TripMaxPoints must be between 1 and 10000.");
            Require(viewer.HealthMaxDuration > TimeSpan.Zero && viewer.HealthMaxDuration <= TimeSpan.FromDays(7),
                "HealthMaxDuration must be positive and at most seven days.");
            Require(viewer.HealthMinGranularity >= TimeSpan.FromMinutes(1)
                && viewer.HealthMinGranularity <= viewer.HealthMaxDuration,
                "HealthMinGranularity must be at least one minute and no larger than HealthMaxDuration.");
            Require(viewer.HealthMaxPoints is > 0 and <= 10000, "HealthMaxPoints must be between 1 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class TelemetryOptionsRegistration
{
    public static IServiceCollection AddTelemetryOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>());
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName), binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        return services;
    }
}
