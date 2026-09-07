# Multi-stage build of DriveTrack.Web.
# The SDK never reaches the final image: migrations run in-process at startup (AD-20),
# so the runtime needs no `dotnet ef` and no build tooling.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore against the project graph only, so a source-only edit reuses the restore layer.
COPY Directory.Build.props ./
COPY src/DriveTrack.Domain/DriveTrack.Domain.csproj src/DriveTrack.Domain/
COPY src/DriveTrack.Application/DriveTrack.Application.csproj src/DriveTrack.Application/
COPY src/DriveTrack.Infrastructure/DriveTrack.Infrastructure.csproj src/DriveTrack.Infrastructure/
COPY src/DriveTrack.Web/DriveTrack.Web.csproj src/DriveTrack.Web/
RUN dotnet restore src/DriveTrack.Web/DriveTrack.Web.csproj

COPY src/ src/
RUN dotnet publish src/DriveTrack.Web/DriveTrack.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# Non-root by construction. The image ships no writable application state; PostgreSQL and
# Garage own everything durable.
RUN addgroup -S drivetrack && adduser -S -G drivetrack drivetrack

COPY --from=build --chown=drivetrack:drivetrack /app/publish ./

USER drivetrack

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

ENTRYPOINT ["dotnet", "DriveTrack.Web.dll"]
