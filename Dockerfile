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
mongod \
  --dbpath /tmp/mdb \
  --bind_ip 127.0.0.1 \
  --port 27017 \
  --fork \
  --logpath /tmp/mongod.log
trap 'mongod --dbpath /tmp/mdb --shutdown || true' EXIT
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

FROM build AS buildaot
RUN dotnet nuget enable source nuget.org
RUN dotnet publish -f net10.0 Tests/Synqra.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests; \
    withmongo Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"
RUN dotnet publish Tests/Synqra.BinarySerializer.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.BinarySerializer.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.BinarySerializer.Tests; \
    Tests/Synqra.BinarySerializer.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.BinarySerializer.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM scratch AS art
COPY --from=pack /out /

# Publish the demo host so the container serves a self-contained wwwroot (_framework/blazor.web.js + WASM
# payload). Publish MUST restore: `dotnet publish --no-restore` silently drops blazor.web.js from the output,
# which then 404s and the WASM never boots. Done in a build-derived stage for the wasm-tools workload.
# ErrorOnDuplicatePublishOutputFiles=false: two projects ship a tsconfig.json at the same relative path
# (NETSDK1152); neither is a runtime asset.
FROM build AS webpublish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish Contoso/Contoso.WebHost -c $BUILD_CONFIGURATION -o /app -p:ErrorOnDuplicatePublishOutputFiles=false

# Browser-level gate for the v7 sub-ms fix. The official Playwright image ships Chromium + all OS deps
# preinstalled (tag pinned to the Microsoft.Playwright package version so the driver matches the browser).
# We copy the pinned SDK + built test project from `build` and the published host from `webpublish`, serve it
# (from /app so the content root finds the static-assets manifest), and run the single non-[Explicit] test.
FROM mcr.microsoft.com/playwright/dotnet:v1.52.0-jammy AS playwright
ARG BUILD_CONFIGURATION=Release
SHELL ["/bin/bash", "-c"]
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
COPY --from=build /usr/share/dotnet /usr/share/dotnet
WORKDIR /src
COPY --from=build /src /src
COPY --from=webpublish /app /app
RUN set -eux; \
    ( cd /app && dotnet Contoso.WebHost.dll --urls http://127.0.0.1:5063 ) >/tmp/host.log 2>&1 & \
    hostpid=$!; \
    for i in $(seq 1 90); do (exec 3<>/dev/tcp/127.0.0.1/5063) 2>/dev/null && break || sleep 2; done; \
    SYNQRA_CONTOSO_TEST_HOST=http://127.0.0.1:5063/ dotnet test Contoso/Contoso.Playwright -c $BUILD_CONFIGURATION --no-restore --no-build 2>&1 | tee /tmp/test.log; \
    rc=${PIPESTATUS[0]}; \
    kill $hostpid 2>/dev/null || true; \
    if [ "$rc" != "0" ]; then echo "===HOSTLOG==="; cat /tmp/host.log || true; fi; \
    exit $rc

#Sync parallel builds here (should be last stage)
FROM scratch AS log
COPY --from=test /src /stage/test
COPY --from=buildaot /src /stage/buildaot
COPY --from=playwright /src /stage/playwright
