@{
    RootModule           = 'PwshDocker.psm1'
    ModuleVersion        = '0.1.0'
    CompatiblePSEditions = @('Core')
    GUID                 = '09e515f5-253b-4920-b0d0-fea069438ea4'
    Author               = 'Sean Tichenor'
    CompanyName          = 'Community'
    Copyright            = '(c) 2026 Sean Tichenor. Released under the MIT License.'
    Description          = 'Native PowerShell module for Docker. Talks directly to the Docker Engine API (named pipe, unix socket, TCP/TLS) with no docker CLI required, and returns rich, pipeline-friendly objects with parallel bulk operations, multi-engine fan-out, live tab completion and -WhatIf support.'
    PowerShellVersion    = '7.4'
    RequiredAssemblies   = @('bin/PwshDocker.Core.dll')
    FormatsToProcess     = @('PwshDocker.Format.ps1xml')
    TypesToProcess       = @('PwshDocker.Types.ps1xml')

    # build.ps1 replaces this with the explicit list of public functions.
    FunctionsToExport    = @()
    CmdletsToExport      = @()
    VariablesToExport    = @()
    AliasesToExport      = @()

    PrivateData          = @{
        PSData = @{
            Tags         = @('Docker', 'Container', 'Containers', 'DockerEngine', 'DevOps', 'PSEdition_Core', 'Windows', 'Linux', 'MacOS')
            LicenseUri   = 'https://github.com/sryantich/PwshDocker/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/sryantich/PwshDocker'
            ReleaseNotes = 'https://github.com/sryantich/PwshDocker/blob/main/docs/ROADMAP.md'
            Prerelease   = 'alpha'
        }
    }
}
