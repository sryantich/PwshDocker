using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management.Automation;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PwshDocker;

/// <summary>
/// A thread-safe connection to one engine. Instances are cached per context/endpoint for the life of the process;
/// each owns a pooled HttpClient so concurrent requests reuse or open named-pipe instances / sockets as needed.
/// </summary>
public sealed class DockerClient : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<DockerClient>> Clients = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _negotiationLock = new(1, 1);
    private volatile DockerPing? _ping;

    private DockerClient(DockerContext context)
    {
        DockerContext = context;
        Endpoint = context.GetEndpoint();
        _http = CreateHttpClient(Endpoint, context.Name);
    }

    /// <summary>The context this client connects to.</summary>
    public DockerContext DockerContext { get; }

    /// <summary>Context name (or engine URI for ad hoc connections); stamped on every object from this engine.</summary>
    public string ContextName => DockerContext.Name;

    public DockerEndpoint Endpoint { get; }

    /// <summary>Negotiated API version, or null before the first request.</summary>
    public Version? ApiVersion => _ping?.NegotiatedApiVersion;

    /// <summary>Engine OS type ("linux" or "windows"), or null before the first request.</summary>
    public string? OSType => _ping?.OSType;

    /// <summary>Result of the most recent ping (negotiation), if any.</summary>
    public DockerPing? LastPing => _ping;

    /// <summary>Gets (or creates) the cached client for a context.</summary>
    public static DockerClient Get(DockerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = context.Name + "|" + context.GetEndpoint().CacheKey;
        return Clients.GetOrAdd(key, _ => new Lazy<DockerClient>(() => new DockerClient(context))).Value;
    }

    /// <summary>Gets (or creates) the cached client for a context name or engine URI.</summary>
    public static DockerClient Get(string nameOrHost) => Get(DockerContextStore.Resolve(nameOrHost));

    /// <summary>Disposes and forgets all cached clients (tests, or after contexts change).</summary>
    public static void ClearCache()
    {
        foreach (var key in Clients.Keys.ToArray())
        {
            if (Clients.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }
    }

    /// <summary>True when the API version in use is at least <paramref name="version"/> (e.g. "1.45").</summary>
    public bool SupportsApiVersion(string version) =>
        ApiVersion is { } negotiated && negotiated >= Version.Parse(version);

    /// <summary>Pings the engine (always sends a request) and refreshes the negotiated API version.</summary>
    public async Task<DockerPing> PingAsync(CancellationToken cancellationToken = default)
    {
        var request = new DockerRequest("GET", "/_ping") { Unversioned = true };
        using var message = request.ToHttpRequestMessage(null);
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendHttpAsync(message, request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        if ((int)response.StatusCode >= 400)
        {
            throw await CreateApiExceptionAsync(response, request, cancellationToken).ConfigureAwait(false);
        }

        var ping = new DockerPing(response, stopwatch.Elapsed);
        _ping = ping;
        return ping;
    }

    /// <summary>Returns the negotiated API version, pinging the engine once if needed.</summary>
    public async ValueTask<Version> GetApiVersionAsync(CancellationToken cancellationToken = default)
    {
        if (_ping is { } ping)
        {
            return ping.NegotiatedApiVersion;
        }

        await _negotiationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (_ping ?? await PingAsync(cancellationToken).ConfigureAwait(false)).NegotiatedApiVersion;
        }
        finally
        {
            _negotiationLock.Release();
        }
    }

    /// <summary>Sends a request and buffers the response. HTTP errors throw <see cref="DockerApiException"/>.</summary>
    public async Task<DockerResult> InvokeAsync(DockerRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new DockerResult(this, request, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
            DockerResult.CollectHeaders(response), body);
    }

    /// <summary>Sends a request and returns once headers arrive, leaving the body open for streaming.</summary>
    public async Task<DockerStreamResponse> OpenStreamAsync(DockerRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DockerStreamResponse(this, request, response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Streams newline-delimited JSON messages (pull/push/build progress, events, stats) as raw JSON elements.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> StreamJsonAsync(DockerRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await OpenStreamAsync(request, cancellationToken).ConfigureAwait(false);
        await foreach (var message in JsonMessageReader.ReadAsync(response.Stream, cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    #region Synchronous, Ctrl+C-aware wrappers for PowerShell

    /// <summary>Sends a request and waits for the buffered response. Pressing Ctrl+C cancels the request.</summary>
    public DockerResult Invoke(DockerRequest request, Cmdlet? cmdlet = null)
    {
        using var cts = new CancellationTokenSource();
        return DockerSync.Wait(InvokeAsync(request, cts.Token), cmdlet, cts);
    }

    /// <summary>Sends a request and returns the JSON body converted to PowerShell objects.</summary>
    public object? InvokeContent(DockerRequest request, Cmdlet? cmdlet = null) => Invoke(request, cmdlet).GetContent();

    /// <summary>Pings the engine. Pressing Ctrl+C cancels the request.</summary>
    public DockerPing Ping(Cmdlet? cmdlet = null)
    {
        using var cts = new CancellationTokenSource();
        return DockerSync.Wait(PingAsync(cts.Token), cmdlet, cts);
    }

    /// <summary>Returns the negotiated API version, pinging the engine once if needed.</summary>
    public Version GetApiVersion(Cmdlet? cmdlet = null)
    {
        using var cts = new CancellationTokenSource();
        return DockerSync.Wait(GetApiVersionAsync(cts.Token).AsTask(), cmdlet, cts);
    }

    /// <summary>
    /// Streams newline-delimited JSON messages as PowerShell objects as they arrive. Enumeration stops when the
    /// engine closes the stream, the consumer stops, or Ctrl+C is pressed.
    /// </summary>
    public IEnumerable<object?> StreamJson(DockerRequest request, Cmdlet? cmdlet = null)
    {
        var cts = new CancellationTokenSource();
        foreach (var element in DockerSync.Enumerate(StreamJsonAsync(request, cts.Token), cmdlet, cts))
        {
            yield return PSJson.ToPSObject(element);
        }
    }

    #endregion

    private async Task<HttpResponseMessage> SendAsync(DockerRequest request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeout = null;
        if (request.Timeout is { } limit)
        {
            timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);
        }

        using var timeoutScope = timeout;
        var token = timeout?.Token ?? cancellationToken;
        try
        {
            var version = request.Unversioned ? null : await GetApiVersionAsync(token).ConfigureAwait(false);
            using var message = request.ToHttpRequestMessage(version);
            var response = await SendHttpAsync(message, request, completion, token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
            {
                try
                {
                    throw await CreateApiExceptionAsync(response, request, token).ConfigureAwait(false);
                }
                finally
                {
                    response.Dispose();
                }
            }

            return response;
        }
        catch (OperationCanceledException) when (timeout is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Docker engine at {Endpoint.Host} did not respond to {request.Method} {request.Path} within {request.Timeout}.");
        }
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage message, DockerRequest request,
        HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(message, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw CreateConnectionException(ex, request);
        }
        catch (DockerConnectionException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw CreateConnectionException(ex, request);
        }
    }

    private DockerConnectionException CreateConnectionException(Exception exception, DockerRequest request)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DockerConnectionException existing)
            {
                return existing;
            }
        }

        var reason = (exception.InnerException?.Message ?? exception.Message).TrimEnd('.');
        return new DockerConnectionException(Endpoint.Host, ContextName,
            $"{reason}. Cannot connect to the Docker engine at {Endpoint.Host} ({request.Method} {request.Path}).",
            exception);
    }

    private async Task<DockerApiException> CreateApiExceptionAsync(HttpResponseMessage response, DockerRequest request, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? message = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            message = ExtractErrorMessage(text);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // fall back to the status line
        }

        message ??= $"{status} {response.ReasonPhrase}".Trim();
        return new DockerApiException(status, message, request.Method, request.Path, ContextName);
    }

    internal static string? ExtractErrorMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // not JSON; use the text as is
        }

        return text.Trim();
    }

    private static HttpClient CreateHttpClient(DockerEndpoint endpoint, string contextName)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        switch (endpoint.Transport)
        {
            case DockerTransport.NamedPipe:
                handler.ConnectCallback = (_, token) => ConnectNamedPipeAsync(endpoint, contextName, token);
                break;
            case DockerTransport.UnixSocket:
                handler.ConnectCallback = (_, token) => ConnectUnixSocketAsync(endpoint, token);
                break;
            case DockerTransport.Tcp:
                if (endpoint.UsesTls)
                {
                    handler.SslOptions = CreateSslOptions(endpoint);
                }

                break;
            case DockerTransport.Ssh:
                throw new NotSupportedException(
                    $"ssh:// engine addresses are not supported yet (planned for phase 2): {endpoint.Host}. Use a tcp:// address or a context that points to one.");
        }

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PwshDockerInfo.UserAgent);
        return http;
    }

    private static async ValueTask<Stream> ConnectNamedPipeAsync(DockerEndpoint endpoint, string contextName, CancellationToken cancellationToken)
    {
        var server = endpoint.PipeServer ?? ".";
        var name = endpoint.PipeName!;
        // A missing local pipe would otherwise wait for the whole connect timeout; fail fast instead.
        if (server == "." && OperatingSystem.IsWindows() && !LocalPipeExists(name))
        {
            throw new DockerConnectionException(endpoint.Host, contextName,
                $"The named pipe \\\\.\\pipe\\{name} does not exist; is the Docker engine running? Cannot connect to the Docker engine at {endpoint.Host}.");
        }

        var pipe = new NamedPipeClientStream(server, name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool LocalPipeExists(string name)
    {
        try
        {
            var fullName = @"\\.\pipe\" + name;
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(p, fullName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // cannot tell; let the connect attempt decide
        }
    }

    private static async ValueTask<Stream> ConnectUnixSocketAsync(DockerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath!), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static SslClientAuthenticationOptions CreateSslOptions(DockerEndpoint endpoint)
    {
        var tls = endpoint.Tls!;
        var options = new SslClientAuthenticationOptions();
        if (tls.CertFile is not null && tls.KeyFile is not null)
        {
            var certificate = X509Certificate2.CreateFromPemFile(tls.CertFile, tls.KeyFile);
            if (OperatingSystem.IsWindows())
            {
                // SChannel cannot use ephemeral PEM keys; round-trip through PKCS#12.
                certificate = new X509Certificate2(certificate.Export(X509ContentType.Pkcs12));
            }

            options.ClientCertificates = new X509CertificateCollection { certificate };
        }

        X509Certificate2Collection? roots = null;
        if (tls.CaFile is not null)
        {
            roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(tls.CaFile);
        }

        options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (tls.SkipVerify || errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (roots is null || certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var serverCertificate = new X509Certificate2(certificate);
            return chain.Build(serverCertificate);
        };
        return options;
    }

    public void Dispose()
    {
        _http.Dispose();
        _negotiationLock.Dispose();
    }

    public override string ToString() => $"{ContextName} ({Endpoint.Host})";
}
