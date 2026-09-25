using System.Management.Automation;

namespace PwshDocker;

/// <summary>
/// Entry points for PowerShell functions. Expected failures never escape as exceptions (PowerShell would record a
/// caught method exception in -ErrorVariable in addition to the real error); instead a single, well-formed error
/// record is written through the calling command and null is returned.
/// </summary>
public static class DockerCommand
{
    /// <summary>
    /// Resolves -Context values (context names or engine URIs, duplicates removed) to cached clients. With no values,
    /// uses <paramref name="currentName"/>. An unknown context is a terminating error of the calling command.
    /// </summary>
    public static DockerClient[] ResolveClients(string[]? contexts, string? currentName, Cmdlet cmdlet)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        var names = (contexts ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            names.Add(currentName ?? DockerContextStore.GetCurrentContextName());
        }

        var clients = new DockerClient[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            try
            {
                clients[i] = DockerClient.Get(names[i]);
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                cmdlet.ThrowTerminatingError(DockerErrors.ToErrorRecord(ex, names[i]));
                throw; // unreachable: ThrowTerminatingError always throws
            }
        }

        return clients;
    }

    /// <summary>Resolves a context name or engine URI; writes a non-terminating error and returns null on failure.</summary>
    public static DockerContext? ResolveContext(string nameOrHost, Cmdlet cmdlet)
    {
        try
        {
            return DockerContextStore.Resolve(nameOrHost);
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            cmdlet.WriteError(DockerErrors.ToErrorRecord(ex, nameOrHost));
            return null;
        }
    }

    /// <summary>Lists contexts; a failure (e.g. an unreadable config file) is a terminating error.</summary>
    public static IReadOnlyList<DockerContext> GetContexts(string? currentName, Cmdlet cmdlet)
    {
        try
        {
            return DockerContextStore.GetContexts(currentName);
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            cmdlet.ThrowTerminatingError(DockerErrors.ToErrorRecord(ex, DockerConfigFile.FilePath));
            throw;
        }
    }

    /// <summary>Sets the docker CLI's current context; writes an error and returns false on failure.</summary>
    public static bool SetCurrentContext(string? name, Cmdlet cmdlet)
    {
        try
        {
            DockerConfigFile.SetCurrentContext(name);
            return true;
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            cmdlet.WriteError(DockerErrors.ToErrorRecord(ex, DockerConfigFile.FilePath));
            return false;
        }
    }

    /// <summary>Sends a request (Ctrl+C aware); writes a non-terminating error and returns null on failure.</summary>
    public static DockerResult? Invoke(DockerClient client, DockerRequest request, Cmdlet cmdlet, object? target = null)
    {
        try
        {
            return client.Invoke(request, cmdlet);
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            cmdlet.WriteError(DockerErrors.ToErrorRecord(ex, target ?? request.ToString()));
            return null;
        }
    }

    /// <summary>
    /// Streams newline-delimited JSON messages as objects. A failure ends the stream with a non-terminating error.
    /// </summary>
    public static IEnumerable<object?> StreamJson(DockerClient client, DockerRequest request, Cmdlet cmdlet, object? target = null)
    {
        using var enumerator = client.StreamJson(request, cmdlet).GetEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = enumerator.MoveNext();
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                cmdlet.WriteError(DockerErrors.ToErrorRecord(ex, target ?? request.ToString()));
                yield break;
            }

            if (!moved)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
