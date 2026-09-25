# PwshDocker Roadmap

PwshDocker is a PowerShell 7.4+ module that manages Docker by talking **directly to the Docker Engine API**
(named pipe, unix socket, TCP/TLS). It does not wrap or shell out to the `docker` CLI. The goal is full feature parity
with the Docker CLI, plus the things a PowerShell-native tool can do better: real objects, pipelines,
`-WhatIf`/`-Confirm`, live tab completion, streaming objects, bounded parallelism, and multi-engine fan-out.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how it is built.

## Why this exists

| Prior art | Status |
|---|---|
| Microsoft `Docker-PowerShell` (Docker.DotNet based) | Archived April 2018 ("no longer maintained due to low usage") |
| PSGallery `Docker`, `PSDocker`, `DockerPS`, `DockerPowerShell` | Last updated 2017-2022; CLI wrappers |
| `DockerCompletion`, `posh-docker` | Tab completion for the CLI only |

The lesson from Microsoft's attempt: `docker ... --format json | ConvertFrom-Json` is "good enough" for many people,
so PwshDocker must be *clearly* better than that, not just different:

- **No process-per-call overhead.** One pooled HTTP connection manager per engine; requests run concurrently.
- **Parallel by design.** Bulk commands (`Get-DockerContainer | Stop-DockerContainer`) run with bounded concurrency
  (`-ThrottleLimit`), stream results as they complete, and cancel cleanly on Ctrl+C.
- **Fleet-aware.** `-Context a,b,c` fans out to several engines at once; every object remembers which engine it came
  from, so piping it to another command targets the right host.
- **Objects, not text.** Curated types (dates as `DateTime`, sizes as bytes, labels as dictionaries, ports as objects)
  with docker-like table views, plus the full API payload for power users.
- **PowerShell semantics.** Approved verbs, pipeline binding by name/ID/object, `-WhatIf`/`-Confirm`, `-PassThru`,
  non-terminating per-item errors, comment-based help with examples for every command.

## Principles

1. **Engine API first, no CLI wrapping.** Where the Docker CLI implements behavior client-side (contexts, credential
   helpers, `docker stats` math, `docker cp` tar handling, compose, stack deploy, BuildKit sessions), PwshDocker
   re-implements it natively. When behavior is not clearly documented, we read the `docker/cli`, `docker/compose` and
   `moby/moby` sources and capture the CLI's real API traffic with the PwshDocker API tracer.
2. **Interoperate, don't fork.** Use the same context store (`~/.docker/contexts`), config file, credential helpers and
   compose labels as the CLI, so PwshDocker and `docker` always see the same world.
3. **Verb-Noun discipline.** Approved verbs only, singular nouns prefixed with `Docker`, enforced by PSScriptAnalyzer
   and Pester tests.
4. **Every command is documented and tested.** Comment-based help (synopsis, description, every parameter, examples) is
   enforced by tests; integration tests run against a live engine.

## Phases

| Phase | Scope | Status |
|---|---|---|
| **0 - Foundation** | Repo, build, CI, analyzer, test harness, docs. C# core: context/endpoint resolution, npipe/unix/tcp/TLS transports, API negotiation, errors, JSON->objects, stream readers, Ctrl+C-aware waits, parallel runner. `Invoke-DockerApi`, `Test-DockerEngine`, `Get-DockerVersion`, `Get-DockerInfo`, `Get-DockerContext`, `Use-DockerContext`. API tracer dev tool. | Done (tracer pending) |
| **1 - Containers & images** | Container lifecycle, logs, exec (non-interactive), top, stats, wait (incl. *healthy*), prune. Images: list, pull with progress, remove, tag, history, search, save/load, prune. Registry auth via config + credential helpers. Formats, completers, parallel bulk ops, fan-out. | Planned |
| **2 - Networks, volumes, system, files, contexts** | Networks, volumes, disk usage, events, system prune, `docker cp` (native tar), export, commit/import, container update, full context management (create/update/rm/export/import), SSH transport. | Planned |
| **3 - Interactive & build** | `Enter-DockerContainer` (TTY over hijacked connection: raw console, resize, detach keys), stdin for exec, `Build-DockerImage` (`.dockerignore`-aware context streaming; classic builder, then BuildKit `/session` + gRPC), push, `Update-DockerImage`. | Planned |
| **4 - Swarm & plugins** | Swarm, nodes, services, tasks, secrets, configs, service logs, plugins. | Planned |
| **5 - Compose & stacks (native)** | Compose-spec parser (YAML, interpolation, `.env`, profiles, `extends`/`include`), dependency graph, up/down/ls/config, compose-compatible labels so `docker compose` sees the same projects; `Deploy-DockerStack` on top. | Planned |
| **6 - Beyond the CLI** | CLI-to-PowerShell translator, run-command regeneration, PowerShell eventing for engine events, image auto-update (watchtower-style), `Docker:` PSDrive provider, OCI registry commands, SecretManagement integration, `Trace-DockerApi`. | Planned |

## CLI parity matrix

Legend: **P0-P6** = phase. Names are proposals until the phase ships; see [Open design questions](#open-design-questions).

### Engine, system, contexts, registries

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker version` | `Get-DockerVersion` | P0 |
| `docker info` | `Get-DockerInfo` | P0 |
| *(ping)* | `Test-DockerEngine` | P0 |
| *(raw API access)* | `Invoke-DockerApi` | P0 |
| `docker context ls` / `inspect` / `show` | `Get-DockerContext [-Name] [-Current]` | P0 |
| `docker context use` | `Use-DockerContext [-Persist]` | P0 |
| `docker context create` / `update` / `rm` | `New-DockerContext` / `Set-DockerContext` / `Remove-DockerContext` | P2 |
| `docker context export` / `import` | `Export-DockerContext` / `Import-DockerContext` | P2 |
| `docker system df` | `Get-DockerDiskUsage` | P2 |
| `docker events` | `Get-DockerEvent [-Follow]` | P2 |
| `docker system prune` | `Clear-DockerSystem` | P2 |
| `docker login` / `logout` | `Connect-DockerRegistry` / `Disconnect-DockerRegistry` | P1 |
| `docker search` | `Find-DockerImage` | P1 |

### Containers

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker ps` / `container ls` | `Get-DockerContainer` | P1 |
| `docker inspect` / `container inspect` | `Get-DockerContainer -Detailed` | P1 |
| `docker create` | `New-DockerContainer` | P1 |
| `docker run` | `Invoke-DockerContainer` (foreground output, or `-Detach`) | P1 |
| `docker start` | `Start-DockerContainer` | P1 |
| `docker stop` | `Stop-DockerContainer` | P1 |
| `docker restart` | `Restart-DockerContainer` | P1 |
| `docker kill` | `Send-DockerContainerSignal` | P1 |
| `docker pause` / `unpause` | `Suspend-DockerContainer` / `Resume-DockerContainer` | P1 |
| `docker rm` | `Remove-DockerContainer` | P1 |
| `docker rename` | `Rename-DockerContainer` | P1 |
| `docker wait` | `Wait-DockerContainer` (+ `-Condition Healthy`, beyond CLI) | P1 |
| `docker logs` | `Get-DockerContainerLog [-Follow]` | P1 |
| `docker exec` (non-interactive) | `Invoke-DockerContainerCommand` | P1 |
| `docker top` | `Get-DockerContainerProcess` | P1 |
| `docker stats` | `Measure-DockerContainer [-Follow]` | P1 |
| `docker port` | `Get-DockerContainerPort` | P1 |
| `docker diff` | `Get-DockerContainerChange` | P1 |
| `docker container prune` | `Clear-DockerContainer` | P1 |
| `docker cp` | `Copy-DockerContainerItem -FromContainer/-ToContainer` | P2 |
| `docker export` | `Export-DockerContainer` | P2 |
| `docker commit` | `ConvertTo-DockerImage -Container` | P2 |
| `docker update` | `Set-DockerContainer` | P2 |
| `docker exec -it` / `docker attach` | `Enter-DockerContainer [-Attach]` | P3 |

### Images and builds

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker images` / `image ls` | `Get-DockerImage` | P1 |
| `docker image inspect` | `Get-DockerImage -Detailed` | P1 |
| `docker pull` | `Install-DockerImage` | P1 |
| `docker rmi` | `Remove-DockerImage` | P1 |
| `docker tag` | `Add-DockerImageTag` | P1 |
| `docker history` | `Get-DockerImageHistory` | P1 |
| `docker save` / `load` | `Export-DockerImage` / `Import-DockerImage` | P1 |
| `docker image prune` | `Clear-DockerImage` | P1 |
| `docker import` | `ConvertTo-DockerImage -Path` | P2 |
| `docker push` | `Publish-DockerImage` | P3 |
| `docker build` / `buildx build` | `Build-DockerImage` | P3 |
| `docker builder prune` / `buildx du` | `Clear-DockerBuildCache` / `Get-DockerBuildCache` | P3 |
| *(re-pull, report changes)* | `Update-DockerImage [-Recreate]` (beyond CLI) | P3 |

### Networks and volumes

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker network ls` / `inspect` | `Get-DockerNetwork` | P2 |
| `docker network create` / `rm` / `prune` | `New-DockerNetwork` / `Remove-DockerNetwork` / `Clear-DockerNetwork` | P2 |
| `docker network connect` / `disconnect` | `Connect-DockerNetwork` / `Disconnect-DockerNetwork` | P2 |
| `docker volume ls` / `inspect` | `Get-DockerVolume` | P2 |
| `docker volume create` / `rm` / `prune` / `update` | `New-DockerVolume` / `Remove-DockerVolume` / `Clear-DockerVolume` / `Set-DockerVolume` | P2 |

### Swarm and plugins

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker swarm init` / `join` / `leave` | `Initialize-DockerSwarm` / `Join-DockerSwarm` / `Exit-DockerSwarm` | P4 |
| `docker swarm update` / *(inspect)* | `Set-DockerSwarm` / `Get-DockerSwarm` | P4 |
| `docker swarm join-token [--rotate]` | `Get-DockerSwarmJoinToken` / `Reset-DockerSwarmJoinToken` | P4 |
| `docker swarm unlock` / `unlock-key [--rotate]` | `Unlock-DockerSwarm` / `Get-DockerSwarmUnlockKey` / `Reset-DockerSwarmUnlockKey` | P4 |
| `docker node ls` / `inspect` / `update` / `promote` / `demote` / `rm` | `Get-DockerNode` / `Set-DockerNode` / `Remove-DockerNode` | P4 |
| `docker service create` / `ls` / `inspect` / `update` / `scale` / `rm` | `New-DockerService` / `Get-DockerService` / `Set-DockerService` / `Remove-DockerService` | P4 |
| `docker service rollback` / `logs` / `ps` | `Undo-DockerService` / `Get-DockerServiceLog` / `Get-DockerTask` | P4 |
| `docker secret` / `docker config` | `Get-`/`New-`/`Remove-DockerSecret`, `Get-`/`New-`/`Remove-DockerConfig` | P4 |
| `docker plugin ...` | `Get`/`Install`/`Enable`/`Disable`/`Set`/`Update`/`Publish`/`New`/`Remove-DockerPlugin` | P4 |

### Compose and stacks (native re-implementation)

| Docker CLI | PwshDocker | Phase |
|---|---|---|
| `docker compose up` | `Deploy-DockerComposeProject` | P5 |
| `docker compose down` | `Remove-DockerComposeProject` | P5 |
| `docker compose ls` | `Get-DockerComposeProject` | P5 |
| `docker compose config` | `Resolve-DockerComposeFile` | P5 |
| `docker compose ps` / `logs` / `stop` / `start` / `restart` / `kill` / `exec` / `top` / `port` | Container commands with `-ComposeProject` / `-ComposeService` filters | P5 |
| `docker stack deploy` / `ls` / `rm` / `ps` / `services` | `Deploy-DockerStack` / `Get-DockerStack` / `Remove-DockerStack` / `Get-DockerTask -Stack` / `Get-DockerService -Stack` | P5 |

### Beyond the CLI

| Feature | PwshDocker | Phase |
|---|---|---|
| Fan-out to many engines | `-Context a,b,c` on every command | P0+ |
| Wait for healthcheck | `Wait-DockerContainer -Condition Healthy` | P1 |
| Translate `docker ...` command lines | `ConvertFrom-DockerCommandLine` | P6 |
| Regenerate a container's run command | `ConvertTo-DockerCommandLine` | P6 |
| Engine events as PowerShell events | `Register-DockerEvent` | P6 |
| Watchtower-style image updates | `Update-DockerImage -Recreate` | P3/P6 |
| Browse engines as a drive | `Docker:` PSDrive provider | P6 |
| Registry/OCI operations (tags, manifests, copy) | `Get-DockerManifest`, `Get-DockerRegistryTag`, ... | P6 |
| Registry creds from SecretManagement vaults | `Connect-DockerRegistry -Vault` | P6 |
| See exactly what any `docker` command sends | `Trace-DockerApi` (starts as a dev tool) | P0 tool / P6 command |

### Out of scope (for now)

- **Docker Desktop product plugins**: `ai`, `debug`, `desktop`, `extension`, `init`, `mcp`, `model`, `offload`, `pass`,
  `sandbox`, `scout`, `sbom`. These are separate products/services, not part of the Engine or core CLI.
- **`docker trust`** (Notary v1) - revisit if there is demand.
- **buildx builder-instance management** (docker-container/kubernetes drivers) - revisit after native BuildKit builds.
- **Windows PowerShell 5.1** - the module targets PowerShell 7.4+ (.NET 8+) for unix sockets, `SocketsHttpHandler`
  connect callbacks, `System.Formats.Tar`, and the `clean {}` block.

## Hard parts (known risks)

| Area | Why it's hard | Plan |
|---|---|---|
| Interactive TTY | Raw console mode, resize events, detach keys, half-close over named pipes | Hijacked HTTP/1.1 upgrade stream (`SocketsHttpHandler` returns a duplex stream on `101`), console VT mode; research with the tracer |
| BuildKit builds | Requires a `/session` hijack speaking gRPC (filesync, auth, secrets, ssh) and protobuf progress decoding | Ship classic builder first; implement BuildKit session with grpc-dotnet over the hijacked stream |
| Compose | Large spec, interpolation rules, dependency/health ordering, label compatibility | Dedicated phase; conformance tests against `docker compose config` output |
| Credential helpers | Secrets live in OS keychains behind `docker-credential-*` helpers | Speak the documented helper protocol directly (these are not the docker CLI) |
| YAML dependency | Compose needs a YAML parser; assembly conflicts with other modules | Isolate dependencies in a custom AssemblyLoadContext |

## Open design questions

These names/defaults are proposals; feedback welcome before the relevant phase ships.

1. **`docker run` -> `Invoke-DockerContainer`.** "Run" is not an approved verb. `Invoke-` matches "execute and
   return output" (like `Invoke-Command`); `-Detach` returns the container object instead. Alternative:
   `New-DockerContainer ... | Start-DockerContainer`, which also works.
2. **`docker pull` -> `Install-DockerImage`.** Mirrors `Install-Module` (download from a repository into a local store)
   and pairs with `Update-DockerImage`. Alternative considered: `Request-DockerImage` (Microsoft's old module).
3. **`Get-DockerContainer` returns all containers by default** (PowerShell convention, like `Get-Service`), not only
   running ones like `docker ps`. Use `-State Running` to filter.
4. **Prune -> `Clear-Docker*`.** `Clear-` = "remove all resources from a container (store) without deleting it", as in
   `Clear-RecycleBin`.
