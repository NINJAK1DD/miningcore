FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build-dependencies
ARG MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=1800
WORKDIR /app
ENV DOTNET_CLI_UI_LANGUAGE=en \
    LC_ALL=C
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN --mount=type=bind,source=scripts/release/container-apt-install.sh,target=/tmp/container-apt-install.sh,readonly \
    bash /tmp/container-apt-install.sh --install-recommends cmake clang ninja-build build-essential libssl-dev pkg-config \
        libboost-all-dev libsodium-dev libzmq5 libzmq3-dev golang-go libgmp-dev \
        libc++-dev python3 zlib1g-dev
FROM build-dependencies AS builder
COPY . .
WORKDIR /app/src/Miningcore
RUN set -euo pipefail; \
    build_log=$(mktemp); \
    trap 'rm -f -- "$build_log"' EXIT; \
    dotnet publish -c Release --framework net10.0 -o ../../build \
      2>&1 | tee "$build_log"; \
    bash ../../scripts/release/assert-warning-free-build.sh "$build_log"; \
    bash ../../scripts/release/test-native-load-fenv.sh ../../build --cryptonote-vectors; \
    python3 ../../scripts/release/assert-linux-native-symbol-contracts.py \
      ../../build Native ../../scripts/release/linux-native-libraries.txt \
      --managed-project-directory .

FROM builder AS runtime-probes-build
ARG MININGCORE_PROBE_TIMEOUT_SECONDS=180
WORKDIR /app
RUN python3 scripts/release/publish-container-probes.py /app /app/build /container-runtime-probes

# Only CI asks for this target; the default final image still contains /app/build.
FROM scratch AS runtime-probes
COPY --from=runtime-probes-build /container-runtime-probes/ /

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute AS runtime-dependencies
ARG MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=1800
WORKDIR /app
# ZeroMQ.dll imports the unversioned libzmq name. Keep libzmq3-dev because it
# supplies libzmq.so; libzmq5 supplies only the versioned libzmq.so.5 object.
# This deliberate runtime exception also installs its development dependencies.
RUN --mount=type=bind,source=scripts/release/container-apt-install.sh,target=/tmp/container-apt-install.sh,readonly \
    bash /tmp/container-apt-install.sh \
        curl \
        libboost-locale1.90.0 \
        libboost-regex1.90.0 \
        libboost-serialization1.90.0 \
        libgmp10 \
        libsodium23 \
        libzmq3-dev && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system --gid 10001 miningcore && \
    useradd --system --uid 10001 --gid miningcore --home-dir /var/lib/miningcore \
        --shell /usr/sbin/nologin miningcore && \
    mkdir -p /var/lib/miningcore /etc/miningcore && \
    chown miningcore:miningcore /var/lib/miningcore

# Export only source-independent dependency layers to the shared CI cache.
FROM scratch AS dependency-cache
COPY --from=build-dependencies /usr/bin/cmake /build-dependencies-marker
COPY --from=runtime-dependencies /usr/bin/curl /runtime-dependencies-marker

FROM runtime-dependencies AS runtime
COPY --from=builder --chown=root:root /app/build ./
RUN set -eu; \
    for library in /app/*.so; do \
        relocations=$(ldd -r "$library" 2>&1) || { \
            printf '%s\n' "$relocations" >&2; \
            exit 1; \
        }; \
        if printf '%s\n' "$relocations" | grep -Eq 'not found|undefined symbol:'; then \
            printf '%s\n' "$relocations" >&2; \
            exit 1; \
        fi; \
    done
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    LD_LIBRARY_PATH=/app
VOLUME ["/var/lib/miningcore"]
EXPOSE 4000-4090
USER miningcore
ENTRYPOINT ["./Miningcore"]
CMD ["-c", "/etc/miningcore/config.json"]
