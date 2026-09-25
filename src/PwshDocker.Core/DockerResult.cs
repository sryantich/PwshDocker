using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PwshDocker;

/// <summary>A buffered Engine API response.</summary>
public sealed class DockerResult
{
    private JsonElement? _json;
    private bool _jsonParsed;

    internal DockerResult(DockerClient client, DockerRequest request, int statusCode, string? contentType,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Client = client;
        Request = request;
        StatusCode = statusCode;
        ContentType = contentType;
        Headers = headers;
        Body = body;
    }

    public DockerClient Client { get; }

    public DockerRequest Request { get; }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public byte[] Body { get; }

    /// <summary>True for 304 Not Modified (e.g. starting a container that is already running).</summary>
    public bool NotModified => StatusCode == 304;

    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>The parsed JSON body, or null when the body is empty or not JSON.</summary>
    public JsonElement? Json
    {
        get
        {
            if (!_jsonParsed)
            {
                _json = TryParseJson(Body);
                _jsonParsed = true;
            }

            return _json;
        }
    }

    /// <summary>The body as PowerShell objects (JSON), a string (text), or null (empty).</summary>
    public object? GetContent()
    {
        if (Body.Length == 0)
        {
            return null;
        }

        return Json is { } json ? PSJson.ToPSObject(json) : Text;
    }

    private static JsonElement? TryParseJson(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        var first = body.AsSpan().TrimStart(" \t\r\n"u8);
        if (first.IsEmpty || (first[0] != (byte)'{' && first[0] != (byte)'[' && first[0] != (byte)'"'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(response.Headers);
        Add(response.Content.Headers);
        return headers;

        void Add(HttpHeaders source)
        {
            foreach (var header in source)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }
    }

    public override string ToString() => $"{StatusCode} {ContentType} ({Body.Length} bytes)";
}

/// <summary>An open streaming response (logs, events, pulls, attach). Dispose to close the connection.</summary>
public sealed class DockerStreamResponse : IDisposable
{
    private readonly HttpResponseMessage _response;

    internal DockerStreamResponse(DockerClient client, DockerRequest request, HttpResponseMessage response, Stream stream)
    {
        Client = client;
        Request = request;
        _response = response;
        Stream = stream;
        StatusCode = (int)response.StatusCode;
        ContentType = response.Content.Headers.ContentType?.MediaType;
        Headers = DockerResult.CollectHeaders(response);
    }

    public DockerClient Client { get; }

    public DockerRequest Request { get; }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The response body. For upgraded (101) connections this stream is also writable.</summary>
    public Stream Stream { get; }

    /// <summary>True when the engine multiplexes stdout/stderr with 8-byte frame headers.</summary>
    public bool IsMultiplexed => string.Equals(ContentType, "application/vnd.docker.multiplexed-stream", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Stream.Dispose();
        _response.Dispose();
    }
}

/// <summary>Result of GET /_ping: engine capabilities and the negotiated API version.</summary>
public sealed class DockerPing
{
    internal DockerPing(HttpResponseMessage response, TimeSpan latency)
    {
        Latency = latency;
        var headers = DockerResult.CollectHeaders(response);
        if (headers.TryGetValue("Api-Version", out var apiVersion) && Version.TryParse(apiVersion, out var parsed))
        {
            ApiVersion = parsed;
        }

        OSType = headers.TryGetValue("OSType", out var osType) ? osType : null;
        Experimental = headers.TryGetValue("Docker-Experimental", out var experimental) &&
            string.Equals(experimental, "true", StringComparison.OrdinalIgnoreCase);
        BuilderVersion = headers.TryGetValue("Builder-Version", out var builder) ? builder : null;
        SwarmStatus = headers.TryGetValue("Swarm", out var swarm) ? swarm : null;
        Server = headers.TryGetValue("Server", out var server) ? server : null;
        NegotiatedApiVersion = Negotiate(ApiVersion);
    }

    /// <summary>Highest API version the engine supports.</summary>
    public Version? ApiVersion { get; }

    /// <summary>API version PwshDocker uses with this engine.</summary>
    public Version NegotiatedApiVersion { get; }

    public string? OSType { get; }

    public bool Experimental { get; }

    public string? BuilderVersion { get; }

    public string? SwarmStatus { get; }

    public string? Server { get; }

    public TimeSpan Latency { get; }

    /// <summary>min(engine version, PwshDocker max), or DOCKER_API_VERSION when set.</summary>
    internal static Version Negotiate(Version? engineVersion)
    {
        var pinned = Environment.GetEnvironmentVariable("DOCKER_API_VERSION");
        if (!string.IsNullOrEmpty(pinned) && Version.TryParse(pinned.TrimStart('v', 'V'), out var pinnedVersion))
        {
            return pinnedVersion;
        }

        if (engineVersion is null)
        {
            return new Version(1, 24); // engines older than 1.25 do not send Api-Version
        }

        return engineVersion < PwshDockerInfo.MaxApiVersion ? engineVersion : PwshDockerInfo.MaxApiVersion;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"API {ApiVersion} ({OSType}), negotiated {NegotiatedApiVersion}, {Latency.TotalMilliseconds:0.0} ms");
}
