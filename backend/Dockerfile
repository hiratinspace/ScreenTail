# syntax=docker/dockerfile:1
# Build from the repo root: docker build -f backend/Dockerfile -t screentail-api .
# (The root holds Directory.Build.props and global.json; .dockerignore allow-lists what's copied.)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props global.json .editorconfig ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/ScreenTail.Api/ScreenTail.Api.csproj backend/src/ScreenTail.Api/
RUN dotnet restore backend/src/ScreenTail.Api/ScreenTail.Api.csproj
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/ScreenTail.Api/ScreenTail.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Non-root user provided by the .NET images.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScreenTail.Api.dll"]
