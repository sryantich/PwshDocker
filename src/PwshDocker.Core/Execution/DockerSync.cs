using System.Management.Automation;

namespace PwshDocker;

/// <summary>
/// Waits for async work on the PowerShell pipeline thread while honoring Ctrl+C: the calling command's
/// <see cref="Cmdlet.Stopping"/> flag is polled, and when set the work is cancelled and the pipeline stops.
/// </summary>
public static class DockerSync
{
    private const int PollMilliseconds = 25;

    /// <summary>Waits for a task, cancelling it if the pipeline stops.</summary>
    public static T Wait<T>(Task<T> task, Cmdlet? cmdlet, CancellationTokenSource? cancellation)
    {
        WaitCore(task, cmdlet, cancellation);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Waits for a task, cancelling it if the pipeline stops.</summary>
    public static void Wait(Task task, Cmdlet? cmdlet, CancellationTokenSource? cancellation)
    {
        WaitCore(task, cmdlet, cancellation);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Enumerates an async sequence synchronously; stopping the pipeline cancels it and closes the source.</summary>
    public static IEnumerable<T> Enumerate<T>(IAsyncEnumerable<T> source, Cmdlet? cmdlet, CancellationTokenSource cancellation)
    {
        var enumerator = source.GetAsyncEnumerator(cancellation.Token);
        try
        {
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();
                var hasNext = moveNext.IsCompletedSuccessfully
                    ? moveNext.Result
                    : Wait(moveNext.AsTask(), cmdlet, cancellation);
                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                enumerator.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Best effort: the source is being abandoned.
            }
        }
    }

    /// <summary>True when the command is being stopped (Ctrl+C, Select-Object -First, ...).</summary>
    public static bool IsStopping(Cmdlet? cmdlet)
    {
        try
        {
            return cmdlet?.Stopping ?? false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WaitCore(Task task, Cmdlet? cmdlet, CancellationTokenSource? cancellation)
    {
        while (!task.IsCompleted)
        {
            if (IsStopping(cmdlet))
            {
                cancellation?.Cancel();
                try
                {
                    // Give the operation a moment to observe cancellation so sockets close promptly.
                    task.Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                    // Expected: the task was cancelled.
                }

                throw new PipelineStoppedException();
            }

            ((IAsyncResult)task).AsyncWaitHandle.WaitOne(PollMilliseconds);
        }
    }
}
