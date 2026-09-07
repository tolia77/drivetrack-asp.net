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
    DriveTrack.Integration.Tests   layering, configuration and migration-pipeline tests
```

The dependency direction above is enforced by a test, not by convention: see
`tests/DriveTrack.Integration.Tests/Architecture/LayeringTests.cs`.
