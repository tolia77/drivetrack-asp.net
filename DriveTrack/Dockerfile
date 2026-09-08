# Multi-stage build of DriveTrack.Web.
# The SDK never reaches the final image: migrations run in-process at startup (AD-20),
# so the runtime needs no `dotnet ef` and no build tooling.

# The build stage is Debian, not Alpine. AD-28 compiles the theme from Sass during
# `dotnet publish`, and the dart-sass binaries the NuGet package supplies are glibc-linked -
# there is no linux-musl-x64 runtime in the package, so on Alpine the Sass step cannot run.
# Only the build stage moves: the final stage below stays Alpine, and its ICU lines are
# load-bearing (NFR-14, NFR-15).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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

# NFR-14 and NFR-15: the Alpine runtime image ships no ICU data and therefore defaults to
# globalization-invariant mode, where "uk-UA" is not a culture at all - the host fails to start
# on UseRequestLocalization. icu-libs alone is not enough: it pulls only icu-data-en, which makes
# uk-UA *resolve* while still formatting 07.09.2026 as 9/7/2026 and 1 234,5 as 1,234.5 - a silent
# NFR-15 breach in the one environment the requirement is promised in. icu-data-full carries the
# Ukrainian CLDR data. LocalizationTests pins this line, because no assertion running on a
# developer machine can observe its absence.
RUN apk add --no-cache icu-libs icu-data-full

# Non-root by construction. The image ships no writable application state; PostgreSQL and
# Garage own everything durable.
RUN addgroup -S drivetrack && adduser -S -G drivetrack drivetrack

COPY --from=build --chown=drivetrack:drivetrack /app/publish ./

USER drivetrack

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "DriveTrack.Web.dll"]
