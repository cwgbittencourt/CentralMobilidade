using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Presentation;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Presentation;

public sealed class PresentationQuerySafetyTests
{
    [Fact]
    public async Task Identifiers_are_parameters_and_sensitive_fields_are_never_selected()
    {
        var transport = new Transport("[]");
        var reader = Reader(transport);
        await reader.LatestAsync(new("company' OR true --"), new("vehicle' OR true --"), "cursor' OR true --", 501, default);
        using var json = JsonDocument.Parse(transport.LastBody!);
        var sql = json.RootElement.GetProperty("q").GetString()!;
        sql.ShouldNotContain("OR true");
        sql.ShouldNotContain("placa");
        sql.ShouldNotContain("cpf");
        sql.ShouldContain("LIMIT 501");
        json.RootElement.GetProperty("params").GetProperty("company").GetString().ShouldBe("company' OR true --");
        json.RootElement.GetProperty("params").GetProperty("vehicle").GetString().ShouldBe("vehicle' OR true --");
    }

    [Theory]
    [InlineData("private failure", 500)]
    [InlineData("invalid-json-private-diagnostics", 200)]
    [InlineData("[{\"empresa_id\":\"other\"}]", 200)]
    public async Task Failed_or_malformed_provider_response_is_safe_and_never_partial(string body, int status)
    {
        var error = await Should.ThrowAsync<ProviderOperationException>(async () => await Reader(new Transport(body, status)).LatestAsync(new("company"), null, null, 2, default));
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.InternalError);
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain("private");
    }

    [Fact]
    public async Task Oversized_response_is_rejected_before_json_parsing()
    {
        var transport = new Transport("[]") { Oversize = true };
        await Should.ThrowAsync<ProviderOperationException>(async () => await Reader(transport).LatestAsync(new("company"), null, null, 2, default));
    }

    private static InfluxPresentationReader Reader(Transport transport) => new(new HttpClient(transport), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["InfluxDb:Queryable:Url"] = "http://query.invalid",
        ["InfluxDb:Queryable:Database"] = "queryable",
        ["InfluxDb:Queryable:Token"] = "test-only-token"
    }).Build());

    private sealed class Transport(string body, int status = 200) : HttpMessageHandler
    {
        public string? LastBody;
        public bool Oversize;
        private int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastBody = await request.Content!.ReadAsStringAsync(token);
            if (++calls == 1)
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"column_name\":\"time\"},{\"column_name\":\"empresa_id\"},{\"column_name\":\"veiculo_id\"},{\"column_name\":\"latitude\"},{\"column_name\":\"longitude\"}]")
                };
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            if (Oversize)
                response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return response;
        }
    }
}
