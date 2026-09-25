# PwshDocker

[![CI](https://github.com/sryantich/PwshDocker/actions/workflows/ci.yml/badge.svg)](https://github.com/sryantich/PwshDocker/actions/workflows/ci.yml)

**A native PowerShell module for Docker.** PwshDocker talks directly to the Docker Engine API over a named pipe,
unix socket or TCP/TLS. It doesn't need the `docker` CLI and never parses its text output. Commands return
rich objects that work with the pipeline, `-WhatIf`/`-Confirm`, tab completion and bulk parallel operations.

> **Status: early alpha.** The foundation is being built; see the [roadmap](docs/ROADMAP.md) for what's done and
> what's next. Names and behavior may change until 1.0.

## Why

Docker never shipped a PowerShell module. Microsoft's `Docker-PowerShell` was archived in 2018, and the modules on
the PowerShell Gallery are stale CLI wrappers or tab-completion helpers. PwshDocker aims to be the module a Windows
or DevOps admin would expect:

- **Objects, not text.** Dates are `DateTime`, sizes are bytes, labels are dictionaries, ports are objects, and table
  views look like the CLI's.
- **Fast and parallel.** No process per call. Bulk commands (`Get-DockerContainer | Stop-DockerContainer`) run
  concurrently with `-ThrottleLimit`, stream results as they finish, and Ctrl+C cancels cleanly.
- **Fleet-aware.** `-Context prod1, prod2, prod3` fans a command out to several engines. Every object remembers its
  engine, so the pipeline always targets the right host.
- **PowerShell-native.** Approved verbs, pipeline binding, `-WhatIf`/`-Confirm`, `-PassThru`, and full help with
  examples for every command.
- **Compatible with the CLI.** Uses the same contexts (`~/.docker/contexts`), config file and credential helpers as
  `docker`, so both tools see the same world.

## Requirements

- PowerShell 7.4 or later (Windows, Linux or macOS)
- A Docker engine: Docker Desktop, Docker Engine, or a remote engine over TCP/TLS

## Getting started

PwshDocker isn't on the PowerShell Gallery yet. Build it from source (requires the .NET 8+ SDK):

```powershell
git clone https://github.com/sryantich/PwshDocker.git
cd PwshDocker
./build.ps1 -Task Build
Import-Module ./out/PwshDocker
```

## Documentation

- [Roadmap and CLI parity matrix](docs/ROADMAP.md): every `docker` command and its PwshDocker equivalent
- [Architecture](docs/ARCHITECTURE.md): transport, object model, parallelism and cancellation
- `Get-Help about_PwshDocker` and `Get-Help <command> -Full` once the module is imported

## Development

```powershell
./build.ps1                                   # bootstrap, build, analyze, run all tests
./build.ps1 -Task Build, Test -ExcludeTag Integration   # skip tests that need a Docker engine
```

`build.ps1` downloads pinned Pester and PSScriptAnalyzer versions into `./.build`, so nothing is installed
system-wide. Tests run in a separate `pwsh` process, so the compiled core assembly never locks your session.

## License

[MIT](LICENSE)
