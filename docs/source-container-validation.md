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
Both APT phases use read-only BuildKit bind mounts for the helper, so no helper
file is added to an image layer. The builder retains its original recommended
packages; the runtime excludes recommendations. Use current Docker with BuildKit:
the legacy builder is unsupported by these mounts and lacks unused-stage isolation.

The listener host is restored, then built with `--no-dependencies` against the
already audited Miningcore assembly. Publishing uses `--no-build --no-restore`.
It cannot silently rebuild the application or native libraries on the runner.
Both managed probes still pass the existing compiler warning audit. Probe builds
force warning allowances off. Application assembly digests are checked before
and after publication; CI also compares the application's SHA-256 digest and
every native library digest between the actual runtime image and the exported
builder manifest. Missing, additional or changed artifacts fail the job. This
detects differences in covered artifacts between Buildx invocations, including
differences caused by base-image drift; identical outputs do not identify a base
change. The boundary is `Miningcore.dll` and top-level unversioned `.so` files,
not whole-image attestation. New native naming or directories must extend it.
Both probe output trees reject shared libraries, including nested/versioned `.so`
files, preserving resolution from the audited `/app` natives.

The source-container job no longer installs a host SDK/native toolchain or runs
a second host native build. Existing ZeroMQ and protected listener checks run
against the exported probes inside the production image. The listener script's
authentication, unconfigured-token refusal, rate limits, trusted proxy handling,
WebSocket and metrics checks remain intact; only its probe bind-mount path changes.
Other release and .NET/TLS test jobs remain required and unchanged.

## Bounded acquisition and cache behavior

Each Docker APT phase has one wall-time budget covering index refresh,
package download, retries and installation. Requests have 30-second inactivity
timeouts and two retries. GNU `timeout` sends TERM at the budget and KILL ten
seconds later. Normal APT signatures and package hashes remain enforced.
Update, installation and deadline failures propagate, so a partial bootstrap
cannot produce a successful image. Local Docker builds default to 1800 seconds;
both source-image CI workflows and the dependency warmer explicitly pass 420 seconds. Configure both
independent Docker stages using a build argument, for example:

```sh
docker build --build-arg MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=1200 -t miningcore-local .
```

The accepted range is 1–1800 seconds; `0` explicitly disables the total timeout
for slow local builds, while request timeouts/retries and verification remain.
All CI callers are guarded to use a positive bounded value. A host environment variable alone is not
inherited by Docker `RUN`. Status 124 identifies expiration; status 137 can mean
deadline escalation or external SIGKILL/OOM and is labelled accordingly.

All four managed probe operations share one 180-second budget and ten-second
termination grace. The `MININGCORE_PROBE_TIMEOUT_SECONDS` Docker build argument
accepts 1–600 seconds. Source-container dependency, production build and probe
export steps have 13-, 20- and 5-minute limits; protected listeners have two
minutes. This leaves five minutes of the unchanged 45-minute job budget for setup,
small checks and cleanup. The production step limit covers NuGet restore/audit,
native Git clones and compilation, rather than relying only on job cancellation.
These are ceilings, not a guarantee against exhaustion of the overall job.
There is no new
retry of the complete job, no alternate mirror and no cached package directory
that bypasses APT verification.

Only `dependency-cache` exports a GitHub Actions cache, under
`source-container-dependencies`, with the backend `timeout=1m` option. The
13-minute action deadline is the hard total bound: compression and multipart
layer uploads can outlast the backend timeout (the first reviewed CI export took
184.1 seconds). It depends on
both APT stages and no source/compile stages. Even with `mode=max`, its export
contains no compiled application/native or probe layers. Export failure is fatal
and visible; no build/cache action ignores errors. Confirm actual export completion
from BuildKit logs, rather than inferring it from successful compilation.
Every consuming production/probe build also imports this dependency scope. A
cache-only solve can record lazy cache hits without fetching their layer data;
its remote provider cannot be assumed to survive into another solve. Reimporting
keeps the provider available when COPY or image export materializes those layers.
No consuming solve exports source-dependent multi-gigabyte layers.

The CI APT budget is seven minutes so the 13-minute dependency step has room for
base pulls, the ten-second termination grace and cache compression/export. A
successful inner phase is still subject to the independent action/job deadlines.

GitVersion still needs `.git`. Checkout-specific Git metadata invalidates `COPY . .`
across CI jobs, so budget CI as a cold source/native build. Only APT dependencies
are expected to be reusable between jobs. Fully warm lab timings describe repeated
builds from one unchanged checkout and builder, not hosted-checkout performance.
GitHub's branch cache rules mean a first PR/tag run may miss every dependency cache.
The dependency-only warmer runs on relevant pushes to `dev`, weekly on the default
branch and manual dispatch. It deliberately uses `pull` and `no-cache` to validate
current package/archive availability and refresh default-branch caches readable
by release tags after the workflow lands. It never compiles or publishes images.
Cache hits do not validate current package names or security updates. A cache-service
outage still fails a required export by policy; warming does not remove that trade-off.
Python tests use `-B`, and Docker excludes bytecode/cache directories.

The small `source-container-probe-evidence` CI artifact records architecture,
artifact digests and four phase durations/statuses, with seven-day retention.
It contains no environment values or captured compiler output. BuildKit logs
retain dependency acquisition and cache-hit timings. A failed probe stage writes
an incomplete manifest inside its failed container but cannot export it; only
the `container_probe_phase` log lines survive in CI. The uploaded artifact is
available only after a successful probe export.

## Reproduce in the documented Linux test lab

Use the documented Docker-enabled Ubuntu lab with Git, Python 3, PyYAML and
Buildx. The checkout must contain its own full Git history, rather than a Windows
worktree pointer or object alternates outside the Docker context. Keep production
configuration, credentials and unrelated files outside that context.

```sh
python3 -B scripts/release/test-container-runtime-build.py
python3 -B scripts/release/test-source-container-validation.py
python3 -B scripts/release/test-container-workflow-policy.py
shellcheck scripts/release/container-apt-install.sh
python3 scripts/release/validate-source-container.py --output build/source-container-validation
```

Clean tracked files are required. The driver rejects all untracked Docker inputs,
including Git-ignored native outputs; Docker-excluded build/bytecode outputs are
allowed. Checks use the shared Git/Docker wildcard subset in the current
`.dockerignore` (`--exclude-from`, not normal Git ignores). Docker-specific pattern
extensions require updating this check and its fixtures. The source commit/tree
are rechecked before each image/probe build and before success. CI source checkout
uses the immutable PR head SHA.

The output directory must be new and inside `build/`, which is excluded from the
Docker context. The driver refuses existing reserved listener containers/networks,
occupied fixture ports or a stale probe export. It creates its own Buildx builder,
runs an uncached dependency bootstrap, a fresh source build and probe export, then
repeats with warm caches. This matches the CI dependency/source/probe target order.
Both modes run image version, provenance, ZeroMQ and the workflow's actual
protected listener script. Cold and warm probe exports must reuse the audited
production publish layer; warm validation also requires production publish and probe
build cache hits. The driver removes its own builder, preserves evidence and
leaves uniquely tagged images for inspection. It never prunes shared Docker caches.
This lab tests local builder reuse, not GHA lazy-layer restoration. A fresh-runner
CI rerun on the same commit must independently show both APT RUN vertices as
`CACHED` in the dependency target and production image build. Keep cache imports
on all consuming solves even when a previous local run appeared to materialize layers.
Interruption stops the active subprocess group and still attempts builder cleanup.
Cleanup failure returns nonzero and leaves `complete: false`, distinguished from
`functional_validation_passed`; the original failure/interruption code is preserved.
An uncatchable SIGKILL cannot run cleanup: after checking no process still owns it,
remove only that run's uniquely named builder reported in its logs.

`evidence.json` records monotonic phase durations and bounded BuildKit vertex/cache
summaries. Each phase log is capped at 64 MiB and explicitly marks truncation;
probe manifests and listener script copies accompany the result. A failed phase
leaves `complete: false` and returns failure. The offline contracts inject failed
APT update/install, stalled acquisition, restore/build/publish failures, compiler
warnings, changed assemblies and bad provenance to verify failure propagation.
They also cover dirty/staged/extra source inputs, cleanup failure, interruption,
malformed packages, invalid budgets, a shared total deadline and probe native
libraries. Static guards cover stage isolation, recommendation policy, immutable
checkout, deadlines and cache failure policy.

## Existing release-wide follow-ups

Tracked in [#225](https://github.com/NINJAK1DD/miningcore/issues/225).

The source SDK/ASP.NET tags still float. Digest pinning needs an explicit servicing
and refresh policy alongside the existing release Ubuntu image pin monitor.
The separate `Dockerfile.release` and release-matrix bootstrap have their own APT
paths; they have not acquired this helper's guarantees. Extending bounded bootstrap
to those publishing paths requires separate release validation. They are not
covered by claims about this source-container job.

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
