#syntax=docker/dockerfile:1-labs
ARG BUILD_BUILDNUMBER="0"

#FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
#USER $APP_UID
#WORKDIR /app

# Pinned to a specific SDK patch. The floating :10.0 tag moved from 10.0.301 to 10.0.302, and on
# 10.0.302 `dotnet test` (Microsoft.Testing.Platform) returns exit code 1 *even when every test
# passes* ("Tests succeeded" then exit 1), failing the `test` stage below for no real reason.
# 10.0.301 is the last-known-good (green build 12795). Bump deliberately, re-verifying `dotnet test`
# still exits 0 on success, rather than tracking :10.0 silently.
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
# wasm-tools/emscripten glue scripts expects python3 in PATH
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    ca-certificates \
    curl \
    git \
    clang \
    zlib1g-dev \
    mc \
 && rm -rf /var/lib/apt/lists/*
RUN dotnet workload install wasm-tools
# provision built-in mongo instance. You can run it in background in the same RUN as test
COPY --from=mongo:8.0 /usr/bin/mongod /usr/local/bin/mongod
RUN cat > /usr/local/bin/withmongo <<'EOF' && chmod +x /usr/local/bin/withmongo
#!/bin/sh
set -eux
mkdir -p /tmp/mdb
# WiredTiger keeps a file per collection and index; at a 1024 soft limit mongod dies mid-suite (EMFILE).
ulimit -n "$(ulimit -Hn)"
echo "nofile soft=$(ulimit -Sn) hard=$(ulimit -Hn)"
# Linux 6.19 to 7.0.13 broke the rseq per-CPU caches of mongod's TCMalloc, and mongod refuses to start there
# (SERVER-121912; an Ubuntu 26.04 agent reports 7.0.0). glibc keeping rseq for itself sends TCMalloc to its
# per-thread caches; mongod then starts. Older kernels are unaffected.
export GLIBC_TUNABLES=glibc.pthread.rseq=1
mongod \
  --dbpath /tmp/mdb \
  --bind_ip 127.0.0.1 \
  --port 27017 \
  --replSet rs0 \
  --fork \
  --logpath /tmp/mongod.log
trap 'mongod --dbpath /tmp/mdb --shutdown || true' EXIT
# One-node replica set: Synqra writes run in transactions, which a standalone refuses. The test
# harness (Synqra EphemeralMongo) initiates the set on first use.
export ConnectionStrings__Mongodb="mongodb://127.0.0.1:27017"
exec "$@"
EOF
ARG BUILD_CONFIGURATION=Release
# && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
# && chmod +x /tmp/dotnet-install.sh \
# && /tmp/dotnet-install.sh --install-dir /usr/share/dotnet --runtime dotnet --channel 8.0 \
# && /tmp/dotnet-install.sh --install-dir /usr/share/dotnet --runtime dotnet --channel 9.0 \
# && rm /tmp/dotnet-install.sh
WORKDIR /src
COPY --parents **/*.*proj *.sln global.json *.targets *.props ./
RUN dotnet restore "-clp:ErrorsOnly;NoSummary" -tl:false -nologo
# Chromium for the browser stage, with the driver the restored Microsoft.Playwright package ships: cached like the restore,
# installed again only when a project file or a package version changes.
RUN pw=$(echo /root/.nuget/packages/microsoft.playwright/*/.playwright) \
 && "$pw/node/linux-x64/node" "$pw/package/cli.js" install --with-deps chromium
COPY . .
RUN dotnet build Synqra.CodeGeneration -c $BUILD_CONFIGURATION --no-restore "-clp:ErrorsOnly;NoSummary" -nologo -tl:off
RUN dotnet build                       -c $BUILD_CONFIGURATION --no-restore "-clp:ErrorsOnly;NoSummary" -nologo -tl:off

FROM build AS test
RUN withmongo dotnet test Tests/Synqra.Tests             -c $BUILD_CONFIGURATION --no-restore --no-build -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"
RUN            dotnet test Tests/Synqra.BinarySerializer.Tests -c $BUILD_CONFIGURATION --no-restore --no-build -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM build AS pack
ARG BUILD_BUILDNUMBER
ENV BUILD_BUILDNUMBER=$BUILD_BUILDNUMBER
ARG BUILD_BUILDNUMBER
RUN dotnet pack -o /out                -c $BUILD_CONFIGURATION --no-restore --no-build -clp:ErrorsOnly -nologo -tl:off
RUN printenv > /out/env.txt

# Its own stage, so BuildKit publishes it beside Synqra.Tests rather than after it.
FROM build AS buildaot-serializer
RUN dotnet nuget enable source nuget.org
RUN dotnet publish Tests/Synqra.BinarySerializer.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.BinarySerializer.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.BinarySerializer.Tests; \
    Tests/Synqra.BinarySerializer.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.BinarySerializer.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM build AS buildaot
RUN dotnet nuget enable source nuget.org
RUN dotnet publish -f net10.0 Tests/Synqra.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests; \
    withmongo Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"
COPY --from=buildaot-serializer /src/global.json /stage/buildaot-serializer/

# The browser suite: headless Chromium against the Contoso master, a real WASM client over IndexedDB (docs/model.md §10).
# The suite runs on VSTest (NUnit), which the 10.0.302 `dotnet test` exit-code fault above does not touch.
FROM build AS browser
# Development: the host serves the projects' static web assets from the build output, as `dotnet run` does.
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS=http://127.0.0.1:5063 \
    SYNQRA_CONTOSO_TEST_HOST=http://127.0.0.1:5063/
WORKDIR /src/Contoso/Contoso.WebHost
RUN (dotnet bin/$BUILD_CONFIGURATION/net10.0/Contoso.WebHost.dll > /tmp/contoso.log 2>&1 &) \
 && for i in $(seq 1 60); do curl -sf -o /dev/null http://127.0.0.1:5063/seeding && break; sleep 1; done \
 && curl -sf -o /dev/null http://127.0.0.1:5063/_framework/blazor.web.js \
 && dotnet test /src/Contoso/Contoso.Playwright -c $BUILD_CONFIGURATION --no-restore --no-build --filter "Category=Seeding" \
 || (tail -n 200 /tmp/contoso.log; exit 1)

FROM scratch AS art
COPY --from=pack /out /

#Sync parallel builds here (should be last stage)
# One small file per stage is enough to run it; copying all of /src made exporting the image take about 35 s.
FROM scratch AS log
COPY --from=test /src/global.json /stage/test/
COPY --from=buildaot /src/global.json /stage/buildaot/
COPY --from=browser /src/global.json /stage/browser/
