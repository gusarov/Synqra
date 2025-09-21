#syntax=docker/dockerfile:1-labs

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY --parents **/*.*proj *.sln *.targets *.props ./
RUN dotnet restore --no-cache && dotnet nuget disable source nuget.org
COPY . .
RUN dotnet build -c $BUILD_CONFIGURATION -o /app/build --no-restore
RUN dotnet test -c $BUILD_CONFIGURATION
RUN dotnet publish Tests/Synqra.Tests -c Release -r linux-musl-x64
RUN Tests/Synqra.Tests/bin/Release/net9.0/linux-musl-x64/publish/Synqra.Tests
