BeforeDiscovery {
    . (Join-Path $PSScriptRoot '..' 'TestHelpers.ps1')
    $engineAvailable = Test-DockerEngine
}

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'TestHelpers.ps1')
    $runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $current = Get-DockerContext -Current
}

Describe 'Engine commands' -Tag 'Integration' -Skip:(-not $engineAvailable) {
    It 'reaches the engine' {
        Test-DockerEngine | Should -BeTrue
        $detail = Test-DockerEngine -Detailed
        $detail.Reachable | Should -BeTrue
        $detail.LatencyMs | Should -BeGreaterThan 0
        $detail.NegotiatedApiVersion | Should -BeLessOrEqual ([PwshDocker.PwshDockerInfo]::MaxApiVersion)
    }

    It 'gets version information and negotiates an API version' {
        $version = Get-DockerVersion
        $version.Context | Should -Be $current.Name
        $version.Version | Should -Not -BeNullOrEmpty
        $expected = if ($version.ApiVersion -lt [PwshDocker.PwshDockerInfo]::MaxApiVersion) { $version.ApiVersion } else { [PwshDocker.PwshDockerInfo]::MaxApiVersion }
        $version.NegotiatedApiVersion | Should -Be $expected
        ($version | Out-String) | Should -Match 'NegotiatedApiVersion'
    }

    It 'queries several engines in parallel, tagging each result with its context' {
        $results = Get-DockerVersion -Context $current.Name, $current.DockerHost
        $results.Count | Should -Be 2
        $results.Context | Sort-Object | Should -Be (@($current.Name, $current.DockerHost) | Sort-Object)
    }

    It 'accepts contexts from the pipeline' {
        (Get-DockerContext -Current | Get-DockerVersion).Context | Should -Be $current.Name
    }

    It 'gets system information' {
        $info = Get-DockerInfo
        $info.PSObject.TypeNames | Should -Contain 'PwshDocker.SystemInfo'
        $info.Context | Should -Be $current.Name
        $info.ServerVersion | Should -Be (Get-DockerVersion).Version
        ($info | Out-String) | Should -Match 'ServerVersion'
    }
}

Describe 'Invoke-DockerApi' -Tag 'Integration' -Skip:(-not $engineAvailable) {
    It 'sends unversioned requests' {
        Invoke-DockerApi /_ping -Unversioned | Should -Be 'OK'
    }

    It 'converts JSON arrays to objects' {
        { Invoke-DockerApi /containers/json -Query @{ all = $true; filters = @{ status = 'exited' } } } | Should -Not -Throw
    }

    It 'returns the full response with -Raw' {
        $result = Invoke-DockerApi /version -Raw
        $result.StatusCode | Should -Be 200
        $result.Headers['Api-Version'] | Should -Not -BeNullOrEmpty
        $result.Json.GetProperty('Version').GetString() | Should -Not -BeNullOrEmpty
    }

    It 'maps engine errors to error records' {
        Invoke-DockerApi "/containers/pwshdocker-missing-$runId/json" -ErrorVariable failures -ErrorAction SilentlyContinue
        $failures.Count | Should -Be 1
        $failures[0].FullyQualifiedErrorId | Should -Be 'DockerNotFound,Invoke-DockerApi'
        $failures[0].CategoryInfo.Category | Should -Be 'ObjectNotFound'
        $failures[0].Exception.StatusCode | Should -Be 404
    }

    It 'does not send state-changing requests with -WhatIf' {
        $name = "pwshdocker-whatif-$runId"
        Invoke-DockerApi /networks/create -Method POST -Body @{ Name = $name } -WhatIf
        Invoke-DockerApi "/networks/$name" -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
    }

    It 'streams events and stops cleanly when the consumer has enough' {
        $name = "pwshdocker-stream-$runId"
        $since = (Get-Date).AddSeconds(-2)
        $created = Invoke-DockerApi /networks/create -Method POST -Body @{ Name = $name; Labels = @{ 'pwshdocker.test' = $runId } }
        try {
            $query = @{ since = $since; filters = @{ type = 'network'; event = 'create'; network = $created.Id } }
            $elapsed = Measure-Command {
                $received = Invoke-DockerApi /events -Stream -Query $query | Select-Object -First 1
            }
            $received.Actor.Attributes.name | Should -Be $name
            $elapsed.TotalSeconds | Should -BeLessThan 10
        } finally {
            Invoke-DockerApi "/networks/$($created.Id)" -Method DELETE -Confirm:$false
        }
    }
}
