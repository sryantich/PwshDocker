BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'TestHelpers.ps1')

    $savedEnvironment = @{
        DOCKER_CONFIG  = $env:DOCKER_CONFIG
        DOCKER_HOST    = $env:DOCKER_HOST
        DOCKER_CONTEXT = $env:DOCKER_CONTEXT
    }

    # An isolated docker config directory with two stored contexts; 'beta' is the CLI's current context.
    $configDir = Join-Path ([System.IO.Path]::GetTempPath()) "pwshdocker-test-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $configDir

    function New-TestContext([string] $Name, [string] $Endpoint) {
        $id = [PwshDocker.DockerContextStore]::GetContextId($Name)
        $directory = Join-Path $configDir 'contexts' 'meta' $id
        $null = New-Item -ItemType Directory -Path $directory -Force
        @{
            Name      = $Name
            Metadata  = @{ Description = "Test context $Name" }
            Endpoints = @{ docker = @{ Host = $Endpoint; SkipTLSVerify = $false } }
        } | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $directory 'meta.json')
    }

    New-TestContext -Name 'alpha' -Endpoint 'tcp://alpha.invalid:2375'
    New-TestContext -Name 'beta' -Endpoint 'unix:///tmp/pwshdocker-beta.sock'
    $configFile = Join-Path $configDir 'config.json'
    '{ "currentContext": "beta", "credsStore": "pwshdocker-test", "auths": {} }' | Set-Content -Path $configFile

    function Reset-TestEnvironment {
        $env:DOCKER_CONFIG = $configDir
        $env:DOCKER_HOST = $null
        $env:DOCKER_CONTEXT = $null
        Use-DockerContext -Reset
    }
}

AfterAll {
    Use-DockerContext -Reset
    foreach ($name in $savedEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Remove-Item -Path $configDir -Recurse -Force -ErrorAction Ignore
}

Describe 'Get-DockerContext' {
    BeforeEach { Reset-TestEnvironment }

    It 'lists the default context and the stored contexts' {
        $names = (Get-DockerContext).Name
        $names | Should -Be @('default', 'alpha', 'beta')
    }

    It "marks the docker CLI's current context as current" {
        (Get-DockerContext | Where-Object IsCurrent).Name | Should -Be 'beta'
        (Get-DockerContext -Current).Name | Should -Be 'beta'
    }

    It 'reads descriptions and endpoints from the store' {
        $alpha = Get-DockerContext -Name alpha
        $alpha.Description | Should -Be 'Test context alpha'
        $alpha.DockerHost | Should -Be 'tcp://alpha.invalid:2375'
        $alpha.Source | Should -Be 'Store'
        $alpha.Context | Should -Be 'alpha'
    }

    It 'supports wildcards' {
        (Get-DockerContext -Name 'al*').Name | Should -Be 'alpha'
    }

    It 'writes a not-found error for unknown names' {
        $result = Get-DockerContext -Name 'nope' -ErrorVariable failures -ErrorAction SilentlyContinue
        $result | Should -BeNullOrEmpty
        $failures[0].FullyQualifiedErrorId | Should -BeLike 'DockerContextNotFound*'
    }

    It 'prefers DOCKER_CONTEXT over config.json' {
        $env:DOCKER_CONTEXT = 'alpha'
        (Get-DockerContext -Current).Name | Should -Be 'alpha'
    }

    It 'uses DOCKER_HOST for the default context and makes it current' {
        $env:DOCKER_HOST = 'tcp://from-env:2375'
        $current = Get-DockerContext -Current
        $current.Name | Should -Be 'default'
        $current.DockerHost | Should -Be 'tcp://from-env:2375'
        $current.Source | Should -Be 'Environment'
    }
}

Describe 'Use-DockerContext' {
    BeforeEach { Reset-TestEnvironment }

    It 'changes the context for this session only' {
        Use-DockerContext alpha
        (Get-DockerContext -Current).Name | Should -Be 'alpha'
        (Get-Content $configFile -Raw | ConvertFrom-Json).currentContext | Should -Be 'beta'
    }

    It 'takes precedence over DOCKER_HOST' {
        $env:DOCKER_HOST = 'tcp://from-env:2375'
        Use-DockerContext alpha
        (Get-DockerContext -Current).Name | Should -Be 'alpha'
    }

    It 'returns the context with -PassThru' {
        $context = Use-DockerContext alpha -PassThru
        $context.Name | Should -Be 'alpha'
        $context.IsCurrent | Should -BeTrue
    }

    It 'persists to config.json with -Persist, keeping other settings' {
        Use-DockerContext alpha -Persist
        $config = Get-Content $configFile -Raw | ConvertFrom-Json
        $config.currentContext | Should -Be 'alpha'
        $config.credsStore | Should -Be 'pwshdocker-test'
        Use-DockerContext beta -Persist
    }

    It 'clears currentContext when persisting the default context' {
        Use-DockerContext default -Persist
        (Get-Content $configFile -Raw | ConvertFrom-Json).PSObject.Properties.Name | Should -Not -Contain 'currentContext'
        Use-DockerContext beta -Persist
    }

    It 'accepts an engine URI for the session' {
        Use-DockerContext 'tcp://adhoc:2375'
        $current = Get-DockerContext -Current
        $current.Name | Should -Be 'tcp://adhoc:2375'
        $current.Source | Should -Be 'Host'
    }

    It 'refuses to persist an engine URI' {
        Use-DockerContext 'tcp://adhoc:2375' -Persist -ErrorVariable failures -ErrorAction SilentlyContinue
        $failures[0].FullyQualifiedErrorId | Should -BeLike 'DockerContextNotPersistable*'
    }

    It 'rejects unknown contexts' {
        Use-DockerContext nope -ErrorVariable failures -ErrorAction SilentlyContinue
        $failures[0].FullyQualifiedErrorId | Should -BeLike 'DockerContextNotFound*'
        (Get-DockerContext -Current).Name | Should -Be 'beta'
    }

    It 'honors -WhatIf' {
        Use-DockerContext alpha -Persist -WhatIf
        (Get-DockerContext -Current).Name | Should -Be 'beta'
        (Get-Content $configFile -Raw | ConvertFrom-Json).currentContext | Should -Be 'beta'
    }

    It 'returns to the CLI context with -Reset' {
        Use-DockerContext alpha
        Use-DockerContext -Reset
        (Get-DockerContext -Current).Name | Should -Be 'beta'
    }
}

Describe 'Test-DockerEngine (unreachable engines)' {
    BeforeEach { Reset-TestEnvironment }

    It 'returns $false quickly for an engine that refuses connections' {
        $elapsed = Measure-Command { $result = Test-DockerEngine -Context 'tcp://127.0.0.1:1' -TimeoutSeconds 5 }
        $result | Should -BeFalse
        $elapsed.TotalSeconds | Should -BeLessThan 5
    }

    It 'explains the failure with -Detailed' {
        $result = Test-DockerEngine -Context 'tcp://127.0.0.1:1' -Detailed -TimeoutSeconds 5
        $result.Reachable | Should -BeFalse
        $result.Error | Should -Not -BeNullOrEmpty
        $result.Endpoint | Should -Be 'tcp://127.0.0.1:1'
    }

    It 'tests several engines in parallel and keeps the input order' {
        $results = Test-DockerEngine -Context 'tcp://127.0.0.1:1', 'tcp://127.0.0.1:2' -Detailed -TimeoutSeconds 5
        $results.Context | Should -Be @('tcp://127.0.0.1:1', 'tcp://127.0.0.1:2')
    }

    It 'reports unknown contexts as unreachable' {
        $result = Test-DockerEngine -Context 'no-such-context' -Detailed
        $result.Reachable | Should -BeFalse
        $result.Error | Should -BeLike '*not found*'
    }
}
