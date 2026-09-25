BeforeDiscovery {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $commandCases = @(
        Get-Command -Module PwshDocker -CommandType Function |
            Sort-Object Name |
            ForEach-Object { @{ Name = $_.Name } }
    )
}

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
}

Describe 'PwshDocker module manifest' {
    It 'is a valid manifest' {
        $manifest = Test-ModuleManifest -Path $ModuleManifestPath -ErrorAction Stop
        $manifest.Name | Should -Be 'PwshDocker'
        $manifest.Guid | Should -Be '09e515f5-253b-4920-b0d0-fea069438ea4'
    }

    It 'targets PowerShell 7.4+ Core edition only' {
        $data = Import-PowerShellDataFile -Path $ModuleManifestPath
        [version]$data.PowerShellVersion | Should -BeGreaterOrEqual ([version]'7.4')
        $data.CompatiblePSEditions | Should -Be @('Core')
    }

    It 'exports exactly the functions in src/PwshDocker/Public' {
        $expected = @(
            Get-ChildItem (Join-Path $SourceRoot 'Public') -Filter '*.ps1' -Recurse -ErrorAction Ignore |
                ForEach-Object BaseName |
                Sort-Object
        )
        $actual = @((Get-Module PwshDocker).ExportedFunctions.Keys | Sort-Object)
        $actual | Should -Be $expected
    }

    It 'lists exported functions explicitly (no wildcards)' {
        $data = Import-PowerShellDataFile -Path $ModuleManifestPath
        @($data.FunctionsToExport) | Should -Not -Contain '*'
    }

    It 'exports no cmdlets, variables or aliases' {
        $module = Get-Module PwshDocker
        $module.ExportedCmdlets.Count | Should -Be 0
        $module.ExportedVariables.Count | Should -Be 0
        $module.ExportedAliases.Count | Should -Be 0
    }

    It 'loads the core assembly' {
        [PwshDocker.PwshDockerInfo]::MaxApiVersion | Should -BeOfType [version]
    }
}

Describe 'Command <Name>' -ForEach $commandCases {
    BeforeAll {
        $command = Get-Command -Name $Name
        $help = Get-Help -Name $Name -Full
        $commonParameters = [System.Management.Automation.Cmdlet]::CommonParameters +
            [System.Management.Automation.Cmdlet]::OptionalCommonParameters
    }

    It 'uses an approved verb' {
        (Get-Verb).Verb | Should -Contain $command.Verb
    }

    It 'uses a Docker-prefixed singular noun' {
        $command.Noun | Should -BeLike 'Docker*'
    }

    It 'has a synopsis' {
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -BeLike "$Name *"
    }

    It 'has a description' {
        ($help.Description.Text -join '').Trim() | Should -Not -BeNullOrEmpty
    }

    It 'has at least one example with an explanation' {
        $examples = @($help.Examples.Example)
        $examples.Count | Should -BeGreaterThan 0
        foreach ($example in $examples) {
            $example.Code | Should -Not -BeNullOrEmpty
            ($example.Remarks.Text -join '').Trim() | Should -Not -BeNullOrEmpty
        }
    }

    It 'documents every parameter' {
        foreach ($parameterName in $command.Parameters.Keys | Where-Object { $_ -notin $commonParameters }) {
            $parameterHelp = $help.Parameters.Parameter | Where-Object Name -EQ $parameterName
            ($parameterHelp.Description.Text -join '').Trim() |
                Should -Not -BeNullOrEmpty -Because "parameter -$parameterName needs help text"
        }
    }

    It 'declares an output type' {
        $command.OutputType.Count | Should -BeGreaterThan 0 -Because 'every command should declare [OutputType()]'
    }
}
