#syntax=docker/dockerfile:1-labs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS buildbase
RUN apt-get update && apt-get install -y clang zlib1g-dev
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY --parents **/*.*proj *.sln global.json *.targets *.props ./
RUN dotnet restore --no-cache "-clp:ErrorsOnly;NoSummary" -tl:false -nologo && dotnet nuget disable source nuget.org
COPY . .
RUN dotnet build -c $BUILD_CONFIGURATION --no-restore -m:1 "-clp:ErrorsOnly;NoSummary" -tl:false -nologo

FROM buildbase AS build
RUN dotnet test -f net10.0 Tests/Synqra.Tests -c $BUILD_CONFIGURATION --no-restore --no-build -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM build AS buildaot
RUN dotnet nuget enable source nuget.org
RUN dotnet publish -f net10.0 Tests/Synqra.Tests -c Release -r linux-x64
RUN chmod +777 Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests
RUN Tests/Synqra.Tests/bin/Release/net10.0/linux-x64/publish/Synqra.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"


#FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build8
#WORKDIR /src
#COPY --from=build /src .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build8
RUN apt-get update && apt-get install -y curl \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --install-dir /usr/share/dotnet --runtime dotnet --channel 8.0 \
 && rm /tmp/dotnet-install.sh
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY --parents **/*.*proj *.sln global.json *.targets *.props ./
RUN dotnet restore --no-cache "-clp:ErrorsOnly;NoSummary" -tl:false -nologo && dotnet nuget disable source nuget.org
COPY . .
RUN dotnet build -f net8.0 Tests/Synqra.Tests -c $BUILD_CONFIGURATION --no-restore -m:1 "-clp:ErrorsOnly;NoSummary" -tl:false -nologo
#RUN dotnet test -f net8.0 Tests/Synqra.Tests -c $BUILD_CONFIGURATION --no-restore --no-build -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"
#RUN dotnet publish -f net8.0 Tests/Synqra.Tests -c Release -r linux-x64
#RUN Tests/Synqra.Tests/bin/Release/net8.0/linux-x64/publish/Synqra.Tests --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build9
RUN apt-get update && apt-get install -y curl \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --install-dir /usr/share/dotnet --runtime dotnet --channel 9.0 \
 && rm /tmp/dotnet-install.sh
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY --parents **/*.*proj *.sln global.json *.targets *.props ./
RUN dotnet restore --no-cache "-clp:ErrorsOnly;NoSummary" -tl:false -nologo && dotnet nuget disable source nuget.org
COPY . .
RUN dotnet build -f net9.0 Tests/Synqra.Tests -c $BUILD_CONFIGURATION --no-restore -m:1 "-clp:ErrorsOnly;NoSummary" -tl:false -nologo


#Sync parallel builds here (should be last stage)
FROM scratch AS log
COPY --from=build8 /src /net8
COPY --from=build9 /src /net9
COPY --from=build /src /net
COPY --from=buildaot /src /netaot

#RUN dotnet test Tests/Synqra.Tests.Multitarget -c $BUILD_CONFIGURATION --no-restore --no-build
#RUN dotnet publish Tests/Synqra.Tests -c $BUILD_CONFIGURATION -r linux-musl-x64
#RUN Tests/Synqra.Tests/bin/Release/net9.0/linux-musl-x64/publish/Synqra.Tests
