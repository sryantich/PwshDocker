BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'TestHelpers.ps1')
}

Describe 'DockerEndpoint.Parse' {
    It 'parses <Address>' -ForEach @(
        @{ Address = 'npipe:////./pipe/docker_engine'; Transport = 'NamedPipe'; Detail = '.|docker_engine' }
        @{ Address = 'npipe://./pipe/dockerDesktopLinuxEngine'; Transport = 'NamedPipe'; Detail = '.|dockerDesktopLinuxEngine' }
        @{ Address = 'npipe:////build01/pipe/docker_engine'; Transport = 'NamedPipe'; Detail = 'build01|docker_engine' }
        @{ Address = 'unix:///var/run/docker.sock'; Transport = 'UnixSocket'; Detail = '/var/run/docker.sock' }
        @{ Address = 'tcp://build01:2375'; Transport = 'Tcp'; Detail = 'build01:2375' }
        @{ Address = 'tcp://build01'; Transport = 'Tcp'; Detail = 'build01:2375' }
        @{ Address = 'https://build01'; Transport = 'Tcp'; Detail = 'build01:2376' }
        @{ Address = 'tcp://[::1]:2376'; Transport = 'Tcp'; Detail = '::1:2376' }
        @{ Address = 'ssh://me@host'; Transport = 'Ssh'; Detail = 'me@host:22' }
    ) {
        $endpoint = [PwshDocker.DockerEndpoint]::Parse($Address)
        $endpoint.Transport | Should -Be $Transport
        $actual = switch ($Transport) {
            'NamedPipe' { "$($endpoint.PipeServer)|$($endpoint.PipeName)" }
            'UnixSocket' { $endpoint.SocketPath }
            'Tcp' { "$($endpoint.HostName):$($endpoint.Port)" }
            'Ssh' { "$($endpoint.SshUser)@$($endpoint.HostName):$($endpoint.Port)" }
        }
        $actual | Should -Be $Detail
        $endpoint.ToString() | Should -Be $Address
    }

    It 'rejects <Address>' -ForEach @(
        @{ Address = 'docker_engine' }
        @{ Address = 'npipe://docker_engine' }
        @{ Address = 'tcp://host:99999' }
        @{ Address = 'tcp://:2375' }
        @{ Address = 'ftp://host' }
    ) {
        { [PwshDocker.DockerEndpoint]::Parse($Address) } | Should -Throw
    }

    It 'uses https and port 2376 for tcp with TLS' {
        $tls = [PwshDocker.DockerTlsSettings]@{ Enabled = $true }
        $endpoint = [PwshDocker.DockerEndpoint]::Parse('tcp://secure-host', $tls)
        $endpoint.UsesTls | Should -BeTrue
        $endpoint.BaseAddress.ToString() | Should -Be 'https://secure-host:2376/'
    }

    It 'uses a placeholder host for pipes and sockets' {
        [PwshDocker.DockerEndpoint]::Parse('unix:///var/run/docker.sock').BaseAddress.Host | Should -Be 'api.moby.localhost'
    }

    It 'recognizes engine URIs vs context names' {
        [PwshDocker.DockerEndpoint]::IsHostUri('tcp://x') | Should -BeTrue
        [PwshDocker.DockerEndpoint]::IsHostUri('desktop-linux') | Should -BeFalse
    }
}

Describe 'DockerContextStore' {
    It 'derives store directory names like the docker CLI (sha256 of the name)' {
        [PwshDocker.DockerContextStore]::GetContextId('desktop-linux') |
            Should -Be 'fe9c6bd7a66301f49ca9b6a70b217107cd1284598bfc254700c989b916da791e'
    }

    It 'uses the platform default engine' {
        $expected = if ($IsWindows) { 'npipe:////./pipe/docker_engine' } else { 'unix:///var/run/docker.sock' }
        [PwshDocker.DockerContextStore]::DefaultHost | Should -Be $expected
    }
}

Describe 'DockerRequest' {
    It 'normalizes method and path and adds the API version prefix' {
        $request = [PwshDocker.DockerRequest]::new('get', 'containers/json', @{ all = $true })
        $request.Method | Should -Be 'GET'
        $request.BuildRelativeUri([version]'1.52') | Should -Be '/v1.52/containers/json?all=true'
    }

    It 'omits the version prefix for unversioned requests' {
        $request = [PwshDocker.DockerRequest]::new('GET', '/_ping')
        $request.Unversioned = $true
        $request.BuildRelativeUri([version]'1.52') | Should -Be '/_ping'
    }

    It 'encodes filters as JSON arrays, accepting scalars and arrays' {
        $request = [PwshDocker.DockerRequest]::new('GET', '/containers/json', @{ filters = @{ status = 'running'; label = @('a=b', 'c') } })
        $request.Query[0].Key | Should -Be 'filters'
        $request.Query[0].Value | Should -Be '{"label":["a=b","c"],"status":["running"]}'
    }

    It 'repeats array parameters, skips nulls and formats dates as Unix timestamps' {
        $since = [datetime]::new(2024, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
        $request = [PwshDocker.DockerRequest]::new('GET', '/images/get', [ordered]@{ names = @('a', 'b'); skip = $null; since = $since })
        $request.BuildRelativeUri($null) | Should -Be '/images/get?names=a&names=b&since=1704067200.000000000'
    }

    It 'escapes query values' {
        $request = [PwshDocker.DockerRequest]::new('POST', '/containers/create', @{ name = 'a b&c' })
        $request.BuildRelativeUri($null) | Should -Be '/containers/create?name=a%20b%26c'
    }
}

Describe 'PSJson' {
    It 'converts engine JSON to PowerShell objects' {
        $json = '{"Id":"abc","Labels":{"com.example":"1"},"Ports":[1,2],"Nested":{"B":true},"N":null,"F":1.5}'
        $object = [PwshDocker.PSJson]::ToPSObject($json)
        $object.Id | Should -Be 'abc'
        $object.Labels | Should -BeOfType ([System.Collections.Generic.Dictionary[string, string]])
        $object.Labels['com.example'] | Should -Be '1'
        $object.Ports | Should -Be @(1, 2)
        $object.Ports[0] | Should -BeOfType [long]
        $object.Nested.B | Should -BeTrue
        $object.N | Should -BeNullOrEmpty
        $object.F | Should -Be 1.5
    }

    It 'keeps label keys case-sensitive' {
        $object = [PwshDocker.PSJson]::ToPSObject('{"Labels":{"a":"lower","A":"upper"}}')
        $object.Labels.Count | Should -Be 2
        $object.Labels['A'] | Should -Be 'upper'
    }

    It 'serializes PowerShell values to engine JSON' {
        $body = [ordered]@{
            Image       = 'alpine'
            Cmd         = @('echo', 'hi')
            Tty         = [switch]::new($true)
            Healthcheck = [pscustomobject]@{ Interval = [timespan]::FromSeconds(5) }
            Labels      = [ordered]@{ a = 'b' }
            Empty       = $null
            Count       = 3
        }
        [PwshDocker.PSJson]::Serialize($body) |
            Should -Be '{"Image":"alpine","Cmd":["echo","hi"],"Tty":true,"Healthcheck":{"Interval":5000000000},"Labels":{"a":"b"},"Empty":null,"Count":3}'
    }
}

Describe 'DockerTime' {
    It 'parses RFC 3339 timestamps with nanoseconds' {
        $parsed = [PwshDocker.DockerTime]::ParseRfc3339('2025-12-12T14:49:51.123456789Z')
        $parsed.ToUniversalTime().Ticks | Should -Be ([datetime]::new(2025, 12, 12, 14, 49, 51, [DateTimeKind]::Utc).AddTicks(1234567).Ticks)
    }

    It 'honors UTC offsets' {
        $parsed = [PwshDocker.DockerTime]::ParseRfc3339('2025-01-01T10:00:00+02:00')
        $parsed.ToUniversalTime().Hour | Should -Be 8
    }

    It 'treats Go zero time as null' {
        [PwshDocker.DockerTime]::ParseRfc3339('0001-01-01T00:00:00Z') | Should -BeNullOrEmpty
        [PwshDocker.DockerTime]::ParseRfc3339('') | Should -BeNullOrEmpty
    }

    It 'formats Unix timestamps with nanoseconds' {
        $time = [datetimeoffset]::new(2024, 1, 1, 0, 0, 0, [timespan]::Zero).AddTicks(5)
        [PwshDocker.DockerTime]::ToUnixTimestamp($time) | Should -Be '1704067200.000000500'
    }
}

Describe 'DockerFormat' {
    It 'formats decimal sizes like docker images: <Bytes> -> <Expected>' -ForEach @(
        @{ Bytes = 0; Expected = '0B' }
        @{ Bytes = 999; Expected = '999B' }
        @{ Bytes = 1000; Expected = '1kB' }
        @{ Bytes = 72840000; Expected = '72.8MB' }
        @{ Bytes = 999999; Expected = '1MB' }
        @{ Bytes = 1234567890; Expected = '1.23GB' }
    ) {
        [PwshDocker.DockerFormat]::HumanSize([long]$Bytes) | Should -Be $Expected
    }

    It 'formats binary sizes like docker stats' {
        [PwshDocker.DockerFormat]::BinarySize(1048576) | Should -Be '1MiB'
        [PwshDocker.DockerFormat]::BinarySize(13149184) | Should -Be '12.54MiB'
    }

    It 'formats durations like the docker CLI: <Seconds>s -> <Expected>' -ForEach @(
        @{ Seconds = 0.5; Expected = 'Less than a second' }
        @{ Seconds = 1; Expected = '1 second' }
        @{ Seconds = 45; Expected = '45 seconds' }
        @{ Seconds = 61; Expected = 'About a minute' }
        @{ Seconds = 600; Expected = '10 minutes' }
        @{ Seconds = 3600; Expected = 'About an hour' }
        @{ Seconds = 10800; Expected = '3 hours' }
        @{ Seconds = 259200; Expected = '3 days' }
        @{ Seconds = 1728000; Expected = '2 weeks' }
        @{ Seconds = 7776000; Expected = '3 months' }
        @{ Seconds = 69120000; Expected = '2 years' }
    ) {
        [PwshDocker.DockerFormat]::HumanDuration([timespan]::FromSeconds($Seconds)) | Should -Be $Expected
    }

    It 'shortens IDs' {
        [PwshDocker.DockerFormat]::ShortId('sha256:0123456789abcdef0123') | Should -Be '0123456789ab'
        [PwshDocker.DockerFormat]::ShortId('0123456789abcdef0123') | Should -Be '0123456789ab'
    }
}

Describe 'Stream readers' {
    It 'reads newline-delimited JSON, including a final unterminated message' {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("{`"a`":1}`r`n`r`n{`"b`":2}`n{`"c`":3}")
        $messages = [PwshDocker.JsonMessageReader]::ReadAll([System.IO.MemoryStream]::new($bytes))
        $messages.Count | Should -Be 3
        $messages[2].GetProperty('c').GetInt32() | Should -Be 3
    }

    It 'extracts errors from stream messages' {
        $message = [System.Text.Json.JsonDocument]::Parse('{"errorDetail":{"message":"pull access denied"},"error":"pull access denied"}').RootElement
        [PwshDocker.JsonMessageReader]::GetError($message) | Should -Be 'pull access denied'
    }

    It 'demultiplexes stdout and stderr frames' {
        $stream = [System.IO.MemoryStream]::new()
        $stream.Write([byte[]](1, 0, 0, 0, 0, 0, 0, 5))
        $stream.Write([System.Text.Encoding]::ASCII.GetBytes('hello'))
        $stream.Write([byte[]](2, 0, 0, 0, 0, 0, 0, 3))
        $stream.Write([System.Text.Encoding]::ASCII.GetBytes('err'))
        $stream.Position = 0
        $frames = [PwshDocker.MultiplexedStreamReader]::ReadAll($stream, $false)
        $frames.Count | Should -Be 2
        $frames[0].Stream | Should -Be 'StdOut'
        [System.Text.Encoding]::ASCII.GetString($frames[0].Payload) | Should -Be 'hello'
        $frames[1].Stream | Should -Be 'StdErr'
    }

    It 'fails on a truncated frame' {
        $stream = [System.IO.MemoryStream]::new([byte[]](1, 0, 0, 0, 0, 0, 0, 9, 65, 66))
        { [PwshDocker.MultiplexedStreamReader]::ReadAll($stream, $false) } | Should -Throw '*closed the stream*'
    }

    It 'passes TTY streams through as stdout' {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes("raw tty`r`n")
        $frames = [PwshDocker.MultiplexedStreamReader]::ReadAll([System.IO.MemoryStream]::new($bytes), $true)
        $frames.Count | Should -Be 1
        $frames[0].Stream | Should -Be 'StdOut'
    }
}

Describe 'DockerErrors' {
    It 'maps HTTP <Status> to <Category>' -ForEach @(
        @{ Status = 400; Message = 'bad'; Category = 'InvalidArgument'; ErrorId = 'DockerBadRequest' }
        @{ Status = 404; Message = 'No such container: x'; Category = 'ObjectNotFound'; ErrorId = 'DockerNotFound' }
        @{ Status = 409; Message = 'Conflict. The container name "/x" is already in use'; Category = 'ResourceExists'; ErrorId = 'DockerConflict' }
        @{ Status = 409; Message = 'container is running: stop the container before removing'; Category = 'InvalidOperation'; ErrorId = 'DockerConflict' }
        @{ Status = 500; Message = 'boom'; Category = 'NotSpecified'; ErrorId = 'DockerServerError' }
    ) {
        $exception = [PwshDocker.DockerApiException]::new($Status, $Message, 'GET', '/x', 'ctx')
        $record = [PwshDocker.DockerErrors]::ToErrorRecord($exception, 'target')
        $record.CategoryInfo.Category | Should -Be $Category
        $record.FullyQualifiedErrorId | Should -Be $ErrorId
        $record.TargetObject | Should -Be 'target'
    }

    It 'unwraps PowerShell method invocation exceptions' {
        $inner = [PwshDocker.DockerApiException]::new(404, 'gone')
        $wrapped = [System.Management.Automation.MethodInvocationException]::new('wrapper', $inner)
        [PwshDocker.DockerErrors]::Unwrap($wrapped) | Should -Be $inner
    }

    It 'adds a recommended action to connection errors' {
        $exception = [PwshDocker.DockerConnectionException]::new('tcp://x:2375', 'ctx', 'refused')
        $record = [PwshDocker.DockerErrors]::ToErrorRecord($exception, $null)
        $record.CategoryInfo.Category | Should -Be 'ConnectionError'
        $record.ErrorDetails.RecommendedAction | Should -BeLike '*tcp://x:2375*'
    }
}
