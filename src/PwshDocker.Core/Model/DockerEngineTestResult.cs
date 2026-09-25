namespace PwshDocker;

/// <summary>Result of Test-DockerEngine -Detailed.</summary>
public sealed class DockerEngineTestResult
{
    public string Context { get; init; } = string.Empty;

    public string? Endpoint { get; init; }

    public bool Reachable { get; init; }

    /// <summary>Round-trip time of GET /_ping in milliseconds.</summary>
    public double? LatencyMs { get; init; }

    public Version? ApiVersion { get; init; }

    public Version? NegotiatedApiVersion { get; init; }

    public string? OSType { get; init; }

    public string? Server { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// Creates an operation that resolves the context and pings the engine. It never fails: problems are reported in
    /// <see cref="Error"/> with <see cref="Reachable"/> = false.
    /// </summary>
    public static DockerOperation CreateOperation(string contextNameOrHost, TimeSpan timeout) =>
        new(async operation =>
        {
            DockerClient client;
            try
            {
                client = DockerClient.Get(contextNameOrHost);
            }
            catch (Exception ex)
            {
                return new DockerEngineTestResult { Context = contextNameOrHost, Reachable = false, Error = DockerErrors.Unwrap(ex).Message };
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                var ping = await client.PingAsync(limit.Token).ConfigureAwait(false);
                return new DockerEngineTestResult
                {
                    Context = client.ContextName,
                    Endpoint = client.Endpoint.Host,
                    Reachable = true,
                    LatencyMs = Math.Round(ping.Latency.TotalMilliseconds, 2),
                    ApiVersion = ping.ApiVersion,
                    NegotiatedApiVersion = ping.NegotiatedApiVersion,
                    OSType = ping.OSType,
                    Server = ping.Server,
                };
            }
            catch (OperationCanceledException) when (!operation.CancellationToken.IsCancellationRequested)
            {
                return Failed(client, $"No response within {timeout.TotalSeconds:0.#} seconds.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(client, DockerErrors.Unwrap(ex).Message);
            }
        }, contextNameOrHost);

    private static DockerEngineTestResult Failed(DockerClient client, string error) => new()
    {
        Context = client.ContextName,
        Endpoint = client.Endpoint.Host,
        Reachable = false,
        Error = error,
    };

    public override string ToString() => Reachable ? $"{Context}: reachable ({LatencyMs} ms)" : $"{Context}: unreachable ({Error})";
}
