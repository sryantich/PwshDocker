# Dot-source from BeforeAll/BeforeDiscovery: locates and imports the built module.
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ModuleManifestPath = Join-Path $RepoRoot 'out' 'PwshDocker' 'PwshDocker.psd1'
$SourceRoot = Join-Path $RepoRoot 'src' 'PwshDocker'

if (-not (Test-Path $ModuleManifestPath)) {
    throw "Built module not found at '$ModuleManifestPath'. Run ./build.ps1 -Task Build first."
}

if (-not (Get-Module -Name PwshDocker)) {
    Import-Module $ModuleManifestPath -Force -ErrorAction Stop
}
