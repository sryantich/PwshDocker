using System.Reflection;

namespace PwshDocker;

/// <summary>Static information about this build of PwshDocker.</summary>
public static class PwshDockerInfo
{
    /// <summary>Highest Docker Engine API version this build has been validated against.</summary>
    public static readonly Version MaxApiVersion = new(1, 52);

    /// <summary>Lowest Docker Engine API version this build supports (Docker Engine 20.10).</summary>
    public static readonly Version MinApiVersion = new(1, 41);

    /// <summary>Version of the PwshDocker core assembly (matches the module version).</summary>
    public static Version CoreVersion { get; } =
        typeof(PwshDockerInfo).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Informational version, including any prerelease suffix.</summary>
    public static string InformationalVersion { get; } =
        typeof(PwshDockerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? CoreVersion.ToString();

    /// <summary>User-Agent sent to the engine.</summary>
    public static string UserAgent => $"PwshDocker/{CoreVersion.ToString(3)}";
}
