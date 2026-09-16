# DriveTrack

Delivery management for a small courier fleet. ASP.NET Core 10, Blazor Web App
(Interactive Server), PostgreSQL 18, Garage object storage.

## Run it

Docker is the only prerequisite.

```bash
cd DriveTrack
cp .env.example .env
docker compose up
```

The app is served on <http://localhost:8080>.

The first start creates the database volume, brings PostgreSQL and Garage up, applies the
Garage cluster layout once, and applies every EF Core migration before the app accepts a
request. Later starts reuse the volumes and apply nothing.

Object storage is brought up but not yet provisioned: `objects-init` applies only the Garage
cluster layout, so no bucket and no S3 access key exist yet and
`ObjectStore__AccessKey`, `ObjectStore__SecretKey` and `ObjectStore__Bucket` stay at their
placeholder values. Creating them arrives with the proof-of-delivery story, which is the
first code that reads or writes an object. Nothing in the app calls S3 before then.

Stop with `docker compose down`; add `-v` to discard the database and object-store volumes.

## The first administrator

There is no sign-up for privileged roles, so the first administrator is provisioned from the
environment. Four variables drive it, all documented in `.env.example` and forwarded by
`compose.yaml`:

| Variable | Meaning |
|---|---|
| `Admin__Email` | The address the administrator signs in with. |
| `Admin__Password` | Their password. Change it before any deployment that is not a laptop. |
| `Admin__FirstName` | Given name, shown wherever a person is named. |
| `Admin__LastName` | Family name. |

On every start the app ensures one role row per role (`Admin`, `Dispatcher`, `Driver`,
`Client`) and then creates that administrator **only if no account already holds the
address**. Starting the stack a second time against the same volume therefore writes
nothing: there is exactly one administrator and exactly four roles however many times the
container restarts.

Leaving `Admin__Email` or `Admin__Password` blank is not an error. The roles are still
ensured, the administrator is skipped with a warning in the log, and startup continues.

Authentication itself needs `Jwt__SigningKey` — at least 32 bytes, with no committed
default. The app refuses to start without it rather than failing at the first sign-in.

## Signing out

`POST /sign-out` clears the browser's session cookie, and that is all it does. There are no
refresh tokens and no revocation list yet, so a bearer token already issued to that person —
by `POST /api/auth/sign-in` or by the same registration — keeps working until it expires on
its own. `Jwt__LifetimeMinutes` is that window; it defaults to 60. Shorten it if a signed-out
session must lose its REST access sooner. Token revocation belongs to a later epic.

## Develop against it

The .NET 10 SDK is the only extra prerequisite.

```bash
cd DriveTrack
dotnet build DriveTrack.sln
dotnet test DriveTrack.sln     # Docker must be running: the migration tests use Testcontainers
```

Configuration comes from the environment, never from a committed file. To run the app
outside the container, export at least `ConnectionStrings__Default` (see `.env.example`
for the full contract).

### The hot-reload stack

`docker compose up` runs the production image: a Release build published into a runtime-only
Alpine layer with no SDK and diagnostics switched off. That is what makes it a deployable
artifact, and it is also why it cannot hot reload — the code inside was copied in at build time
and nothing in the image can recompile it. Every edit needs a rebuild.

For UI work, add the overlay:

```bash
cd DriveTrack
docker compose -f compose.yaml -f compose.dev.yaml up
```

Same database, same object store, same `.env`. Only `app` changes: it builds from
`Dockerfile.dev`, keeps the SDK, mounts `src/` from the host and runs `dotnet watch`. The two
stacks build separate images — `drivetrack-app` and `drivetrack-app-dev` — so neither overwrites
the other, and a bare `docker compose up` is still the production stack.

What reloads, and what does not:

| Edit | In the container |
|---|---|
| `.cs`, `.razor` | applies on its own, in about three seconds |
| `.js`, `.css` | live on the server; **reload the browser** to pick it up |

`dotnet watch` pushes static-asset updates over a WebSocket whose port it picks at random inside
the container and announces to the browser as `ws://localhost:<random>`. That port cannot be
published (it changes every start) and Docker Desktop for Mac does not bridge container `localhost`
to the host, so the browser never connects and never auto-refreshes. C# and Razor edits are
unaffected because they are applied server-side and simply render differently.

A plain reload is enough — `Program.cs` drops request validators and sends `no-store` for static
assets in Development, without which `MapStaticAssets` answers `304 Not Modified` against the
build-time ETag and the browser reuses stale JavaScript through reloads, hard reloads and browser
restarts alike.

The overlay also publishes PostgreSQL on `${DB_PORT:-5432}` for `psql` and GUI clients. The test
suite does not use it; Testcontainers starts a database of its own per run.

New migrations are generated against the `DriveTrack.Infrastructure` project:

```bash
cd DriveTrack
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/DriveTrack.Infrastructure --output-dir Persistence/Migrations
```

## Layout

```text
DriveTrack/
  compose.yaml        app + PostgreSQL 18 + Garage, one named volume per stateful service
  Dockerfile          multi-stage build of DriveTrack.Web; no SDK in the runtime image
  garage.toml         Garage node config; secrets come from the environment
  src/
    DriveTrack.Domain          entities and rules; depends on nothing
    DriveTrack.Application     services, ports, DTOs; depends on Domain
    DriveTrack.Infrastructure  EF Core and adapters; depends on Application and Domain
    DriveTrack.Web             Blazor and REST adapters; Program.cs is the composition root
  tests/
    DriveTrack.Application.Tests   guard coverage, guard behaviour and validators; no Docker
    DriveTrack.Integration.Tests   layering, configuration, persistence and identity tests
```

The dependency direction above is enforced by a test, not by convention: see
`tests/DriveTrack.Integration.Tests/Architecture/LayeringTests.cs`.
