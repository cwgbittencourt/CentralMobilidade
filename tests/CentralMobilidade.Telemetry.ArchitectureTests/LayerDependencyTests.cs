using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ArchitectureTests;

public sealed class LayerDependencyTests
{
    private const string Prefix = "CentralMobilidade.Telemetry.";

    public static TheoryData<string> Layers => new()
    {
        "Domain", "Contracts", "Application", "Infrastructure", "Api", "Worker"
    };

    [Theory]
    [MemberData(nameof(Layers))]
    public void Project_references_must_point_to_allowed_layers(string layer)
    {
        var project = XDocument.Load(ProjectPath(layer));
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!));

        foreach (var reference in references)
        {
            DependencyRules.IsAllowedProject(layer, reference).ShouldBeTrue($"{layer} references {reference}");
        }
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void Compiled_assemblies_must_point_to_allowed_layers(string layer)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, Prefix + layer + ".dll"));
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                DependencyRules.IsAllowedProject(layer, name).ShouldBeTrue($"{layer} assembly references {name}");
            }

            if (layer is "Domain" or "Application" or "Contracts")
            {
                DependencyRules.IsInfrastructure(name).ShouldBeFalse($"{layer} assembly references provider {name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void Evaluated_project_references_must_point_to_allowed_layers(string layer)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ProjectPath(layer))!, "obj", "project.assets.json")));
        var project = assets.RootElement.GetProperty("project");
        foreach (var framework in project.GetProperty("restore").GetProperty("frameworks").EnumerateObject())
        {
            foreach (var reference in framework.Value.GetProperty("projectReferences").EnumerateObject())
            {
                var name = Path.GetFileNameWithoutExtension(reference.Name);
                DependencyRules.IsAllowedProject(layer, name).ShouldBeTrue($"{layer} restores project {name}");
            }
        }

    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Contracts")]
    public void Inner_layers_must_not_resolve_infrastructure_packages(string layer)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ProjectPath(layer))!, "obj", "project.assets.json")));
        var project = assets.RootElement.GetProperty("project");
        foreach (var framework in project.GetProperty("frameworks").EnumerateObject())
        {
            foreach (var reference in framework.Value.GetProperty("frameworkReferences").EnumerateObject())
            {
                reference.Name.ShouldBe("Microsoft.NETCore.App", $"{layer} must not depend on a host framework");
            }
        }

        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() == "package")
            {
                var name = library.Name.Split('/')[0];
                DependencyRules.IsInfrastructure(name).ShouldBeFalse($"{layer} resolves provider {name}");
            }
        }
    }

    [Theory]
    [InlineData("Application", "Domain")]
    [InlineData("Application", "Contracts")]
    [InlineData("Infrastructure", "Application")]
    [InlineData("Api", "Infrastructure")]
    [InlineData("Worker", "Domain")]
    public void Guard_allows_inward_dependencies(string layer, string dependency)
    {
        DependencyRules.IsAllowedProject(layer, Prefix + dependency).ShouldBeTrue();
    }

    [Theory]
    [InlineData("FluentValidation")]
    [InlineData("Microsoft.Extensions.Options.ConfigurationExtensions")]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions")]
    [InlineData("System.Text.Json")]
    public void Guard_allows_application_libraries(string dependency)
    {
        DependencyRules.IsInfrastructure(dependency).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Domain", "Application")]
    [InlineData("Domain", "Infrastructure")]
    [InlineData("Contracts", "Domain")]
    [InlineData("Application", "Infrastructure")]
    [InlineData("Infrastructure", "Api")]
    [InlineData("Api", "Worker")]
    [InlineData("Worker", "Api")]
    [InlineData("Application", "UnitTests")]
    public void Guard_rejects_inverted_or_unknown_project_dependencies(string layer, string dependency)
    {
        DependencyRules.IsAllowedProject(layer, Prefix + dependency).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("RabbitMQ.Client")]
    [InlineData("Google.Cloud.PubSub.V1")]
    [InlineData("Microsoft.AspNetCore.Http.Abstractions")]
    [InlineData("Microsoft.Extensions.Hosting")]
    [InlineData("OpenTelemetry.Exporter.OpenTelemetryProtocol")]
    [InlineData("Serilog.Sinks.Console")]
    public void Guard_rejects_provider_dependencies(string dependency)
    {
        DependencyRules.IsInfrastructure(dependency).ShouldBeTrue();
    }

    private static string ProjectPath(string layer)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Architecture tests require the solution checkout and restored project assets.");
        }

        return Path.Combine(directory.FullName, "src", Prefix + layer, Prefix + layer + ".csproj");
    }
}

internal static class DependencyRules
{
    private const string Prefix = "CentralMobilidade.Telemetry.";
    private static readonly IReadOnlyDictionary<string, string[]> AllowedLayers = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Domain"] = [],
        ["Contracts"] = [],
        ["Application"] = ["Domain", "Contracts"],
        ["Infrastructure"] = ["Domain", "Contracts", "Application"],
        ["Api"] = ["Domain", "Contracts", "Application", "Infrastructure"],
        ["Worker"] = ["Domain", "Contracts", "Application", "Infrastructure"]
    };

    // Compare package/assembly families at a segment boundary, including transitive providers.
    private static readonly string[] InfrastructureFamilies =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "RabbitMQ", "Google.Cloud", "Google.Apis",
        "Microsoft.AspNetCore", "Microsoft.Extensions.Hosting", "OpenTelemetry", "Serilog",
        "InfluxDB", "InfluxDB3", "Azure", "AWSSDK", "Grpc"
    ];

    internal static bool IsAllowedProject(string layer, string dependency)
    {
        return dependency.StartsWith(Prefix, StringComparison.Ordinal)
            && AllowedLayers.TryGetValue(layer, out var allowed)
            && allowed.Contains(dependency[Prefix.Length..], StringComparer.Ordinal);
    }

    internal static bool IsInfrastructure(string dependency)
    {
        return InfrastructureFamilies.Any(family => dependency.Equals(family, StringComparison.OrdinalIgnoreCase)
            || dependency.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase));
    }
}
