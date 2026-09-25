#Requires -Version 7.4
Set-StrictMode -Version 1.0

#region DevLoader
# When running from source, dot-source every function file. build.ps1 replaces this region with the
# merged contents of Private/*.ps1 and Public/*.ps1 so the shipped module imports from a single file.
foreach ($folder in 'Private', 'Public') {
    $folderPath = Join-Path -Path $PSScriptRoot -ChildPath $folder
    if (Test-Path -LiteralPath $folderPath) {
        foreach ($file in Get-ChildItem -LiteralPath $folderPath -Filter '*.ps1' -Recurse | Sort-Object -Property FullName) {
            . $file.FullName
        }
    }
}
#endregion DevLoader

# Per-runspace module state.
# SessionContext: context name or engine URI selected with Use-DockerContext (overrides env/config for this session).
$script:SessionContext = $null

if (Get-Command -Name 'Initialize-PwshDockerModule' -CommandType Function -ErrorAction Ignore) {
    Initialize-PwshDockerModule
}
