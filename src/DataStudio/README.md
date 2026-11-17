# DataStudio

```shell
dotnet add package DataStudio
```

The shell of [Data Studio](https://github.com/lahma/marten-studio#readme): an embeddable Blazor Server admin UI that a
host mounts once. It draws the document, the layout, the rail, the capability chip, the front page, the Activity page and
the JSON viewer, and serves the studio's one stylesheet and script. It shows nothing by itself: the modules attach to it.
Every module package brings it at exactly its own version, so most hosts never reference it directly.

| Module | Package | What it is for |
|---|---|---|
| Postgres | `DataStudio.Postgres` | the PostgreSQL databases an application registers as `NpgsqlDataSource` |
| Marten | `DataStudio.Marten` | an application's own Marten store (it brings the Postgres pages over the store's databases) |
| Kafka | `DataStudio.Kafka` | the Kafka clusters an application already has credentials for (a preview) |

Install the modules you want; each module pins exactly the shell it was built with.

```csharp
using DataStudio;

builder.Services
    .AddDataStudio(studio => studio.AuthorizationPolicy = "ops")
    .AddPostgres()      // or .AddMarten(...), or .AddKafka(...): one call per module installed
    .AddKafka(kafka => kafka.AddCluster("orders", consumerConfig));

app.MapDataStudio();
```

The shell has no pages of its own but two: the front page at `/studio`, one tile per module in the order the host added
them, and Activity at `/studio/activity`; each module's pages are under its own segment (`/studio/postgres`,
`/studio/marten`, `/studio/kafka`). It references ASP.NET Core and nothing else: no database driver, no Marten, no Kafka
client. A host that adds the packages and never calls `MapDataStudio()` is the application it was.

MIT licensed. See the repository README for the rest.
