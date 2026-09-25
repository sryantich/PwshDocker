using System.Management.Automation;
using System.Text.Json;

namespace PwshDocker;

/// <summary>Engine and client version information (docker version).</summary>
public sealed class DockerVersionInfo : DockerObject
{
    private DockerVersionInfo(DockerClient client) : base(client)
    {
    }

    public string? Endpoint { get; private init; }

    /// <summary>Product name, e.g. "Docker Desktop 4.57.0 (215387)" or "Docker Engine - Community".</summary>
    public string? Platform { get; private init; }

    /// <summary>Engine version, e.g. 29.1.3.</summary>
    public string? Version { get; private init; }

    /// <summary>Highest API version the engine supports.</summary>
    public Version? ApiVersion { get; private init; }

    /// <summary>Lowest API version the engine accepts.</summary>
    public Version? MinApiVersion { get; private init; }

    /// <summary>API version PwshDocker uses with this engine.</summary>
    public Version? NegotiatedApiVersion { get; private init; }

    public string? Os { get; private init; }

    public string? Arch { get; private init; }

    public string? KernelVersion { get; private init; }

    public string? GoVersion { get; private init; }

    public string? GitCommit { get; private init; }

    public DateTime? BuildTime { get; private init; }

    public bool Experimental { get; private init; }

    /// <summary>Engine components (Engine, containerd, runc, docker-init) and their versions.</summary>
    public object?[] Components { get; private init; } = Array.Empty<object?>();

    /// <summary>PwshDocker version.</summary>
    public string ClientVersion => PwshDockerInfo.CoreVersion.ToString(3);

    /// <summary>Highest API version this PwshDocker build supports.</summary>
    public Version ClientApiVersion => PwshDockerInfo.MaxApiVersion;

    public static DockerVersionInfo FromJson(DockerClient client, JsonElement json)
    {
        string? Text(string name) =>
            json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        Version? ParseVersion(string name) => System.Version.TryParse(Text(name), out var parsed) ? parsed : null;

        string? platform = null;
        if (json.TryGetProperty("Platform", out var platformElement) && platformElement.ValueKind == JsonValueKind.Object &&
            platformElement.TryGetProperty("Name", out var platformName) && platformName.ValueKind == JsonValueKind.String)
        {
            platform = platformName.GetString();
        }

        return new DockerVersionInfo(client)
        {
            Endpoint = client.Endpoint.Host,
            Platform = string.IsNullOrEmpty(platform) ? null : platform,
            Version = Text("Version"),
            ApiVersion = ParseVersion("ApiVersion"),
            MinApiVersion = ParseVersion("MinAPIVersion"),
            NegotiatedApiVersion = client.ApiVersion,
            Os = Text("Os"),
            Arch = Text("Arch"),
            KernelVersion = Text("KernelVersion"),
            GoVersion = Text("GoVersion"),
            GitCommit = Text("GitCommit"),
            BuildTime = DockerTime.ParseRfc3339(Text("BuildTime")),
            Experimental = json.TryGetProperty("Experimental", out var experimental) && experimental.ValueKind == JsonValueKind.True,
            Components = json.TryGetProperty("Components", out var components) && components.ValueKind == JsonValueKind.Array
                ? (object?[])PSJson.ToPSObject(components)!
                : Array.Empty<object?>(),
        };
    }

    public override string ToString() => $"{Context}: Docker {Version} (API {ApiVersion}, using {NegotiatedApiVersion})";
}

/// <summary>Adds engine affinity to dynamic payloads (e.g. docker info) returned as PSCustomObjects.</summary>
public static class DockerPSObject
{
    /// <summary>Converts JSON to a PSCustomObject tagged with a type name and the engine's Context.</summary>
    public static PSObject Create(DockerClient client, JsonElement json, string typeName)
    {
        var result = PSJson.ToPSObject(json) as PSObject ?? new PSObject();
        result.TypeNames.Insert(0, typeName);
        if (result.Properties["Context"] is null)
        {
            result.Properties.Add(new PSNoteProperty("Context", client.ContextName));
        }

        return result;
    }
}
