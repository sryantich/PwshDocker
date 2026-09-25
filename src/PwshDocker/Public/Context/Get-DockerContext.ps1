function Get-DockerContext {
    <#
    .SYNOPSIS
        Gets Docker contexts (named engine endpoints) from the docker context store.

    .DESCRIPTION
        Lists the contexts in the docker CLI's context store (~/.docker/contexts) plus the built-in 'default'
        context, which points at DOCKER_HOST or the platform's default engine. This is the equivalent of
        `docker context ls`, `docker context inspect` and `docker context show`.

        The current context is the one PwshDocker commands use when -Context is omitted. It is marked with
        IsCurrent and an asterisk in the table view. It honors Use-DockerContext for this session, then
        DOCKER_HOST, DOCKER_CONTEXT, and the docker CLI's current context.

    .PARAMETER Name
        Context name(s) to get. Wildcards are supported. Defaults to all contexts.

    .PARAMETER Current
        Gets only the current context (the equivalent of `docker context show`).

    .EXAMPLE
        Get-DockerContext

        Lists all contexts; the current one is marked with an asterisk.

    .EXAMPLE
        Get-DockerContext -Current | Select-Object Name, DockerHost

        Shows which engine PwshDocker commands talk to by default.

    .EXAMPLE
        Get-DockerContext desktop* | Get-DockerVersion

        Gets version information from every context whose name starts with "desktop".

    .INPUTS
        System.String

    .OUTPUTS
        PwshDocker.DockerContext

    .LINK
        Use-DockerContext

    .LINK
        https://docs.docker.com/reference/cli/docker/context/ls/
    #>
    [CmdletBinding(DefaultParameterSetName = 'Name')]
    [OutputType('PwshDocker.DockerContext')]
    param(
        [Parameter(ParameterSetName = 'Name', Position = 0, ValueFromPipeline)]
        [SupportsWildcards()]
        [ArgumentCompleter([PwshDocker.Completion.ContextCompleter])]
        [string[]] $Name = '*',

        [Parameter(ParameterSetName = 'Current', Mandatory)]
        [switch] $Current
    )

    begin {
        $currentName = Get-DockerCurrentContextName
        $contexts = @([PwshDocker.DockerCommand]::GetContexts($currentName, $PSCmdlet))
        $emitted = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    }

    process {
        if ($Current) {
            $match = $contexts | Where-Object IsCurrent
            if (-not $match) {
                # The session context may be an engine URI rather than a stored context.
                $match = [PwshDocker.DockerCommand]::ResolveContext($currentName, $PSCmdlet)
                if ($null -eq $match) {
                    return
                }
                $match.IsCurrent = $true
            }
            return $match
        }

        foreach ($pattern in $Name) {
            $found = $false
            foreach ($context in $contexts) {
                if ($context.Name -like $pattern) {
                    $found = $true
                    if ($emitted.Add($context.Name)) {
                        $context
                    }
                }
            }
            if (-not $found -and -not [WildcardPattern]::ContainsWildcardCharacters($pattern)) {
                $PSCmdlet.WriteError([PwshDocker.DockerErrors]::Create(
                        "Docker context '$pattern' was not found.", 'DockerContextNotFound', 'ObjectNotFound', $pattern))
            }
        }
    }
}
