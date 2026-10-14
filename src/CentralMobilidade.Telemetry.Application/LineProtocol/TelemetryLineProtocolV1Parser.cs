using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.Telemetry;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Application.LineProtocol;

public sealed record LineProtocolParseResult(ParsedTelemetryEvent? Event, string? ErrorCode)
{
    public override string ToString() => "[LineProtocolParseResult]";
}

public sealed record ParsedTelemetryEvent
{
    internal ParsedTelemetryEvent(TelemetryEvent value)
    {
        Value = value;
        Identity = new(value.CompanyId, value.VehicleId, value.Timestamp);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("centralmobilidade:telemetry:event:v1");
            writer.Write(value.CompanyId.Value);
            writer.Write(value.VehicleId.Value);
            writer.Write(value.Timestamp.UnixMilliseconds);
            writer.Write(value.DriverId is not null);
            if (value.DriverId is not null)
                writer.Write(value.DriverId.Value);
            writer.Write(value.Latitude == 0 ? 0d : value.Latitude);
            writer.Write(value.Longitude == 0 ? 0d : value.Longitude);
            writer.Write(value.Speed == 0 ? 0d : value.Speed);
            writer.Write(value.Rpm);
            writer.Write(value.Ignition);
            writer.Write(value.Plate is not null);
            if (value.Plate is not null)
                writer.Write(value.Plate);
        }
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }
    public TelemetryEvent Value
    {
        get;
    }
    public EventIdentity Identity
    {
        get;
    }
    public CompanyId CompanyId => Value.CompanyId;
    public VehicleId VehicleId => Value.VehicleId;
    public EventTimestamp Timestamp => Value.Timestamp;
    public DriverId? DriverId => Value.DriverId;
    public double Latitude => Value.Latitude;
    public double Longitude => Value.Longitude;
    public double Speed => Value.Speed;
    public long Rpm => Value.Rpm;
    public bool Ignition => Value.Ignition;
    public string? Plate => Value.Plate;
    public string Fingerprint
    {
        get;
    }
    public override string ToString() => "[ParsedTelemetryEvent]";
}

/// <summary>One bounded UTF-8 line at a time; schema v1 is closed and diagnostics contain no raw data.</summary>
public sealed class TelemetryLineProtocolV1Parser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly int maxLineBytes;
    public TelemetryLineProtocolV1Parser() => maxLineBytes = 16 * 1024;
    public TelemetryLineProtocolV1Parser(IOptions<TelemetryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        maxLineBytes = options.Value.MaxLineBytes;
        if (maxLineBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public LineProtocolParseResult Parse(ReadOnlyMemory<byte> input)
    {
        var bytes = input.Span;
        if (!bytes.IsEmpty && bytes[^1] == '\n')
        {
            bytes = bytes[..^1];
            if (!bytes.IsEmpty && bytes[^1] == '\r')
                bytes = bytes[..^1];
        }
        if (bytes.Length > maxLineBytes)
            return Reject(TelemetryErrorCodes.LpLineTooLarge);
        string line;
        try
        {
            line = Utf8.GetString(bytes).Trim(' ');
        }
        catch (DecoderFallbackException) { return Reject(TelemetryErrorCodes.LpInvalidSyntax); }
        if (line.Length == 0)
            return Reject(TelemetryErrorCodes.LpEmptyLine);
        foreach (var character in line)
            if (char.IsControl(character))
                return Reject(TelemetryErrorCodes.LpInvalidSyntax);
        try
        {
            var first = Boundary(line, 0, false);
            if (first < 0)
                throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
            var fieldStart = first;
            while (fieldStart < line.Length && line[fieldStart] == ' ')
                fieldStart++;
            var second = Boundary(line, fieldStart, true);
            if (second < 0)
                throw Invalid(TelemetryErrorCodes.LpInvalidTimestamp);
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var tagParts = Split(line[..first], false).GetEnumerator();
            using (tagParts)
            {
                if (!tagParts.MoveNext() || Decode(tagParts.Current, false) != "telemetria")
                    throw Invalid(TelemetryErrorCodes.LpUnknownMeasurement);
                while (tagParts.MoveNext())
                {
                    var pair = Pair(tagParts.Current);
                    var key = Decode(pair.Key, false);
                    if (key is not ("empresa_id" or "veiculo_id" or "motorista_id"))
                        throw Invalid(TelemetryErrorCodes.LpUnknownColumn);
                    if (!tags.TryAdd(key, Decode(pair.Value, false)))
                        throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
                }
            }
            if (!tags.ContainsKey("empresa_id") || !tags.ContainsKey("veiculo_id"))
                throw Invalid(TelemetryErrorCodes.LpMissingRequiredTag);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in Split(line[fieldStart..second], true))
            {
                var pair = Pair(part);
                if (pair.Key is not ("latitude" or "longitude" or "velocidade" or "rpm" or "ignicao" or "placa"))
                    throw Invalid(TelemetryErrorCodes.LpUnknownColumn);
                if (!fields.TryAdd(pair.Key, pair.Value))
                    throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
            }
            foreach (var key in new[] { "latitude", "longitude", "velocidade", "rpm", "ignicao" })
                if (!fields.ContainsKey(key))
                    throw Invalid(TelemetryErrorCodes.LpMissingRequiredField);
            var latitude = Float(fields["latitude"]);
            var longitude = Float(fields["longitude"]);
            var speed = Float(fields["velocidade"]);
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
                throw Invalid(TelemetryErrorCodes.LpCoordinateOutOfRange);
            var rpmText = fields["rpm"];
            if (!rpmText.EndsWith('i') || !long.TryParse(rpmText.AsSpan(0, rpmText.Length - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rpm) || rpm < 0 || speed < 0)
                throw Invalid(TelemetryErrorCodes.LpInvalidFieldType);
            if (fields["ignicao"] is not ("true" or "false"))
                throw Invalid(TelemetryErrorCodes.LpInvalidFieldType);
            string? plate = null;
            if (fields.TryGetValue("placa", out var plateText))
            {
                if (plateText.Length < 2 || plateText[0] != '"' || plateText[^1] != '"')
                    throw Invalid(TelemetryErrorCodes.LpInvalidFieldType);
                plate = Decode(plateText[1..^1], true);
            }
            var timeText = line[(second + 1)..].Trim(' ');
            var digits = timeText.AsSpan();
            if (!digits.IsEmpty && digits[0] is '+' or '-')
                digits = digits[1..];
            if (digits.IsEmpty)
                throw Invalid(TelemetryErrorCodes.LpInvalidTimestamp);
            foreach (var character in digits)
                if (!char.IsAsciiDigit(character))
                    throw Invalid(TelemetryErrorCodes.LpInvalidTimestamp);
            if (!long.TryParse(timeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var time) || time < long.MinValue / 1000000 || time > long.MaxValue / 1000000)
                throw Invalid(TelemetryErrorCodes.LpTimestampOutOfRange);
            CompanyId company;
            VehicleId vehicle;
            DriverId? driver;
            try
            {
                company = new(tags["empresa_id"]);
                vehicle = new(tags["veiculo_id"]);
                driver = tags.TryGetValue("motorista_id", out var driverText) ? new(driverText) : null;
            }
            catch (ArgumentException) { throw Invalid(TelemetryErrorCodes.LpInvalidSyntax); }
            var value = new TelemetryEvent(company, vehicle, EventTimestamp.FromUnixTimeMilliseconds(time), latitude, longitude, speed, rpm,
                fields["ignicao"] == "true", driver, plate);
            return new(new ParsedTelemetryEvent(value), null);
        }
        catch (ParseFailure error) { return Reject(error.Code); }
    }

    private static double Float(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
            throw Invalid(TelemetryErrorCodes.LpInvalidFieldType);
        return result;
    }
    private static int Boundary(string value, int start, bool strings)
    {
        var quoted = false;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                if (++i == value.Length)
                    throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
                continue;
            }
            if (strings && value[i] == '"')
                quoted = !quoted;
            if (value[i] == ' ' && !quoted)
                return i;
        }
        if (quoted)
            throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
        return -1;
    }
    private static IEnumerable<string> Split(string value, bool strings)
    {
        var quoted = false;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                if (++i == value.Length)
                    throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
                continue;
            }
            if (strings && value[i] == '"')
                quoted = !quoted;
            if (value[i] == ',' && !quoted)
            {
                yield return value[start..i];
                start = i + 1;
            }
        }
        if (quoted)
            throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
        yield return value[start..];
    }
    private static KeyValuePair<string, string> Pair(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
                continue;
            }
            if (value[i] == '=' && i > 0 && i < value.Length - 1)
                return new(value[..i], value[(i + 1)..]);
        }
        throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
    }
    private static string Decode(string value, bool quoted)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                if (i + 1 == value.Length)
                    throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
                var next = value[i + 1];
                if (quoted ? next is '\\' or '"' : next is '\\' or ' ' or ',' or '=')
                {
                    c = next;
                    i++;
                }
            }
            else if (quoted && c == '"')
                throw Invalid(TelemetryErrorCodes.LpInvalidSyntax);
            result.Append(c);
        }
        return result.ToString();
    }
    private static LineProtocolParseResult Reject(string code) => new(null, code);
    private static ParseFailure Invalid(string code) => new(code);
    private sealed class ParseFailure(string code) : Exception("Line Protocol v1 rejected.")
    {
        internal string Code => code;
    }
}
