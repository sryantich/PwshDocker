using System.Diagnostics;
using System.Management.Automation;
using System.Threading.Channels;

namespace PwshDocker;

/// <summary>A unit of async work for <see cref="DockerParallel"/>.</summary>
public sealed class DockerOperation
{
    public DockerOperation(Func<DockerOperationContext, Task<object?>> run, object? state = null)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        State = state;
    }

    /// <summary>Caller-defined value returned with the result (e.g. the input object).</summary>
    public object? State { get; }

    internal Func<DockerOperationContext, Task<object?>> Run { get; }

    /// <summary>An operation that sends one request and returns the buffered <see cref="DockerResult"/>.</summary>
    public static DockerOperation FromRequest(DockerClient client, DockerRequest request, object? state = null) =>
        new(async context => await client.InvokeAsync(request, context.CancellationToken).ConfigureAwait(false), state ?? request.State);
}

/// <summary>Passed to running operations: cancellation and progress reporting.</summary>
public sealed class DockerOperationContext
{
    private readonly ChannelWriter<DockerOperationEvent> _events;

    internal DockerOperationContext(ChannelWriter<DockerOperationEvent> events, object? state, CancellationToken cancellationToken)
    {
        _events = events;
        State = state;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    public object? State { get; }

    /// <summary>Reports progress; the pipeline thread writes it with WriteProgress.</summary>
    public void ReportProgress(ProgressRecord record) =>
        _events.TryWrite(new DockerOperationProgress(record) { State = State });
}

/// <summary>Base type for events produced by <see cref="DockerParallel.Invoke"/>.</summary>
public abstract class DockerOperationEvent
{
    public object? State { get; init; }
}

/// <summary>A completed operation: either a result or an error.</summary>
public sealed class DockerOperationResult : DockerOperationEvent
{
    public object? Result { get; init; }

    public Exception? Error { get; init; }

    public bool Succeeded => Error is null;

    public TimeSpan Duration { get; init; }
}

/// <summary>Progress reported by a running operation.</summary>
public sealed class DockerOperationProgress : DockerOperationEvent
{
    public DockerOperationProgress(ProgressRecord record) => Record = record;

    public ProgressRecord Record { get; }
}

/// <summary>
/// Runs operations concurrently with a throttle, yielding results on the calling (pipeline) thread as they
/// complete. Stopping the pipeline cancels everything still running.
/// </summary>
public static class DockerParallel
{
    public const int DefaultThrottleLimit = 8;

    public static IEnumerable<DockerOperationEvent> Invoke(DockerOperation[] operations, int throttleLimit, Cmdlet? cmdlet)
    {
        var pending = operations.Where(operation => operation is not null).ToList();
        if (pending.Count == 0)
        {
            yield break;
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var channel = Channel.CreateUnbounded<DockerOperationEvent>(new UnboundedChannelOptions { SingleReader = true });
        var throttle = new SemaphoreSlim(Math.Max(1, throttleLimit));
        var remaining = pending.Count;

        foreach (var operation in pending)
        {
            _ = Task.Run(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var acquired = false;
                try
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    stopwatch.Restart();
                    var context = new DockerOperationContext(channel.Writer, operation.State, token);
                    var result = await operation.Run(context).ConfigureAwait(false);
                    channel.Writer.TryWrite(new DockerOperationResult { State = operation.State, Result = result, Duration = stopwatch.Elapsed });
                }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new DockerOperationResult { State = operation.State, Error = DockerErrors.Unwrap(ex), Duration = stopwatch.Elapsed });
                }
                finally
                {
                    if (acquired)
                    {
                        throttle.Release();
                    }

                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        channel.Writer.TryComplete();
                    }
                }
            }, CancellationToken.None);
        }

        try
        {
            var reader = channel.Reader;
            while (true)
            {
                var waitToRead = reader.WaitToReadAsync(CancellationToken.None);
                var more = waitToRead.IsCompletedSuccessfully
                    ? waitToRead.Result
                    : DockerSync.Wait(waitToRead.AsTask(), cmdlet, cancellation);
                if (!more)
                {
                    yield break;
                }

                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    /// <summary>
    /// Runs operations for a PowerShell command: progress is written with the command's WriteProgress, failures
    /// become non-terminating errors of the command, and only successful results are yielded (in completion order).
    /// </summary>
    public static IEnumerable<DockerOperationResult> Run(DockerOperation[] operations, int throttleLimit, Cmdlet cmdlet)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        foreach (var item in Invoke(operations, throttleLimit, cmdlet))
        {
            switch (item)
            {
                case DockerOperationProgress progress:
                    cmdlet.WriteProgress(progress.Record);
                    break;
                case DockerOperationResult { Succeeded: false } failed:
                    cmdlet.WriteError(DockerErrors.ToErrorRecord(failed.Error!, failed.State));
                    break;
                case DockerOperationResult result:
                    yield return result;
                    break;
            }
        }
    }

    /// <summary>Runs one operation with Ctrl+C support and returns its result (errors are rethrown).</summary>
    public static object? InvokeOne(DockerOperation operation, Cmdlet? cmdlet)
    {
        foreach (var item in Invoke(new[] { operation }, 1, cmdlet))
        {
            if (item is DockerOperationResult result)
            {
                if (result.Error is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error).Throw();
                }

                return result.Result;
            }
        }

        return null;
    }
}
