function Get-DockerVersion {
    <#
    .SYNOPSIS
        Gets version information for Docker engines and PwshDocker.

    .DESCRIPTION
        Returns the engine version, the API versions it supports, the API version PwshDocker negotiated with it,
        and platform details such as OS, architecture, kernel and component versions. This is the equivalent of
        `docker version`.

        Pass several contexts, or pipe contexts in, to query several engines in parallel.

    .PARAMETER Context
        Context name(s) or engine URI(s) to query. Defaults to the current context. Accepts pipeline input by
        property name, so you can pipe contexts or any other PwshDocker object.

    .PARAMETER ThrottleLimit
        Maximum number of engines queried at the same time.

    .EXAMPLE
        Get-DockerVersion

        Shows version information for the current engine.

    .EXAMPLE
        Get-DockerContext | Get-DockerVersion | Format-Table Context, Version, ApiVersion, NegotiatedApiVersion, Os

        Queries every configured engine in parallel and summarizes the results.

    .EXAMPLE
        (Get-DockerVersion).NegotiatedApiVersion -ge [version]'1.45'

        Checks whether the engine supports API version 1.45 features.

    .INPUTS
        System.String

    .OUTPUTS
        PwshDocker.DockerVersionInfo

    .LINK
        Get-DockerInfo

    .LINK
        https://docs.docker.com/reference/cli/docker/version/
    #>
    [CmdletBinding()]
    [OutputType('PwshDocker.DockerVersionInfo')]
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
            [PwshDocker.DockerOperation]::FromRequest($client, [PwshDocker.DockerRequest]::new('GET', '/version'), $client)
        }
        foreach ($result in [PwshDocker.DockerParallel]::Run($operations, $ThrottleLimit, $PSCmdlet)) {
            [PwshDocker.DockerVersionInfo]::FromJson($result.State, $result.Result.Json)
        }
    }
}
