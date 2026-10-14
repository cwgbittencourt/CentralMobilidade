using System;
using System.Security.Cryptography;
using System.Text;

namespace CentralMobilidade.Telemetry.Domain.Processing;

public enum ProcessingOutcome { Accepted, IdenticalReplay, Rejected }

// Wire names are stable; a contract test requires exact equality with the public rejection catalog.
public enum LineRejectionCode
{
    LP_EMPTY_LINE, LP_INVALID_SYNTAX, LP_UNKNOWN_MEASUREMENT, LP_UNKNOWN_COLUMN,
    LP_MISSING_REQUIRED_TAG, LP_MISSING_REQUIRED_FIELD, LP_INVALID_FIELD_TYPE,
    LP_INVALID_TIMESTAMP, LP_TIMESTAMP_OUT_OF_RANGE, LP_COORDINATE_OUT_OF_RANGE,
    LP_LINE_TOO_LARGE, PAYLOAD_DECOMPRESSED_LIMIT, PAYLOAD_EXPANSION_LIMIT,
    DUPLICATE_CONFLICT, STORAGE_SCHEMA_CONFLICT, STORAGE_REJECTED
}

/// <summary>Keyed diagnostic digest. Never store the key or the original line in the value.</summary>
public sealed record SafeLineDigest
{
    private const string Prefix = "hmac-sha256:v1:";
    private SafeLineDigest(string value) => Value = value;
    public string Value { get; }
    public static SafeLineDigest Compute(ReadOnlySpan<byte> rawLine, ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
            throw new ArgumentException("Diagnostic digest key must contain at least 32 bytes.", nameof(key));
        return new(Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(key, rawLine)));
    }
    // Trusted persistence rehydration: syntax cannot prove a stored digest's cryptographic provenance.
    public static SafeLineDigest FromStoredValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported keyed diagnostic digest format.", nameof(value));
        return new(Prefix + ProcessingValidation.Sha256(value[Prefix.Length..], nameof(value)));
    }
    public override string ToString() => "[SafeLineDigest]";
}

/// <summary>Internal protected-store reference, never a public API locator or a log value.</summary>
public sealed record ProtectedContentReference
{
    public ProtectedContentReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            throw new ArgumentException("Invalid protected content reference.", nameof(value));
        foreach (var character in value)
            if (char.IsControl(character))
                throw new ArgumentException("Invalid protected content reference.", nameof(value));
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid protected content reference.", nameof(value)); }
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => "[ProtectedContentReference]";
}

internal static class ProcessingValidation
{
    internal static string Sha256(string value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length != 64)
            throw new ArgumentException("A SHA-256 hexadecimal digest is required.", parameter);
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character))
                throw new ArgumentException("A SHA-256 hexadecimal digest is required.", parameter);
        return value.ToLowerInvariant();
    }
}
