# DataStudio.Postgres.Diagnostics

```shell
dotnet add package DataStudio.Postgres.Diagnostics
```

The diagnostics engine of [Data Studio](https://github.com/lahma/marten-studio#readme), as a package of its own with **no
ASP.NET Core in it**: the health sweep and its checks, the facts they read, `IPostgresDiagnostics` and its typed,
stable-coded read model, and an opt-in health check built on `Microsoft.Extensions.Diagnostics.HealthChecks`. It answers
"is this database healthy, and what should I look at?" for any host that registers an `NpgsqlDataSource` - a worker, a job
host, a shared library - without a UI to mount.

Take **`DataStudio.Postgres.Diagnostics`** when you want the findings in your own code. Take
[`DataStudio.Postgres`](https://www.nuget.org/packages/DataStudio.Postgres) when you want the Blazor UI over the same
databases, or [`DataStudio.Marten`](https://www.nuget.org/packages/DataStudio.Marten) when the application uses Marten;
both bring this package with them, at exactly their own version, and you need not reference it.

It brings `Npgsql` (floor `9.0.4`) and the `Microsoft.Extensions.*` abstractions it registers into
(`DependencyInjection.Abstractions`, `Logging.Abstractions`, `Options`, `Diagnostics.HealthChecks`, floor `10.0.0`), and
references no ASP.NET Core, Marten, JasperFx or Weasel package. Target framework: `net10.0`.

```csharp
using DataStudio.Postgres;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNpgsqlDataSource(connectionString);      // found by itself, as the source "default"
builder.Services.AddPostgresDiagnostics();                   // options are validated on start

// ... in a hosted service:
await foreach (PostgresDatabaseRef db in diagnostics.ListDatabasesAsync(ct))
{
    PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(db, PostgresDiagnosticsRequest.FindingsOnly, ct);

    foreach (PostgresFinding finding in snapshot.Findings.Where(f => f.Severity <= PostgresSeverity.Warning))
    {
        Console.WriteLine($"{finding.Code} {finding.Severity}: {finding.Title}");
    }
}
```

`AddPostgresDiagnostics()` is refused in an application that also calls `AddPostgres()` or `AddMarten()` on the Data Studio
builder: they register the engine themselves. There are no mutes without a UI, and a finding's studio path is a string
relative to a mount that does not exist here (`postgres/health?…`; a host that has a studio mounted joins its own mount to
it).

The long form - the request and the snapshot, findings and their codes, `IncludeActivity`, cost, the JSON shape and what it
guarantees - is
[`docs/diagnostics.md`](https://github.com/lahma/marten-studio/blob/main/docs/diagnostics.md), and a complete worker is
[`samples/DataStudio.Sample.Postgres.Worker`](https://github.com/lahma/marten-studio/tree/main/samples/DataStudio.Sample.Postgres.Worker).
A release of any size may break the public API, the read model included; what is promised is in
[`docs/stability.md`](https://github.com/lahma/marten-studio/blob/main/docs/stability.md): a finding's code never changes
meaning.
