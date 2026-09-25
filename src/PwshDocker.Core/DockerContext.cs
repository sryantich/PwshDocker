using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PwshDocker;

/// <summary>Reads and updates the docker CLI configuration file ($DOCKER_CONFIG/config.json).</summary>
public static class DockerConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>$DOCKER_CONFIG, or ~/.docker.</summary>
    public static string ConfigDirectory
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
            return !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        }
    }

    public static string FilePath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Reads config.json; returns an empty object when the file does not exist.</summary>
    public static JsonObject Read()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new DockerException($"Cannot parse the Docker config file '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>Gets a top-level string property (e.g. currentContext, credsStore).</summary>
    public static string? GetString(string property) =>
        Read()[property] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    /// <summary>Sets currentContext like `docker context use` (the default context clears the setting).</summary>
    public static void SetCurrentContext(string? name)
    {
        var config = Read();
        if (string.IsNullOrEmpty(name) || name == DockerContextStore.DefaultContextName)
        {
            config.Remove("currentContext");
        }
        else
        {
            config["currentContext"] = name;
        }

        Write(config);
    }

    /// <summary>Writes config.json atomically, preserving unknown settings and file permissions.</summary>
    public static void Write(JsonObject config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var path = FilePath;
        var temp = path + ".pwshdocker.tmp";
        File.WriteAllText(temp, config.ToJsonString(WriteOptions) + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.Exists(path)
                ? File.GetUnixFileMode(path)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(temp, mode);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A Docker context: a named engine endpoint, compatible with `docker context`.</summary>
public sealed class DockerContext
{
    private DockerEndpoint? _endpoint;

    internal DockerContext(string name, string dockerHost, string source)
    {
        Name = name;
        DockerHost = dockerHost;
        Source = source;
    }

    public string Name { get; }

    /// <summary>The context itself (every PwshDocker object exposes the engine it belongs to as Context).</summary>
    public string Context => Name;

    public string? Description { get; internal init; }

    /// <summary>Engine address, e.g. npipe:////./pipe/dockerDesktopLinuxEngine.</summary>
    public string DockerHost { get; }

    public bool SkipTlsVerify { get; internal init; }

    /// <summary>True when TLS certificates are configured for this context.</summary>
    public bool TlsConfigured => Tls is { CertFile: not null } || Tls is { CaFile: not null };

    /// <summary>True when this is the context commands use when -Context is omitted.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Where the context came from: Default, Environment (DOCKER_HOST), Store, or Host (an ad hoc engine URI).</summary>
    public string Source { get; }

    /// <summary>Directory holding meta.json for stored contexts.</summary>
    public string? StorePath { get; internal init; }

    /// <summary>Extra metadata stored with the context.</summary>
    public PSObject? Metadata { get; internal init; }

    internal DockerTlsSettings? Tls { get; init; }

    /// <summary>Parses the engine address (with TLS settings) for this context.</summary>
    public DockerEndpoint GetEndpoint() => _endpoint ??= DockerEndpoint.Parse(DockerHost, Tls);

    public override string ToString() => Name;
}

/// <summary>Reads the docker CLI context store (~/.docker/contexts) and resolves the current context.</summary>
public static class DockerContextStore
{
    public const string DefaultContextName = "default";

    /// <summary>Platform default engine address.</summary>
    public static string DefaultHost => OperatingSystem.IsWindows()
        ? "npipe:////./pipe/docker_engine"
        : "unix:///var/run/docker.sock";

    public static string MetaDirectory => Path.Combine(DockerConfigFile.ConfigDirectory, "contexts", "meta");

    public static string TlsDirectory => Path.Combine(DockerConfigFile.ConfigDirectory, "contexts", "tls");

    /// <summary>Store directory name for a context: lowercase hex SHA-256 of the name (same as the docker CLI).</summary>
    public static string GetContextId(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    /// <summary>
    /// Current context name using docker CLI precedence: DOCKER_HOST (implies default), DOCKER_CONTEXT,
    /// currentContext in config.json, then default.
    /// </summary>
    public static string GetCurrentContextName()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return DefaultContextName;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        return DockerConfigFile.GetString("currentContext") ?? DefaultContextName;
    }

    /// <summary>The default context: DOCKER_HOST (with DOCKER_TLS* settings) or the platform default engine.</summary>
    public static DockerContext GetDefaultContext()
    {
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        var fromEnvironment = !string.IsNullOrEmpty(host);
        var tls = DockerTlsSettings.FromEnvironment();
        return new DockerContext(DefaultContextName, fromEnvironment ? host! : DefaultHost, fromEnvironment ? "Environment" : "Default")
        {
            Description = "Current DOCKER_HOST based configuration",
            SkipTlsVerify = tls?.SkipVerify ?? false,
            Tls = tls,
        };
    }

    /// <summary>All contexts: default first, then the store sorted by name.</summary>
    /// <param name="currentName">Name to flag as current; null uses <see cref="GetCurrentContextName"/>.</param>
    public static IReadOnlyList<DockerContext> GetContexts(string? currentName = null)
    {
        currentName ??= GetCurrentContextName();
        var contexts = new List<DockerContext> { GetDefaultContext() };
        contexts.AddRange(ReadStore().OrderBy(c => c.Name, StringComparer.Ordinal));
        foreach (var context in contexts)
        {
            context.IsCurrent = string.Equals(context.Name, currentName, StringComparison.Ordinal);
        }

        return contexts;
    }

    /// <summary>Gets a context by exact name, or null.</summary>
    public static DockerContext? GetContext(string name)
    {
        if (name == DefaultContextName)
        {
            return GetDefaultContext();
        }

        var directory = Path.Combine(MetaDirectory, GetContextId(name));
        return File.Exists(Path.Combine(directory, "meta.json")) ? ReadContext(directory) : null;
    }

    /// <summary>
    /// Resolves a context name or engine URI. URIs become ad hoc contexts whose name is the URI,
    /// with TLS settings from DOCKER_TLS* when the scheme is tcp.
    /// </summary>
    public static DockerContext Resolve(string nameOrHost)
    {
        if (string.IsNullOrWhiteSpace(nameOrHost))
        {
            throw new ArgumentException("A context name or engine URI is required.", nameof(nameOrHost));
        }

        nameOrHost = nameOrHost.Trim();
        if (DockerEndpoint.IsHostUri(nameOrHost))
        {
            var tls = nameOrHost.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? DockerTlsSettings.FromEnvironment() : null;
            var context = new DockerContext(nameOrHost, nameOrHost, "Host")
            {
                Description = "Engine address",
                Tls = tls,
                SkipTlsVerify = tls?.SkipVerify ?? false,
            };
            context.GetEndpoint(); // validate early
            return context;
        }

        return GetContext(nameOrHost)
            ?? throw new DockerContextNotFoundException(nameOrHost, GetContexts().Select(c => c.Name));
    }

    private static IEnumerable<DockerContext> ReadStore()
    {
        if (!Directory.Exists(MetaDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(MetaDirectory))
        {
            if (!File.Exists(Path.Combine(directory, "meta.json")))
            {
                continue;
            }

            DockerContext? context;
            try
            {
                context = ReadContext(directory);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue; // skip unreadable contexts, like the CLI does
            }

            if (context is not null)
            {
                yield return context;
            }
        }
    }

    private static DockerContext? ReadContext(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "meta.json")));
        var root = document.RootElement;
        if (!root.TryGetProperty("Name", out var nameElement) || nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        string? host = null;
        var skipTlsVerify = false;
        if (root.TryGetProperty("Endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object &&
            endpoints.TryGetProperty("docker", out var docker) && docker.ValueKind == JsonValueKind.Object)
        {
            if (docker.TryGetProperty("Host", out var hostElement))
            {
                host = hostElement.GetString();
            }

            if (docker.TryGetProperty("SkipTLSVerify", out var skip) && skip.ValueKind is JsonValueKind.True)
            {
                skipTlsVerify = true;
            }
        }

        string? description = null;
        PSObject? metadata = null;
        if (root.TryGetProperty("Metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
        {
            metadata = PSJson.ToPSObject(metadataElement) as PSObject;
            if (metadataElement.TryGetProperty("Description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString();
            }
        }

        // TLS material lives in contexts/tls/<id>/docker/{ca,cert,key}.pem. The CLI enables TLS when material
        // exists or SkipTLSVerify is set.
        var tlsDirectory = Path.Combine(TlsDirectory, Path.GetFileName(directory), "docker");
        DockerTlsSettings? tls = null;
        if (Directory.Exists(tlsDirectory) || skipTlsVerify)
        {
            tls = DockerTlsSettings.FromDirectory(tlsDirectory, skipTlsVerify);
        }

        return new DockerContext(name, host ?? DefaultHost, "Store")
        {
            Description = description,
            SkipTlsVerify = skipTlsVerify,
            StorePath = directory,
            Metadata = metadata,
            Tls = tls,
        };
    }
}
