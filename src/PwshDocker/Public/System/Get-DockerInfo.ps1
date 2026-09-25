function Get-DockerInfo {
    <#
    .SYNOPSIS
        Gets system-wide information about Docker engines.

    .DESCRIPTION
        Returns engine configuration and state: container and image counts, storage driver, logging driver,
        cgroup setup, CPU and memory, swarm status, registry configuration, security options, warnings and more.
        This is the equivalent of `docker info`.

        The full engine payload is returned, so new engine fields show up without a module update. A curated
        list view is shown by default; use Format-List * or Select-Object to see everything.

    .PARAMETER Context
        Context name(s) or engine URI(s) to query. Defaults to the current context. Accepts pipeline input by
        property name.

    .PARAMETER ThrottleLimit
        Maximum number of engines queried at the same time.

    .EXAMPLE
        Get-DockerInfo

        Shows a summary of the current engine.

    .EXAMPLE
        Get-DockerInfo | Select-Object -ExpandProperty Warnings

        Lists any warnings the engine reports about its configuration.

    .EXAMPLE
        Get-DockerContext | Get-DockerInfo | Format-Table Context, Name, ServerVersion, ContainersRunning, Images, NCPU

        Summarizes every configured engine side by side.

    .INPUTS
        System.String

    .OUTPUTS
        PwshDocker.SystemInfo

    .LINK
        Get-DockerVersion

    .LINK
        https://docs.docker.com/reference/cli/docker/system/info/
    #>
    [CmdletBinding()]
    [OutputType('PwshDocker.SystemInfo')]
    param(
        [Parameter(Position = 0, ValueFromPipelineByPropertyName)]
        [ArgumentCompleter([PwshDocker.Completion.ContextCompleter])]
        [string[]] $Context,

        [Parameter()]
        [ValidateRange(1, 256)]
        [int] $ThrottleLimit = [PwshDocker.DockerParallel]::DefaultThrottleLimit
    )

    begin {
        $contexts = [System.Collections.Generic.List[string]]::new()
    }

    process {
        if ($Context) {
            $contexts.AddRange([string[]]$Context)
        }
    }

    end {
        $operations = foreach ($client in Resolve-DockerClient -Context $contexts -Cmdlet $PSCmdlet) {
            [PwshDocker.DockerOperation]::FromRequest($client, [PwshDocker.DockerRequest]::new('GET', '/info'), $client)
        }
        foreach ($result in [PwshDocker.DockerParallel]::Run($operations, $ThrottleLimit, $PSCmdlet)) {
            [PwshDocker.DockerPSObject]::Create($result.State, $result.Result.Json, 'PwshDocker.SystemInfo')
        }
    }
}
