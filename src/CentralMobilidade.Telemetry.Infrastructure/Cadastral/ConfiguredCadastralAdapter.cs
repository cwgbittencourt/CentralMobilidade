using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using Microsoft.Extensions.Configuration;

namespace CentralMobilidade.Telemetry.Infrastructure.Cadastral;

/// <summary>Configured HTTP visibility lookup; only an explicit, tenant-matching positive response permits presentation.</summary>
public sealed class ConfiguredCadastralAdapter : ICompanyVisibilityResolver
{
    private readonly HttpClient client;
    private readonly Uri? endpoint;
    private readonly string? token;
    private readonly TimeSpan timeout;
    public ConfiguredCadastralAdapter(HttpClient client, IConfiguration configuration)
    {
        this.client = client;
        var url = configuration["Cadastral:CompanyVisibilityUrl"];
        if (!string.IsNullOrEmpty(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https") ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Invalid cadastral endpoint configuration.");
        }
        token = configuration["Cadastral:Token"];
        if (token?.Contains('\r') == true || token?.Contains('\n') == true)
            throw new ArgumentException("Invalid cadastral credential configuration.");
        timeout = configuration.GetValue("Cadastral:LookupTimeout", TimeSpan.FromSeconds(2));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid cadastral timeout.");
    }

    public async ValueTask<CompanyVisibility> ResolveAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(companyId);
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint is null)
            return CompanyVisibility.Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.AbsoluteUri + "?companyId=" + Uri.EscapeDataString(companyId.Value));
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return CompanyVisibility.Unknown;
            if (response.StatusCode != HttpStatusCode.OK)
                return CompanyVisibility.Unavailable;
            using var document = JsonDocument.Parse(await BoundedProviderJson.ReadAsync(response.Content, 16384, deadline.Token), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !BoundedProviderJson.UniqueProperties(root) ||
                !root.TryGetProperty("companyId", out var company) || company.ValueKind != JsonValueKind.String || company.GetString() != companyId.Value ||
                !root.TryGetProperty("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return CompanyVisibility.Inconclusive;
            return visible.GetBoolean() ? CompanyVisibility.Visible : CompanyVisibility.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return CompanyVisibility.Inconclusive; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return CompanyVisibility.Unavailable; }
    }
}
