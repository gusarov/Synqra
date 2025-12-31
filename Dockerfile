#syntax=docker/dockerfile:1-labs

#FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
#USER $APP_UID
#WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
RUN apt-get update && apt-get install -y --no-install-recommends mc clang zlib1g-dev curl
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
RUN dotnet test Tests/Synqra.Tests     -c $BUILD_CONFIGURATION --no-restore --no-build -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM build AS pack
RUN dotnet pack -o /out                -c $BUILD_CONFIGURATION --no-restore --no-build

FROM build AS buildaot
RUN dotnet nuget enable source nuget.org
RUN dotnet publish -f net10.0 Tests/Synqra.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests
RUN Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM scratch AS art
COPY --from=pack /out /

#Sync parallel builds here (should be last stage)
FROM scratch AS log
COPY --from=test /src /stage/test
COPY --from=buildaot /src /stage/buildaot

