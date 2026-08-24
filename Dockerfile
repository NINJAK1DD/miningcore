FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS builder
WORKDIR /app
ENV DOTNET_CLI_UI_LANGUAGE=en \
    LC_ALL=C
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN apt-get update && \
    apt-get -y install cmake clang ninja-build build-essential libssl-dev pkg-config \
        libboost-all-dev libsodium-dev libzmq5 libzmq3-dev golang-go libgmp-dev \
        libc++-dev python3 zlib1g-dev
COPY . .
WORKDIR /app/src/Miningcore
RUN set -euo pipefail; \
    build_log=$(mktemp); \
    trap 'rm -f -- "$build_log"' EXIT; \
    dotnet publish -c Release --framework net10.0 -o ../../build \
      2>&1 | tee "$build_log"; \
    bash ../../scripts/release/assert-warning-free-build.sh "$build_log"; \
    python3 ../../scripts/release/assert-linux-native-symbol-contracts.py \
      ../../build Native ../../scripts/release/linux-native-libraries.txt

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute
WORKDIR /app
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        libboost-locale1.90.0 \
        libboost-regex1.90.0 \
        libboost-serialization1.90.0 \
        libgmp10 \
        libsodium-dev \
        libzmq3-dev && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system --gid 10001 miningcore && \
    useradd --system --uid 10001 --gid miningcore --home-dir /var/lib/miningcore \
        --shell /usr/sbin/nologin miningcore && \
    mkdir -p /var/lib/miningcore /etc/miningcore && \
    chown miningcore:miningcore /var/lib/miningcore
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
