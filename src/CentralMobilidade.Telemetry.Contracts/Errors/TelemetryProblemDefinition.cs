namespace CentralMobilidade.Telemetry.Contracts.Errors;

/// <summary>
/// Immutable metadata for an HTTP Problem Details response. API adapters supply trace correlation;
/// they must not replace the fixed detail with raw payloads, credentials, or exception messages.
/// Line rejection codes describe asynchronous outcomes and do not carry HTTP status mappings.
/// </summary>
public sealed record TelemetryProblemDefinition(string Code, string Type, string Title, int Status, string Detail);
