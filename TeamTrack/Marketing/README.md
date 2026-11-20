# Marketing Lead Desk (feature-flagged)

API under `/api/marketing` (role `Marketing`), requests in `Marketing.http`. On with `Features:Marketing=true`
(already set in `appsettings.Development.json`). With the flag off nothing is registered and the routes return 404.

## Local setup (Development)
Keep local settings in user secrets, never in `appsettings.json`:

    dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=teamtrack_dev;Username=...;Password=..." --project TeamTrack
    dotnet user-secrets set "Seed:MarketingDemoPassword" "..." --project TeamTrack

On startup the `mk_*` tables are created (`CREATE ... IF NOT EXISTS`) and, in Development only, the demo user
(`marketing.demo@example.com`, skipped with a warning if the password is not set) and one demo lead are seeded.
The first log lines show which database is in use: `Database: <host>/<database>` (never the password).

## Running `dotnet ef` safely
`appsettings.json` still holds the shared (RDS) connection string, so a wrong run silently targets it.

- `dotnet ef` runs the **already built** `bin/Debug/net8.0/TeamTrack.dll`. If the app is running, that folder is locked,
  the build cannot refresh it and ef uses the OLD build. An old build made before the `UserSecretsId` was added has no
  user secrets, so it falls back to `appsettings.json`. Stop the app and rebuild first.
- Always check the target before changing anything (this command does not connect to the database):

      dotnet ef dbcontext info --context AppDbContext

  and only continue if it says `Database name: teamtrack_dev` and `Data source: tcp://localhost:5432`.
- `RestructureProductHierarchy` has no designer file, so ef does not see it. The other five migrations apply normally.
