using System.Globalization;

namespace PwshDocker;

/// <summary>How PwshDocker reaches an engine.</summary>
public enum DockerTransport
{
    NamedPipe,
    UnixSocket,
    Tcp,
    Ssh,
}

/// <summary>TLS settings for TCP endpoints.</summary>
public sealed class DockerTlsSettings
{
    public bool Enabled { get; init; }

    /// <summary>Accept any server certificate (DOCKER_TLS without DOCKER_TLS_VERIFY, or SkipTLSVerify in a context).</summary>
    public bool SkipVerify { get; init; }

    public string? CaFile { get; init; }

    public string? CertFile { get; init; }

    public string? KeyFile { get; init; }

    internal string CacheKey => $"{Enabled}|{SkipVerify}|{CaFile}|{CertFile}|{KeyFile}";

    /// <summary>Reads DOCKER_TLS_VERIFY / DOCKER_TLS / DOCKER_CERT_PATH the way the docker CLI does.</summary>
    public static DockerTlsSettings? FromEnvironment()
    {
        var verify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_TLS_VERIFY"));
        var tls = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_TLS"));
        if (!verify && !tls)
        {
            return null;
        }

        var certPath = Environment.GetEnvironmentVariable("DOCKER_CERT_PATH");
        if (string.IsNullOrEmpty(certPath))
        {
            certPath = DockerConfigFile.ConfigDirectory;
        }

        return FromDirectory(certPath, skipVerify: !verify);
    }

    /// <summary>Loads ca.pem / cert.pem / key.pem from a directory (missing files are ignored).</summary>
    public static DockerTlsSettings FromDirectory(string directory, bool skipVerify) => new()
    {
        Enabled = true,
        SkipVerify = skipVerify,
        CaFile = ExistingOrNull(Path.Combine(directory, "ca.pem")),
        CertFile = ExistingOrNull(Path.Combine(directory, "cert.pem")),
        KeyFile = ExistingOrNull(Path.Combine(directory, "key.pem")),
    };

    private static string? ExistingOrNull(string path) => File.Exists(path) ? path : null;
}

/// <summary>A parsed engine address (DOCKER_HOST syntax) plus TLS settings.</summary>
public sealed class DockerEndpoint
{
    private DockerEndpoint(string host, DockerTransport transport)
    {
        Host = host;
        Transport = transport;
    }

    /// <summary>The engine address as given, e.g. npipe:////./pipe/docker_engine.</summary>
    public string Host { get; }

    public DockerTransport Transport { get; }

    public string? PipeServer { get; private init; }

    public string? PipeName { get; private init; }

    public string? SocketPath { get; private init; }

    public string? HostName { get; private init; }

    public int Port { get; private init; }

    public string? SshUser { get; private init; }

    public DockerTlsSettings? Tls { get; private init; }

    public bool UsesTls => Transport == DockerTransport.Tcp && Tls is { Enabled: true };

    /// <summary>Base address for HTTP requests. Pipes and sockets use a placeholder host, like the Go client.</summary>
    public Uri BaseAddress => Transport == DockerTransport.Tcp
        ? new Uri(string.Create(CultureInfo.InvariantCulture, $"{(UsesTls ? "https" : "http")}://{FormatHostName(HostName!)}:{Port}/"))
        : new Uri("http://api.moby.localhost/");

    internal string CacheKey => $"{Host}|{Tls?.CacheKey}";

    public override string ToString() => Host;

    /// <summary>Returns true when the value looks like an engine URI rather than a context name.</summary>
    public static bool IsHostUri(string? value) =>
        !string.IsNullOrEmpty(value) && value.Contains("://", StringComparison.Ordinal);

    /// <summary>Parses npipe://, unix://, tcp://, http://, https:// and ssh:// engine addresses.</summary>
    public static DockerEndpoint Parse(string host, DockerTlsSettings? tls = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("A Docker engine address is required.", nameof(host));
        }

        host = host.Trim();
        var separator = host.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw InvalidHost(host, "expected a URI such as npipe:////./pipe/docker_engine, unix:///var/run/docker.sock or tcp://host:2376");
        }

        var scheme = host[..separator].ToLowerInvariant();
        var rest = host[(separator + 3)..];

        switch (scheme)
        {
            case "npipe":
            {
                // npipe:////./pipe/docker_engine -> rest "//./pipe/docker_engine"
                var parts = rest.Replace('\\', '/').TrimStart('/').Split('/', 3);
                if (parts.Length != 3 || parts[0].Length == 0 || !parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) || parts[2].Length == 0)
                {
                    throw InvalidHost(host, "expected npipe:////<server>/pipe/<name>");
                }

                return new DockerEndpoint(host, DockerTransport.NamedPipe) { PipeServer = parts[0], PipeName = parts[2] };
            }

            case "unix":
            {
                var path = rest;
                // unix:///c:/path/engine.sock on Windows -> c:/path/engine.sock
                if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && path[2] == ':')
                {
                    path = path[1..];
                }

                if (path.Length == 0)
                {
                    throw InvalidHost(host, "expected unix:///path/to/docker.sock");
                }

                return new DockerEndpoint(host, DockerTransport.UnixSocket) { SocketPath = path };
            }

            case "tcp":
            case "http":
            case "https":
            {
                var authority = rest.Split('/', 2)[0];
                var (hostName, port) = SplitHostPort(authority, host);
                var effectiveTls = scheme switch
                {
                    "https" => tls ?? new DockerTlsSettings { Enabled = true },
                    "http" => null,
                    _ => tls is { Enabled: true } ? tls : null,
                };
                var useTls = effectiveTls is { Enabled: true };
                return new DockerEndpoint(host, DockerTransport.Tcp)
                {
                    HostName = hostName,
                    Port = port ?? (useTls ? 2376 : 2375),
                    Tls = effectiveTls,
                };
            }

            case "ssh":
            {
                var authority = rest.Split('/', 2)[0];
                string? user = null;
                var at = authority.LastIndexOf('@');
                if (at >= 0)
                {
                    user = authority[..at];
                    authority = authority[(at + 1)..];
                }

                var (hostName, port) = SplitHostPort(authority, host);
                return new DockerEndpoint(host, DockerTransport.Ssh) { HostName = hostName, Port = port ?? 22, SshUser = user };
            }

            default:
                throw InvalidHost(host, $"unsupported scheme '{scheme}' (supported: npipe, unix, tcp, http, https, ssh)");
        }
    }

    private static (string Host, int? Port) SplitHostPort(string authority, string original)
    {
        if (authority.Length == 0)
        {
            throw InvalidHost(original, "missing host name");
        }

        string hostName;
        string? portText = null;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                throw InvalidHost(original, "unterminated IPv6 address");
            }

            hostName = authority[1..close];
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':')
                {
                    throw InvalidHost(original, "invalid port");
                }

                portText = authority[(close + 2)..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                hostName = authority[..colon];
                portText = authority[(colon + 1)..];
            }
            else
            {
                hostName = authority;
            }
        }

        if (hostName.Length == 0)
        {
            throw InvalidHost(original, "missing host name");
        }

        if (portText is null || portText.Length == 0)
        {
            return (hostName, null);
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw InvalidHost(original, $"invalid port '{portText}'");
        }

        return (hostName, port);
    }

    private static string FormatHostName(string hostName) => hostName.Contains(':') ? $"[{hostName}]" : hostName;

    private static ArgumentException InvalidHost(string host, string reason) =>
        new($"Invalid Docker engine address '{host}': {reason}.", nameof(host));
}
