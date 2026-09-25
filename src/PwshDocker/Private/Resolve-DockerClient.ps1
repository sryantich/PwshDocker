function Resolve-DockerClient {
    <#
    .SYNOPSIS
        Resolves -Context values (context names or engine URIs) to cached DockerClient instances for the calling
        command. With no values, returns the client for the current context. Unknown contexts are terminating
        errors of the calling command.
    #>
    [CmdletBinding()]
    [OutputType('PwshDocker.DockerClient')]
    param(
        [Parameter()]
        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]] $Context,

        # The calling command's $PSCmdlet, so errors are attributed to it.
        [Parameter(Mandatory)]
        [System.Management.Automation.PSCmdlet] $Cmdlet
    )

    $currentName = if ($Context) { $null } else { Get-DockerCurrentContextName }
    [PwshDocker.DockerCommand]::ResolveClients($Context, $currentName, $Cmdlet)
}
