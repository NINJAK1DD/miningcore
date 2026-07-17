FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS builder
WORKDIR /app
RUN apt-get update && \
    apt-get -y install cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libzmq3-dev golang-go libgmp-dev libc++-dev zlib1g-dev
COPY . .
WORKDIR /app/src/Miningcore
RUN dotnet publish -c Release --framework net10.0 -o ../../build

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
RUN apt-get update && \
    apt-get install -y --no-install-recommends libgmp10 libzmq3-dev libsodium-dev curl && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system --gid 10001 miningcore && \
    useradd --system --uid 10001 --gid miningcore --home-dir /var/lib/miningcore \
        --shell /usr/sbin/nologin miningcore && \
    mkdir -p /var/lib/miningcore /etc/miningcore && \
    chown miningcore:miningcore /var/lib/miningcore
COPY --from=builder --chown=root:root /app/build ./
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    LD_LIBRARY_PATH=/app
VOLUME ["/var/lib/miningcore"]
EXPOSE 4000-4090
USER miningcore
ENTRYPOINT ["./Miningcore"]
CMD ["-c", "/etc/miningcore/config.json"]
