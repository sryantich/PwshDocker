function Test-DockerEngine {
    <#
    .SYNOPSIS
        Tests whether Docker engines are reachable.

    .DESCRIPTION
        Pings each engine (GET /_ping) and returns $true when it answers. With -Detailed, returns an object with
        the endpoint, round-trip latency, API versions, OS type, and the error message for unreachable engines.

        Several engines are tested in parallel, and results are returned in the order the contexts were given.
        A missing local named pipe or socket fails immediately rather than waiting for a timeout.

    .PARAMETER Context
        Context name(s) or engine URI(s) to test. Defaults to the current context. Accepts pipeline input by
        property name, so you can pipe the output of Get-DockerContext.

    .PARAMETER Detailed
        Returns a PwshDocker.DockerEngineTestResult object for each engine instead of $true/$false.

    .PARAMETER TimeoutSeconds
        How long to wait for each engine to answer.

    .EXAMPLE
        Test-DockerEngine

        Returns $true if the current engine is reachable.

    .EXAMPLE
        if (-not (Test-DockerEngine)) { throw 'Start Docker first.' }

        Guards a script against a stopped engine.

    .EXAMPLE
        Get-DockerContext | Test-DockerEngine -Detailed

        Checks every configured engine in parallel and shows latency, API version and errors.

    .INPUTS
        System.String

    .OUTPUTS
        System.Boolean, or PwshDocker.DockerEngineTestResult with -Detailed.

    .LINK
        Get-DockerVersion
    #>
    [CmdletBinding()]
    [OutputType('System.Boolean', 'PwshDocker.DockerEngineTestResult')]
    param(
        [Parameter(Position = 0, ValueFromPipelineByPropertyName)]
        [ArgumentCompleter([PwshDocker.Completion.ContextCompleter])]
        [string[]] $Context,

        [Parameter()]
        [switch] $Detailed,

        [Parameter()]
        [ValidateRange(1, 600)]
        [int] $TimeoutSeconds = 10
    )

    begin {
        $contexts = [System.Collections.Generic.List[string]]::new()
    }

    process {
        foreach ($name in $Context) {
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not $contexts.Contains($name)) {
                $contexts.Add($name)
            }
        }
    }

    end {
        if ($contexts.Count -eq 0) {
            $contexts.Add((Get-DockerCurrentContextName))
        }

        $timeout = [timespan]::FromSeconds($TimeoutSeconds)
        $operations = foreach ($name in $contexts) {
            [PwshDocker.DockerEngineTestResult]::CreateOperation($name, $timeout)
        }

        $results = @{}
        foreach ($result in [PwshDocker.DockerParallel]::Run($operations, 32, $PSCmdlet)) {
            $results[$result.State] = $result.Result
        }

        foreach ($name in $contexts) {
            $result = $results[$name]
            if ($null -eq $result) {
                continue
            }
            if ($Detailed) {
                $result
            } else {
                $result.Reachable
            }
        }
    }
}
