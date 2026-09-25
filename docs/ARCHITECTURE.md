# PwshDocker Architecture

## Layers

```
 PowerShell commands            src/PwshDocker/Public/**/*.ps1
 (verbs, parameters, pipeline,  - one function per file, comment-based help
  ShouldProcess, formatting)    - private helpers in src/PwshDocker/Private
            |
            v
 PwshDocker.Core.dll (C#)       src/PwshDocker.Core
  Connection  DockerContextStore -> DockerEndpoint -> DockerClient (cached per endpoint)
  Transport   npipe | unix socket | tcp | tcp+TLS  (SocketsHttpHandler.ConnectCallback)
  Protocol    API version negotiation, request building, error mapping, HTTP upgrade (hijack)
  Streams     NDJSON messages (pull/push/build/events), multiplexed stdout/stderr frames, log line splitting
  Execution   Ctrl+C-aware waits, bounded parallel runner, progress events
  Model       curated types (Container, Image, ...) + dynamic JSON -> PSObject conversion
            |
            v
 Docker Engine API (HTTP/1.1, versioned paths /v1.xx/...)
```

**Why C# for the core and PowerShell for commands?** Transport code (thread-pool callbacks, stream demuxing,
upgraded connections, cancellation) is fragile and slow in script. Commands are where the surface area grows
(200+ CLI subcommands), and PowerShell functions are the easiest for admins to read, review, and extend. This is the
same split `dbatools` uses.

## Connecting to an engine

Every command accepts `-Context <string[]>`. Each value can be:

- a **context name** from the Docker context store (`desktop-linux`, `prod`, ...), or
- an **engine URI** (`npipe:////./pipe/docker_engine`, `unix:///var/run/docker.sock`, `tcp://host:2376`), or
- a **context object** (it converts to its name).

Passing several values runs the command against every engine in parallel (fan-out).

When `-Context` is omitted, resolution follows the Docker CLI's precedence, with one PowerShell-specific layer on top:

1. Session context set with `Use-DockerContext` (per PowerShell runspace, does not touch the CLI).
2. `DOCKER_HOST` environment variable (implies the `default` context).
3. `DOCKER_CONTEXT` environment variable.
4. `currentContext` in `$DOCKER_CONFIG/config.json` (default `~/.docker/config.json`).
5. The `default` context: `npipe:////./pipe/docker_engine` on Windows, `unix:///var/run/docker.sock` elsewhere.

Context metadata is read from `~/.docker/contexts/meta/<sha256(name)>/meta.json` and TLS material from
`~/.docker/contexts/tls/<sha256(name)>/docker/{ca,cert,key}.pem`. That's the same store the CLI uses, so both tools
always agree.

`DockerClient` instances are cached per endpoint for the life of the process. Each owns one `HttpClient` with a pooled
`SocketsHttpHandler`, so concurrent requests reuse or open connections (named-pipe instances or sockets) as needed.
The API version is negotiated once per client via `GET /_ping` (`min(client max, server version)`), or pinned via
`DOCKER_API_VERSION`.

## Object model

- **Curated types** for primary resources (`PwshDocker.Container`, `PwshDocker.Image`, ...) with PowerShell-friendly
  properties. Dates are `DateTime`, sizes are bytes, labels are case-sensitive dictionaries, ports are objects, and
  states are enums. Table views mimic the CLI, with human-readable sizes and relative times.
- **Summary vs detail.** List endpoints are cheap; inspect endpoints are rich. `Get-DockerContainer` returns summary
  objects; `-Detailed` returns `PwshDocker.ContainerDetail` (a subclass), fetched in parallel.
- **Dynamic payloads.** Deep, fast-evolving structures (`HostConfig`, `NetworkSettings`, `Info`) are converted from
  JSON to `PSCustomObject`s, so new engine fields appear without a module update.
- **Engine affinity.** Every object carries the `Context` it came from. Piping objects to another command targets the
  engine they came from, so `Get-DockerContainer -Context a,b | Restart-DockerContainer` does the right thing.

## Concurrency and cancellation

- The core is thread-safe; all network I/O is async.
- PowerShell-facing waits poll `$PSCmdlet.Stopping`, so **Ctrl+C always works**, even while waiting on a slow engine
  or a `-Follow` stream. On stop, in-flight requests are cancelled and connections closed.
- Bulk commands collect targets (running `ShouldProcess` per item), then execute them with bounded concurrency
  (`-ThrottleLimit`). Results are emitted on the pipeline thread as each operation completes, and failures become
  per-item, non-terminating errors.
- Script blocks never run on thread-pool threads. Parallel work is C# tasks, and emission happens on the pipeline
  thread. This is faster and safer than `ForEach-Object -Parallel` runspaces, but the module is still runspace-safe
  if users parallelize it themselves.

## Errors

Engine errors (`{"message": "..."}`) become `PwshDocker.DockerApiException` with the HTTP status, and are surfaced as
`ErrorRecord`s:

| HTTP | ErrorCategory |
|---|---|
| 400 | InvalidArgument |
| 401 / 403 | PermissionDenied |
| 404 | ObjectNotFound |
| 409 | ResourceExists / InvalidOperation |
| 500 | NotSpecified |
| 503 | ResourceUnavailable |
| connection failure | ConnectionError, with the endpoint and a "is the engine running?" hint |

Errors in streamed payloads (e.g. a failed pull reported mid-stream) are detected and raised the same way.

## Command conventions

- Approved verbs only; nouns are singular and prefixed with `Docker` (`Get-DockerContainer`).
- Target selection: `-Name` (positional, wildcards, names or IDs), `-Id`, or `-InputObject` from the pipeline.
- State-changing commands support `-WhatIf`/`-Confirm`; destructive bulk ones (`Clear-*`) use `ConfirmImpact = 'High'`.
- Action commands are quiet by default and emit updated objects with `-PassThru`.
- `-Force` keeps Docker's meaning (e.g. remove a running container).
- `-Filter <hashtable>` passes raw engine filters on list commands.
- `-ThrottleLimit` on bulk commands (default 8).

## Parity research method

1. The versioned Engine API reference (swagger) for each endpoint.
2. `docker/cli`, `docker/compose`, and `moby/moby` sources for client-side behavior (stats math, `cp` semantics,
   run-flag mapping, context store, compose labels).
3. The PwshDocker API tracer: a pass-through proxy that records every request the real `docker` CLI makes, for when
   docs and source reading aren't enough.

## Testing

- **Quality tests**: manifest validity, approved verbs, exported commands, comment-based help completeness
  (synopsis, description, all parameters, examples), PSScriptAnalyzer.
- **Unit tests**: pure logic in the core (endpoint parsing, context hashing, stream demuxing, timestamp parsing,
  stats math, size formatting).
- **Integration tests** (`-Tag Integration`): run against a live engine; every resource they create is labeled
  `pwshdocker.test=<run id>` and cleaned up afterwards.

## Build

`./build.ps1` bootstraps pinned Pester/PSScriptAnalyzer into `./.build` (nothing installed system-wide), builds the
core with `dotnet build`, assembles the module into `./out/PwshDocker` (public/private functions merged into one
`.psm1` for fast import, explicit `FunctionsToExport`), then runs the analyzer and tests in a clean `pwsh` process so
the compiled assembly never locks your session.
