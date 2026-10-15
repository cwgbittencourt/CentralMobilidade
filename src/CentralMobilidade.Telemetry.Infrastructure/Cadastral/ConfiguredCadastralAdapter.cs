using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace CentralMobilidade.Telemetry.Infrastructure.Cadastral;

// Optional identity projection only. This adapter never decides whether a company exists.
public sealed class ConfiguredCadastralAdapter : IDriverResolver, IDisposable
{
    private const int MaxResponseBytes = 16 * 1024;
    private const int MaxCacheEntries = 4096;
    private readonly HttpClient client;
    private readonly IConfiguration configuration;
    private readonly IClock clock;
    private readonly IDisposable reload;
    private readonly object gate = new();
    private readonly Dictionary<(string Company, string Driver), CacheEntry> cache = new();
    private Settings? active;
    private long generation;
    private bool disposed;

    public ConfiguredCadastralAdapter(HttpClient client, IConfiguration configuration, IClock clock)
    {
        this.client = client;
        this.configuration = configuration;
        this.clock = clock;
        reload = ChangeToken.OnChange(configuration.GetReloadToken, Invalidate);
    }

    public async ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId companyId, DriverId driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(companyId);
        ArgumentNullException.ThrowIfNull(driverId);
        var settings = ReadSettings();
        var key = (companyId.Value, driverId.Value);
        long version;
        lock (gate)
        {
            if (disposed)
                return null;
            if (active != settings)
            {
                cache.Clear();
                active = settings;
                generation++;
            }
            if (settings is null)
                return null;
            version = generation;
            var now = clock.UtcNow;
            if (cache.TryGetValue(key, out var cached) && now >= cached.CachedAt && now < cached.ExpiresAt)
                return cached.Identity;
            cache.Remove(key);
        }
        // Dot segments cannot be represented safely as a path component by System.Uri.
        if (driverId.Value is "." or "..")
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Timeout);
        try
        {
            var uri = new Uri(settings.BaseUrl, "drivers/" + Uri.EscapeDataString(driverId.Value) + "?empresaId=" + Uri.EscapeDataString(companyId.Value));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[2048];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxResponseBytes + 1 - (int)bytes.Length)), deadline.Token);
                if (read == 0)
                    break;
                bytes.Write(buffer, 0, read);
                if (bytes.Length > MaxResponseBytes)
                    return null;
            }
            using var json = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()
                || !root.TryGetProperty("driverId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != driverId.Value
                || !root.TryGetProperty("displayName", out var name) || name.ValueKind != JsonValueKind.String)
                return null;
            var displayName = name.GetString()!;
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256 || displayName.Any(char.IsControl))
                return null;
            var suffix = root.TryGetProperty("cpf", out var cpf) && cpf.ValueKind == JsonValueKind.String ? Suffix(cpf.GetString()!) : null;
            var identity = new DriverIdentityPresentation(driverId, displayName, suffix);
            cancellationToken.ThrowIfCancellationRequested();
            // Do not disclose or cache a response started against a revoked/replaced source.
            var current = ReadSettings();
            lock (gate)
            {
                if (disposed || generation != version || current != settings)
                    return null;
                var now = clock.UtcNow;
                foreach (var expired in cache.Where(p => now < p.Value.CachedAt || now >= p.Value.ExpiresAt).Select(p => p.Key).ToArray())
                    cache.Remove(expired);
                if (cache.Count >= MaxCacheEntries)
                    cache.Remove(cache.MinBy(p => p.Value.CachedAt).Key);
                cache[key] = new(identity, now, now.Add(settings.CacheTtl));
            }
            return identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            // No response body, source exception, name, CPF or URL enters logs/diagnostics.
            return null;
        }
    }

    private Settings? ReadSettings()
    {
        try
        {
            var section = configuration.GetSection("DriverEnrichment");
            if (!section.GetValue("Enabled", false))
                return null;
            if (!Uri.TryCreate(section["BaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return null;
            var timeout = section.GetValue("Timeout", TimeSpan.FromSeconds(2));
            var ttl = section.GetValue("CacheTtl", TimeSpan.FromMinutes(1));
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30) || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(5))
                return null;
            return new(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), timeout, ttl);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    private static string? Suffix(string cpf)
    {
        if (cpf.Length == 14 && cpf[3] == '.' && cpf[7] == '.' && cpf[11] == '-')
            cpf = cpf[..3] + cpf.Substring(4, 3) + cpf.Substring(8, 3) + cpf[12..];
        return cpf.Length == 11 && cpf.All(c => c is >= '0' and <= '9') ? cpf[^4..] : null;
    }

    private void Invalidate()
    {
        lock (gate)
        {
            cache.Clear();
            active = null;
            generation++;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            cache.Clear();
            generation++;
        }
        reload.Dispose();
        client.Dispose();
    }
    private sealed record Settings(Uri BaseUrl, TimeSpan Timeout, TimeSpan CacheTtl);
    private sealed record CacheEntry(DriverIdentityPresentation Identity, DateTimeOffset CachedAt, DateTimeOffset ExpiresAt);
}
