using System.Management.Automation;
using System.Reflection;

namespace PwshDocker;

/// <summary>Base type for all PwshDocker errors.</summary>
public class DockerException : Exception
{
    public DockerException(string message) : base(message) { }

    public DockerException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>PowerShell error category used when this error becomes an ErrorRecord.</summary>
    public virtual ErrorCategory Category => ErrorCategory.NotSpecified;

    /// <summary>Stable identifier used as the ErrorRecord's FullyQualifiedErrorId.</summary>
    public virtual string ErrorId => "DockerError";

    /// <summary>Suggested action shown with the error, if any.</summary>
    public virtual string? RecommendedAction => null;
}

/// <summary>The engine answered with an HTTP error status.</summary>
public sealed class DockerApiException : DockerException
{
    public DockerApiException(int statusCode, string message, string? method = null, string? path = null, string? context = null)
        : base(message)
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        Context = context;
    }

    public int StatusCode { get; }

    public string? Method { get; }

    public string? Path { get; }

    public string? Context { get; }

    public override ErrorCategory Category => StatusCode switch
    {
        400 => ErrorCategory.InvalidArgument,
        401 or 403 => ErrorCategory.PermissionDenied,
        404 => ErrorCategory.ObjectNotFound,
        409 when Message.Contains("already in use", StringComparison.OrdinalIgnoreCase)
              || Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => ErrorCategory.ResourceExists,
        409 => ErrorCategory.InvalidOperation,
        501 => ErrorCategory.NotImplemented,
        503 => ErrorCategory.ResourceUnavailable,
        _ => ErrorCategory.NotSpecified,
    };

    public override string ErrorId => StatusCode switch
    {
        400 => "DockerBadRequest",
        401 => "DockerUnauthorized",
        403 => "DockerForbidden",
        404 => "DockerNotFound",
        409 => "DockerConflict",
        500 => "DockerServerError",
        501 => "DockerNotImplemented",
        503 => "DockerUnavailable",
        _ => $"DockerHttp{StatusCode}",
    };
}

/// <summary>The engine could not be reached.</summary>
public sealed class DockerConnectionException : DockerException
{
    public DockerConnectionException(string endpoint, string context, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
        Context = context;
    }

    public string Endpoint { get; }

    public string Context { get; }

    public override ErrorCategory Category => ErrorCategory.ConnectionError;

    public override string ErrorId => "DockerConnectionFailed";

    public override string RecommendedAction =>
        $"Make sure the Docker engine is running and reachable at {Endpoint}, or choose another engine with -Context or Use-DockerContext.";
}

/// <summary>A context name could not be resolved.</summary>
public sealed class DockerContextNotFoundException : DockerException
{
    public DockerContextNotFoundException(string name, IEnumerable<string> available)
        : base($"Docker context '{name}' was not found. Available contexts: {string.Join(", ", available)}.")
    {
        Name = name;
    }

    public string Name { get; }

    public override ErrorCategory Category => ErrorCategory.ObjectNotFound;

    public override string ErrorId => "DockerContextNotFound";

    public override string RecommendedAction => "Run Get-DockerContext to list contexts, or pass an engine URI such as tcp://host:2375.";
}

/// <summary>An error reported inside a streamed response (for example a failed image pull).</summary>
public sealed class DockerStreamException : DockerException
{
    public DockerStreamException(string message) : base(message) { }

    public override ErrorCategory Category => ErrorCategory.InvalidResult;

    public override string ErrorId => "DockerStreamError";
}

/// <summary>Helpers that turn exceptions into well-formed PowerShell error records.</summary>
public static class DockerErrors
{
    /// <summary>Strips wrapper exceptions (PowerShell method invocation, aggregate, reflection) to find the real error.</summary>
    public static Exception Unwrap(Exception exception)
    {
        var current = exception;
        while (true)
        {
            switch (current)
            {
                case DockerException:
                    return current;
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    current = aggregate.InnerExceptions[0];
                    continue;
                case TargetInvocationException { InnerException: { } invocationInner }:
                    current = invocationInner;
                    continue;
                case MethodInvocationException { InnerException: { } methodInner }:
                    current = methodInner;
                    continue;
                case { InnerException: DockerException dockerInner }:
                    current = dockerInner;
                    continue;
            }

            return current;
        }
    }

    /// <summary>Creates an ErrorRecord with a stable error id and category for any exception.</summary>
    public static ErrorRecord ToErrorRecord(Exception exception, object? target = null)
    {
        var error = Unwrap(exception);
        switch (error)
        {
            case DockerException docker:
            {
                var record = new ErrorRecord(docker, docker.ErrorId, docker.Category, target);
                if (docker.RecommendedAction is { } action)
                {
                    record.ErrorDetails = new ErrorDetails(docker.Message) { RecommendedAction = action };
                }

                return record;
            }
            case IContainsErrorRecord { ErrorRecord: { } inner }:
                return new ErrorRecord(inner, error);
            case OperationCanceledException:
                return new ErrorRecord(error, "DockerOperationCanceled", ErrorCategory.OperationStopped, target);
            case TimeoutException:
                return new ErrorRecord(error, "DockerTimeout", ErrorCategory.OperationTimeout, target);
            case ArgumentException:
                return new ErrorRecord(error, "DockerInvalidArgument", ErrorCategory.InvalidArgument, target);
            default:
                return new ErrorRecord(error, "DockerUnexpectedError", ErrorCategory.NotSpecified, target);
        }
    }

    /// <summary>Creates an ErrorRecord from a message (for validation failures detected in script).</summary>
    public static ErrorRecord Create(string message, string errorId, ErrorCategory category, object? target = null) =>
        new(new DockerException(message), errorId, category, target);
}
