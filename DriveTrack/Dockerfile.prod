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
# No --no-restore here, deliberately. The restore above runs against the project graph before
# src/ is copied, so at that point the project has no .razor files and the static web assets
# state it writes omits the Blazor framework scripts. Publishing from that state produces an
# endpoints manifest without _framework/blazor.web.js: MapStaticAssets then 404s it, and because
# AD-14 turns prerendering off there is no server-rendered fallback - every page serves 200 with
# an empty body. Letting publish restore again costs a few seconds against an already-warm
# package cache and is the only reason the UI renders at all.
RUN dotnet publish src/DriveTrack.Web/DriveTrack.Web.csproj \
    --configuration Release \
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
# krb5-libs is not load-bearing and buys no functionality: Npgsql probes for GSSAPI when it
# opens a connection, musl ships no krb5, and the failed probe prints "Cannot load library
# libgssapi_krb5.so.2" to stdout ahead of the first migration line - two lines that read like a
# fatal startup error in `docker compose up` and are not one. The connection authenticates with
# a password and never negotiates Kerberos; the package is here so the probe resolves quietly.
RUN apk add --no-cache icu-libs icu-data-full krb5-libs

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
