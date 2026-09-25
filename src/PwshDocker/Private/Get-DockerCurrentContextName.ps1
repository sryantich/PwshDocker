function Get-DockerCurrentContextName {
    <#
    .SYNOPSIS
        Returns the context used when -Context is omitted: the session context (Use-DockerContext) or the
        docker CLI's current context (DOCKER_HOST, DOCKER_CONTEXT, config.json, default).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    if ($script:SessionContext) {
        return $script:SessionContext
    }
    [PwshDocker.DockerContextStore]::GetCurrentContextName()
}
