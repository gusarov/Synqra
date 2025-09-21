#syntax=docker/dockerfile:1-labs

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
	mc vim \
	&& rm -rf /var/lib/apt/lists/*
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY --parents **/*.*proj *.sln global.json *.targets *.props ./
RUN dotnet restore --no-cache "-clp:ErrorsOnly;NoSummary" -tl:false -nologo && dotnet nuget disable source nuget.org
COPY . .
RUN dotnet build Synqra.Utils -c $BUILD_CONFIGURATION -o /app/build --no-restore "-clp:ErrorsOnly;NoSummary" -tl:false -nologo
#RUN dotnet build -c $BUILD_CONFIGURATION -o /app/build --no-restore
#RUN dotnet test -c $BUILD_CONFIGURATION
#RUN dotnet publish Tests/Synqra.Tests -c Release -r linux-musl-x64
#RUN Tests/Synqra.Tests/bin/Release/net9.0/linux-musl-x64/publish/Synqra.Tests
