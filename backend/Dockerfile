# syntax=docker/dockerfile:1
# Build from the repo root: docker build -f backend/Dockerfile -t screentail-api .
# (The root holds Directory.Build.props and global.json; .dockerignore allow-lists what's copied.)
# deploy/compose.yaml builds it the same way (ST-007, docs/dev/deploy.md).
#
# Both images are pinned by version and digest, so a rebuild gets the bytes this file was tested with.
# To move to a newer patch, change the tag and the digest together.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /repo
COPY Directory.Build.props global.json .editorconfig ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/ScreenTail.Api/ScreenTail.Api.csproj backend/src/ScreenTail.Api/
RUN dotnet restore backend/src/ScreenTail.Api/ScreenTail.Api.csproj
COPY research/prompts/note_v1.md research/prompts/schema.json research/prompts/unicode-tables.json research/prompts/
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/ScreenTail.Api/ScreenTail.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# Ubuntu chiseled: the .NET runtime and nothing else. No shell, no package manager, and a non-root user
# by default. The app runs with InvariantGlobalization, so it needs no ICU. The container's health check
# is the app itself (`--health-probe`), since there is no curl to run.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScreenTail.Api.dll"]
