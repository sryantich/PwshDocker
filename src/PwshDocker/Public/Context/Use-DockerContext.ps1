function Use-DockerContext {
    <#
    .SYNOPSIS
        Selects the Docker context (engine) that PwshDocker commands use by default.

    .DESCRIPTION
        Sets the context used when a command's -Context parameter is omitted. By default the choice applies
        only to the current PowerShell session: it does not change the docker CLI or other sessions, and it
        takes precedence over DOCKER_HOST, DOCKER_CONTEXT and the docker CLI's current context.

        Use -Persist to also make it the docker CLI's current context (the equivalent of
        `docker context use`, which updates currentContext in ~/.docker/config.json).

        The name can also be an engine URI (for example tcp://build01:2375) to use an engine without creating
        a context. Engine URIs can't be persisted.

    .PARAMETER Name
        Name of the context to use, or an engine URI such as npipe:////./pipe/docker_engine,
        unix:///var/run/docker.sock or tcp://host:2375.

    .PARAMETER Persist
        Also sets the context as the docker CLI's current context in ~/.docker/config.json, so new
        sessions and the docker CLI use it too.

    .PARAMETER Reset
        Clears the session choice. Commands go back to DOCKER_HOST, DOCKER_CONTEXT, or the docker CLI's
        current context.

    .PARAMETER PassThru
        Returns the context that is now current.

    .EXAMPLE
        Use-DockerContext desktop-linux

        Uses the desktop-linux context for the rest of this PowerShell session.

    .EXAMPLE
        Use-DockerContext prod -Persist

        Makes 'prod' the current context for this session, new sessions and the docker CLI.

    .EXAMPLE
        Use-DockerContext tcp://build01:2375 -PassThru

        Talks to the engine at build01 for the rest of this session without creating a context.

    .EXAMPLE
        Use-DockerContext -Reset

        Forgets the session choice and returns to the docker CLI's current context.

    .INPUTS
        System.String

    .OUTPUTS
        None, or PwshDocker.DockerContext with -PassThru.

    .LINK
        Get-DockerContext

    .LINK
        https://docs.docker.com/reference/cli/docker/context/use/
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Name')]
    [OutputType('PwshDocker.DockerContext')]
    param(
        [Parameter(ParameterSetName = 'Name', Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [ArgumentCompleter([PwshDocker.Completion.ContextCompleter])]
        [ValidateNotNullOrEmpty()]
        [string] $Name,

        [Parameter(ParameterSetName = 'Name')]
        [switch] $Persist,

        [Parameter(ParameterSetName = 'Reset', Mandatory)]
        [switch] $Reset,

        [Parameter()]
        [switch] $PassThru
    )

    process {
        if ($Reset) {
            if ($PSCmdlet.ShouldProcess('PowerShell session', 'Clear the session Docker context')) {
                $script:SessionContext = $null
                if ($PassThru) {
                    Get-DockerContext -Current
                }
            }
            return
        }

        $context = [PwshDocker.DockerCommand]::ResolveContext($Name, $PSCmdlet)
        if ($null -eq $context) {
            return
        }

        if ($Persist) {
            if ($context.Source -eq 'Host') {
                $PSCmdlet.WriteError([PwshDocker.DockerErrors]::Create(
                        "'$Name' is an engine address, not a context, so it can't be saved as the docker CLI's current context. Use it for this session only, or create a context for it.",
                        'DockerContextNotPersistable', 'InvalidArgument', $Name))
                return
            }
            $configPath = [PwshDocker.DockerConfigFile]::FilePath
            if ($PSCmdlet.ShouldProcess($configPath, "Set the docker CLI's current context to '$($context.Name)'")) {
                if (-not [PwshDocker.DockerCommand]::SetCurrentContext($context.Name, $PSCmdlet)) {
                    return
                }
            }
        }

        if ($PSCmdlet.ShouldProcess('PowerShell session', "Use Docker context '$($context.Name)'")) {
            $script:SessionContext = $context.Name
            if ($PassThru) {
                $context.IsCurrent = $true
                $context
            }
        }
    }
}
