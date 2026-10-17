using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.LineProtocol;

public sealed class LineProtocolV1GoldenTests
{
    [Theory]
    [InlineData("latitude=0,longitude=0,velocidade=0", "latitude=-0.0,longitude=-0e0,velocidade=-0.0")]
    [InlineData("latitude=1,longitude=2,velocidade=3,placa=\"iabc123\"", "latitude=1.0,longitude=2e0,velocidade=3.00,placa=\"IABC123\"")]
    public void Canonicalization_is_invariant_for_signed_zero_and_normalized_plate(string first, string second)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Fingerprint(Tags + " " + first + ",rpm=4i,ignicao=true " + Timestamp)
                .ShouldBe(Fingerprint(Tags + " " + second + ",rpm=4i,ignicao=true " + Timestamp));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("empresa-42", "Empresa-42")]
    [InlineData("empresa-42", "empresa-42\\ ")]
    [InlineData("empresa-42", "e\u0301")]
    public void Canonicalization_never_normalizes_company_identity(string first, string second)
        => Fingerprint(Baseline.Replace("empresa-42", first)).ShouldNotBe(Fingerprint(Baseline.Replace("empresa-42", second)));

    private const string Tags = "telemetria,empresa_id=empresa-42,veiculo_id=veiculo-900";
    private const string Fields = "latitude=-23.55052,longitude=-46.633308,velocidade=82.5,rpm=2150i,ignicao=true";
    private const string Timestamp = "1791686400123";
    private const string Baseline = Tags + " " + Fields + " " + Timestamp;

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Golden_event_preserves_values_and_millisecond_units(string ending)
    {
        var result = Parse(Encoding.UTF8.GetBytes(Baseline + ending));
        Property(result, "ErrorCode").ShouldBeNull();
        var point = Property(result, "Event").ShouldNotBeNull();
        Value(point, "CompanyId").ShouldBe("empresa-42");
        Value(point, "VehicleId").ShouldBe("veiculo-900");
        Property(point, "DriverId").ShouldBeNull();
        Property(point, "Plate").ShouldBeNull();
        Convert.ToDouble(Property(point, "Latitude")).ShouldBe(-23.55052);
        Convert.ToDouble(Property(point, "Longitude")).ShouldBe(-46.633308);
        Convert.ToDouble(Property(point, "Speed")).ShouldBe(82.5); // km/h, never converted to m/s.
        Convert.ToInt64(Property(point, "Rpm")).ShouldBe(2150);
        Property(point, "Ignition").ShouldBe(true);
        Convert.ToInt64(Property(Property(point, "Timestamp")!, "UnixMilliseconds")).ShouldBe(1791686400123L);
    }

    [Fact]
    public void Escaped_tags_and_quoted_plate_decode_without_splitting_columns()
    {
        var point = Accepted("telemetria,empresa_id=São\\ Paulo\\,SP,veiculo_id=carro\\=42,motorista_id=José "
            + Fields + ",placa=\"bra\\\"2\\\\e19\" " + Timestamp);
        Value(point, "CompanyId").ShouldBe("São Paulo,SP");
        Value(point, "VehicleId").ShouldBe("carro=42");
        Value(point, "DriverId").ShouldBe("José");
        Property(point, "Plate").ShouldBe("BRA\"2\\E19");
    }

    [Theory]
    [InlineData(-90, -180, 0, 0, false)]
    [InlineData(90, 180, 1000, 9223372036854775807L, true)]
    public void Inclusive_domain_boundaries_are_accepted(double latitude, double longitude, double speed, long rpm, bool ignition)
    {
        var fields = FormattableString.Invariant($"latitude={latitude:R},longitude={longitude:R},velocidade={speed:R},rpm={rpm}i,ignicao={ignition.ToString().ToLowerInvariant()}");
        Accepted(Tags + " " + fields + " " + Timestamp).ShouldNotBeNull();
    }

    public static IEnumerable<object[]> InvalidLines()
    {
        yield return ["", TelemetryErrorCodes.LpEmptyLine];
        yield return [Baseline.Replace("telemetria,", "outra,"), TelemetryErrorCodes.LpUnknownMeasurement];
        foreach (var tag in new[] { "empresa_id=empresa-42", "veiculo_id=veiculo-900" })
            yield return [Baseline.Replace("," + tag, ""), TelemetryErrorCodes.LpMissingRequiredTag];
        foreach (var field in Fields.Split(','))
            yield return [Tags + " " + string.Join(',', Array.FindAll(Fields.Split(','), value => value != field)) + " " + Timestamp,
                TelemetryErrorCodes.LpMissingRequiredField];
        foreach (var column in new[] { "cpf=123", "nome=José", "email=teste", "event_fingerprint=abc" })
            yield return [Tags + "," + column + " " + Fields + " " + Timestamp, TelemetryErrorCodes.LpUnknownColumn];
        yield return [Tags + " " + Fields + ",extra=1 " + Timestamp, TelemetryErrorCodes.LpUnknownColumn];
        foreach (var (original, replacement) in new[]
        {
            ("latitude=-23.55052", "latitude=1i"), ("longitude=-46.633308", "longitude=\"2\""),
            ("velocidade=82.5", "velocidade=true"), ("rpm=2150i", "rpm=2150"),
            ("rpm=2150i", "rpm=2150u"), ("ignicao=true", "ignicao=1i"),
            ("ignicao=true", "ignicao=\"true\""), ("latitude=-23.55052", "latitude=NaN"),
            ("longitude=-46.633308", "longitude=Infinity")
        })
            yield return [Baseline.Replace(original, replacement), TelemetryErrorCodes.LpInvalidFieldType];
        yield return [Tags + " " + Fields + ",placa=ABC1234 " + Timestamp, TelemetryErrorCodes.LpInvalidFieldType];
        yield return [Baseline.Replace("latitude=-23.55052", "latitude=90.0001"), TelemetryErrorCodes.LpCoordinateOutOfRange];
        yield return [Baseline.Replace("longitude=-46.633308", "longitude=-180.0001"), TelemetryErrorCodes.LpCoordinateOutOfRange];
        yield return [Baseline.Replace(Timestamp, "not-a-time"), TelemetryErrorCodes.LpInvalidTimestamp];
        yield return [Baseline.Replace(Timestamp, "1791686400123.5"), TelemetryErrorCodes.LpInvalidTimestamp];
        yield return [Baseline.Replace(Timestamp, "1791686400123000000"), TelemetryErrorCodes.LpTimestampOutOfRange];
    }

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public void Invalid_lines_have_stable_rejection_codes(string line, string code)
    {
        var result = Parse(Encoding.UTF8.GetBytes(line));
        Property(result, "Event").ShouldBeNull();
        Property(result, "ErrorCode").ShouldBe(code);
    }

    [Theory]
    [InlineData("velocidade=82.5", "velocidade=-0.1")]
    [InlineData("rpm=2150i", "rpm=-1i")]
    public void Negative_speed_and_rpm_are_rejected(string original, string replacement)
    {
        var result = Parse(Encoding.UTF8.GetBytes(Baseline.Replace(original, replacement)));
        Property(result, "Event").ShouldBeNull();
        TelemetryErrorCodes.IsRejectionCode(Property(result, "ErrorCode") as string).ShouldBeTrue();
    }

    [Fact]
    public void Malformed_utf8_is_rejected_instead_of_replaced()
    {
        var bytes = Encoding.UTF8.GetBytes(Baseline);
        bytes[Array.IndexOf(bytes, (byte)'4')] = 0xff;
        var result = Parse(bytes);
        Property(result, "Event").ShouldBeNull();
        Property(result, "ErrorCode").ShouldBe(TelemetryErrorCodes.LpInvalidSyntax);
    }

    [Theory]
    [InlineData("telemetria,veiculo_id=veiculo-900,empresa_id=empresa-42 ignicao=true,rpm=2150i,velocidade=82.5,longitude=-46.633308,latitude=-23.55052 1791686400123")]
    [InlineData("telemetria,empresa_id=empresa-42,veiculo_id=veiculo-900 latitude=-23.5505200,longitude=-46.6333080,velocidade=8.25e1,rpm=2150i,ignicao=true 1791686400123\r\n")]
    public void Canonical_fingerprint_ignores_order_numeric_spelling_and_line_ending(string equivalent)
    {
        Fingerprint(Baseline).ShouldBe(Fingerprint(equivalent));
    }

    [Theory]
    [InlineData("velocidade=82.5", "velocidade=82.6")]
    [InlineData("empresa-42", "empresa-43")]
    [InlineData("veiculo-900", "veiculo-901")]
    [InlineData("1791686400123", "1791686400124")]
    [InlineData("ignicao=true", "ignicao=false")]
    public void Canonical_fingerprint_changes_with_event_content(string original, string replacement)
    {
        Fingerprint(Baseline).ShouldNotBe(Fingerprint(Baseline.Replace(original, replacement)));
    }

    private static object Accepted(string line)
    {
        var result = Parse(Encoding.UTF8.GetBytes(line));
        Property(result, "ErrorCode").ShouldBeNull();
        return Property(result, "Event").ShouldNotBeNull();
    }

    [Theory]
    [InlineData(",empresa_id=second", true)]
    [InlineData(",latitude=1", false)]
    public void Duplicate_columns_are_rejected(string extra, bool tag)
    {
        var input = tag ? Tags + extra + " " + Fields + " " + Timestamp : Tags + " " + Fields + extra + " " + Timestamp;
        var result = new TelemetryLineProtocolV1Parser().Parse(Encoding.UTF8.GetBytes(input));
        result.Event.ShouldBeNull();
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.LpInvalidSyntax);
    }

    [Fact]
    public void Optional_driver_and_plate_participate_in_canonical_content()
    {
        var parser = new TelemetryLineProtocolV1Parser();
        var plain = parser.Parse(Encoding.UTF8.GetBytes(Baseline)).Event.ShouldNotBeNull();
        var driver = parser.Parse(Encoding.UTF8.GetBytes(Tags + ",motorista_id=driver " + Fields + " " + Timestamp)).Event.ShouldNotBeNull();
        var plate = parser.Parse(Encoding.UTF8.GetBytes(Tags + " " + Fields + ",placa=\"abc1234\" " + Timestamp)).Event.ShouldNotBeNull();
        driver.Fingerprint.ShouldNotBe(plain.Fingerprint);
        plate.Fingerprint.ShouldNotBe(plain.Fingerprint);
        parser.Parse(Encoding.UTF8.GetBytes(Tags + " " + Fields + ",placa=\"ABC1234\" " + Timestamp)).Event!.Fingerprint.ShouldBe(plate.Fingerprint);
        parser.Parse(Encoding.UTF8.GetBytes(Tags + " " + Fields + ",placa=\"\" " + Timestamp)).Event!.Fingerprint.ShouldNotBe(plain.Fingerprint);
    }

    [Fact]
    public void Signed_zero_whitespace_and_culture_do_not_change_fingerprint()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var parser = new TelemetryLineProtocolV1Parser();
            var zero = Baseline.Replace("velocidade=82.5", "velocidade=0");
            var point = parser.Parse(Encoding.UTF8.GetBytes(zero)).Event.ShouldNotBeNull();
            parser.Parse(Encoding.UTF8.GetBytes("  " + zero.Replace("velocidade=0", "velocidade=-0.0").Replace(" latitude=", "   latitude=") + "  ")).Event!.Fingerprint.ShouldBe(point.Fingerprint);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            parser.Parse(Encoding.UTF8.GetBytes(zero)).Event!.Fingerprint.ShouldBe(point.Fingerprint);
            point.ToString().ShouldBe("[ParsedTelemetryEvent]");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("latitude=1i", TelemetryErrorCodes.LpInvalidFieldType)]
    [InlineData("ignicao=True", TelemetryErrorCodes.LpInvalidFieldType)]
    [InlineData("placa=\"unterminated", TelemetryErrorCodes.LpInvalidSyntax)]
    public void Malformed_fields_return_private_stable_errors(string field, string code)
    {
        var fields = field.StartsWith("latitude", StringComparison.Ordinal) ? Fields.Replace("latitude=-23.55052", field)
            : field.StartsWith("ignicao", StringComparison.Ordinal) ? Fields.Replace("ignicao=true", field) : Fields + "," + field;
        var result = new TelemetryLineProtocolV1Parser().Parse(Encoding.UTF8.GetBytes(Tags + " " + fields + " " + Timestamp));
        result.Event.ShouldBeNull();
        result.ErrorCode.ShouldBe(code);
        result.ToString().ShouldBe("[LineProtocolParseResult]");
    }

    [Fact]
    public void Parser_applies_byte_limit_before_decoding_or_allocating_line_text()
    {
        var result = new TelemetryLineProtocolV1Parser().Parse(new byte[16 * 1024 + 1]);
        result.Event.ShouldBeNull();
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.LpLineTooLarge);
    }

    private static string Fingerprint(string line)
    {
        var point = Accepted(line);
        var digest = Property(point, "Fingerprint").ShouldBeOfType<string>();
        digest.Length.ShouldBe(64);
        Convert.FromHexString(digest).Length.ShouldBe(32);
        return digest;
    }

    [Theory]
    [InlineData("company\"quoted", "company\"quoted")]
    [InlineData("company\\literal", "company\\literal")]
    public void Literal_tag_quotes_and_non_special_backslashes_are_preserved(string encoded, string decoded)
    {
        var input = Baseline.Replace("empresa-42", encoded, StringComparison.Ordinal);
        var result = new TelemetryLineProtocolV1Parser().Parse(Encoding.UTF8.GetBytes(input));
        result.ErrorCode.ShouldBeNull();
        result.Event!.CompanyId.Value.ShouldBe(decoded);
    }

    // Reflection keeps the test-first commit compilable while T043/T046 do not yet exist.
    // This invokes the production parser; it contains no substitute parser or expected-failure bypass.
    private static object Parse(byte[] bytes)
    {
        var type = typeof(AcceptTelemetryBatchHandler).Assembly.GetType(
            "CentralMobilidade.Telemetry.Application.LineProtocol.TelemetryLineProtocolV1Parser");
        type.ShouldNotBeNull("T046 must implement the production Line Protocol v1 parser");
        var method = type.GetMethod("Parse", [typeof(ReadOnlyMemory<byte>)]);
        method.ShouldNotBeNull("Parse(ReadOnlyMemory<byte>) must parse one bounded UTF-8 line");
        var parser = method.IsStatic ? null : Activator.CreateInstance(type);
        return method.Invoke(parser, [new ReadOnlyMemory<byte>(bytes)]).ShouldNotBeNull();
    }

    private static object? Property(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("The parser result/event must expose " + name);
        return property.GetValue(instance);
    }

    private static string Value(object point, string name) =>
        Property(Property(point, name).ShouldNotBeNull(), "Value").ShouldBeOfType<string>();
}
