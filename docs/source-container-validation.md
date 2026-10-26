# Source container dependency budgets and runtime validation

Issue [#223](https://github.com/NINJAK1DD/miningcore/issues/223) concerns a release
check cancelled before its protected listener tests. In Release run
[40238781701](https://github.com/NINJAK1DD/miningcore/actions/runs/40238781701),
the cancelled attempt spent 25m56s installing the listener host's dependencies,
including 25m37s downloading 87.3 MB at 56.8 kB/s. Its unchanged successful retry
spent 11m56s on that step. The source image itself built in 12m05s and 11m51s.
That evidence identifies package acquisition, rather than demonstrated CPU
contention, as the main difference. Increasing the 45-minute job deadline would
leave the duplicate work and unbounded downloads in place.

## Build and verification contract

The production image retains its warning-free source build, Linux native symbol
audits, runtime relocation checks, non-root identity and runtime packages.
The optional `runtime-probes` Docker target extends that same builder and exports
only the managed listener host, ZeroMQ probe and bounded build evidence. The
default final image remains the production runtime image.

The listener host is restored, then built with `--no-dependencies` against the
already audited Miningcore assembly. Publishing uses `--no-build --no-restore`.
It cannot silently rebuild the application or native libraries on the runner.
Both managed probes still pass the existing compiler warning audit. Probe builds
force warning allowances off. Application assembly digests are checked before
and after publication; CI also compares the application's SHA-256 digest and
every native library digest between the actual runtime image and the exported
builder manifest. Missing, additional or changed artifacts fail the job. This
also catches a base-image change between the two Buildx invocations.

The source-container job no longer installs a host SDK/native toolchain or runs
a second host native build. Existing ZeroMQ and protected listener checks run
against the exported probes inside the production image. The listener script's
authentication, unconfigured-token refusal, rate limits, trusted proxy handling,
WebSocket and metrics checks remain intact; only its probe bind-mount path changes.
Other release and .NET/TLS test jobs remain required and unchanged.

## Bounded acquisition and cache behavior

Each Docker APT phase has one 600-second wall-time budget covering index refresh,
package download, retries and installation. Requests have 30-second inactivity
timeouts and two retries. GNU `timeout` sends TERM at the budget and KILL ten
seconds later. Normal APT signatures and package hashes remain enforced.
Update, installation and deadline failures propagate, so a partial bootstrap
cannot produce a successful image. Overrides through
`MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS` accept only 1–1800 seconds and are
intended for isolated diagnostics, not an automatic retry loop in CI.

Each managed probe operation has a 300-second budget with the same ten-second
termination grace. `MININGCORE_PROBE_STEP_TIMEOUT_SECONDS` is similarly bounded.
The whole source-container job retains its 45-minute deadline. There is no new
retry of the complete job, no alternate mirror and no cached package directory
that bypasses APT verification.

BuildKit imports and exports have a two-minute cache-service budget. Production
layers and probe layers use separate GitHub Actions cache scopes so one export
does not overwrite the other. A failed optional cache export may be ignored;
build, warning, symbol, provenance and runtime failures never are. Cache misses
run the same audited build. GitHub's branch access rules still apply, so a PR
cache cannot be assumed to be available to another branch or a release tag.

The small `source-container-probe-evidence` CI artifact records architecture,
artifact digests and four phase durations/statuses, with seven-day retention.
It contains no environment values or captured compiler output. BuildKit logs
retain dependency acquisition and cache-hit timings.

## Reproduce in the documented Linux test lab

Use the documented Docker-enabled Ubuntu lab with Git, Python 3, PyYAML and
Buildx. The checkout must contain its own full Git history, rather than a Windows
worktree pointer or object alternates outside the Docker context. Keep production
configuration, credentials and unrelated files outside that context.

```sh
python3 scripts/release/test-container-runtime-build.py
shellcheck scripts/release/container-apt-install.sh
python3 scripts/release/validate-source-container.py --output build/source-container-validation
```

The output directory must be new and inside `build/`, which is excluded from the
Docker context. The driver refuses existing reserved listener containers/networks,
occupied fixture ports or a stale probe export. It creates its own Buildx builder,
runs an uncached production build and probe export, then repeats with warm caches.
Both modes run image version, provenance, ZeroMQ and the workflow's actual
protected listener script. Warm validation requires production publish and probe
build cache hits. The driver removes its own builder, preserves evidence and
leaves uniquely tagged images for inspection. It never prunes shared Docker caches.

`evidence.json` records monotonic phase durations and bounded BuildKit vertex/cache
summaries. Each phase log is capped at 64 MiB and explicitly marks truncation;
probe manifests and listener script copies accompany the result. A failed phase
leaves `complete: false` and returns failure. The offline contracts inject failed
APT update/install, stalled acquisition, restore/build/publish failures, compiler
warnings, changed assemblies and bad provenance to verify failure propagation.

## Design references

- [Docker multi-stage builds](https://docs.docker.com/build/building/multi-stage/)
  explain reuse of a named builder without adding the SDK to the runtime image.
- [Docker local exporter](https://docs.docker.com/build/exporters/local-tar/)
  documents extraction of the probe-only target.
- [BuildKit GitHub Actions cache](https://docs.docker.com/build/cache/backends/gha/)
  documents cache scopes, timeout limits, export error policy and branch restrictions.
- [.NET build](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build)
  defines `--no-dependencies`; [.NET publish](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish)
  defines `--no-build` and its implied restore suppression.
