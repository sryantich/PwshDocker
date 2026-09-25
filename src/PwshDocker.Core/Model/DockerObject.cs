namespace PwshDocker;

/// <summary>Base type for objects that belong to an engine.</summary>
public abstract class DockerObject
{
    protected DockerObject(DockerClient? client)
    {
        Client = client;
        Context = client?.ContextName;
    }

    /// <summary>Context name (or engine URI) of the engine this object came from.</summary>
    public string? Context { get; }

    internal DockerClient? Client { get; }

    /// <summary>The client for the engine this object came from.</summary>
    public DockerClient? GetClient() => Client;
}
