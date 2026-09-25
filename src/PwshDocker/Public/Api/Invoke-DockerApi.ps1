function Invoke-DockerApi {
    <#
    .SYNOPSIS
        Sends a request to the Docker Engine API and returns the response as objects.

    .DESCRIPTION
        A low-level escape hatch for any Engine API endpoint, including ones without a dedicated PwshDocker
        command yet. Paths are unversioned (for example /containers/json); PwshDocker adds the negotiated API
        version prefix, connection handling and error mapping.

        JSON responses become PowerShell objects, and arrays are written to the pipeline one item at a time.
        Use -Raw for the full response (status code, headers, body), or -Stream for endpoints that return
        newline-delimited JSON (events, image pulls, stats). -Stream writes each message as it arrives, until
        the engine closes the stream or you press Ctrl+C.

        Requests other than GET and HEAD support -WhatIf and -Confirm.

    .PARAMETER Path
        API path, for example /containers/json or /images/alpine/json. Don't include the /v1.xx prefix.

    .PARAMETER Method
        HTTP method.

    .PARAMETER Query
        Query string parameters. Booleans become true/false, arrays repeat the parameter, dates become Unix
        timestamps, and hashtables are JSON-encoded. The values of a 'filters' hashtable can be single strings
        or arrays.

    .PARAMETER Body
        Request body: a hashtable or object (sent as JSON), a JSON string, or a byte array.

    .PARAMETER ContentType
        Content type of the body, when it isn't JSON (for example application/x-tar).

    .PARAMETER Header
        Additional request headers, for example @{ 'X-Registry-Auth' = $token }.

    .PARAMETER Unversioned
        Sends the path without the API version prefix (for example /_ping).

    .PARAMETER TimeoutSeconds
        Maximum time to wait for the engine to start responding.

    .PARAMETER Raw
        Returns a PwshDocker.DockerResult with the status code, headers and body instead of converted objects.

    .PARAMETER Stream
        Reads a newline-delimited JSON stream and writes each message as it arrives.

    .PARAMETER Context
        Context name or engine URI to send the request to. Defaults to the current context.

    .EXAMPLE
        Invoke-DockerApi /containers/json -Query @{ all = $true; filters = @{ status = 'exited' } }

        Lists stopped containers using the raw API.

    .EXAMPLE
        Invoke-DockerApi /_ping -Unversioned

        Pings the engine; returns 'OK'.

    .EXAMPLE
        Invoke-DockerApi /events -Stream -Query @{ filters = @{ type = 'container' } } | Select-Object -First 5

        Waits for the next five container events, then stops.

    .EXAMPLE
        Invoke-DockerApi /containers/web/rename -Method POST -Query @{ name = 'web-old' } -WhatIf

        Shows what would happen without renaming the container.

    .INPUTS
        None

    .OUTPUTS
        System.Management.Automation.PSObject, or PwshDocker.DockerResult with -Raw.

    .LINK
        https://docs.docker.com/reference/api/engine/
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Content')]
    [OutputType('System.Management.Automation.PSObject', 'PwshDocker.DockerResult')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string] $Path,

        [Parameter(Position = 1)]
        [ValidateSet('GET', 'HEAD', 'POST', 'PUT', 'DELETE', 'PATCH')]
        [string] $Method = 'GET',

        [Parameter()]
        [System.Collections.IDictionary] $Query,

        [Parameter()]
        [object] $Body,

        [Parameter()]
        [string] $ContentType,

        [Parameter()]
        [System.Collections.IDictionary] $Header,

        [Parameter()]
        [switch] $Unversioned,

        [Parameter()]
        [ValidateRange(1, 86400)]
        [int] $TimeoutSeconds,

        [Parameter(ParameterSetName = 'Raw')]
        [switch] $Raw,

        [Parameter(ParameterSetName = 'Stream')]
        [switch] $Stream,

        [Parameter()]
        [ArgumentCompleter([PwshDocker.Completion.ContextCompleter])]
        [string] $Context
    )

    $client = Resolve-DockerClient -Context $Context -Cmdlet $PSCmdlet
    $request = [PwshDocker.DockerRequest]::new($Method, $Path, $Query, $Body)
    $request.Unversioned = $Unversioned.IsPresent
    if ($ContentType) {
        $request.ContentType = $ContentType
    }
    if ($TimeoutSeconds) {
        $request.Timeout = [timespan]::FromSeconds($TimeoutSeconds)
    }
    if ($Header) {
        foreach ($entry in $Header.GetEnumerator()) {
            $request.Headers[[string]$entry.Key] = [string]$entry.Value
        }
    }

    if ($Method -notin 'GET', 'HEAD' -and -not $PSCmdlet.ShouldProcess("$($client.ContextName) ($($client.Endpoint.Host))", "$Method $Path")) {
        return
    }

    if ($Stream) {
        foreach ($message in [PwshDocker.DockerCommand]::StreamJson($client, $request, $PSCmdlet, "$Method $Path")) {
            $message
        }
        return
    }

    $result = [PwshDocker.DockerCommand]::Invoke($client, $request, $PSCmdlet, "$Method $Path")
    if ($null -eq $result) {
        return
    }
    if ($Raw) {
        return $result
    }
    $result.GetContent()
}
