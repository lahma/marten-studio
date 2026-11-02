# PostgresStudio.Diagnostics

The diagnostics engine of [Postgres Studio](https://github.com/lahma/marten-studio/blob/main/src/PostgresStudio/README.md)
and [Marten Studio](https://github.com/lahma/marten-studio#readme), as a package of its own with **no ASP.NET Core in it**: the
health sweep and its checks, the facts they read, `IPostgresDiagnostics` and its typed, stable-coded read model, and an
opt-in health check built on `Microsoft.Extensions.Diagnostics.HealthChecks`. It answers "is this database healthy, and what should I look at?" for any host that
registers an `NpgsqlDataSource` - a worker, a job host, a shared library - without a UI to mount.

Take **`PostgresStudio.Diagnostics`** when you want the findings in your own code. Take
[`PostgresStudio`](https://www.nuget.org/packages/PostgresStudio) when you want the Blazor UI over the same databases, or
[`MartenStudio`](https://www.nuget.org/packages/MartenStudio) when the application uses Marten; both bring this package with
them, at exactly their own version, and you need not reference it.

```shell
dotnet add package PostgresStudio.Diagnostics
```

It brings `Npgsql` (floor `8.0.3`) and the `Microsoft.Extensions.*` abstractions it registers into
(`DependencyInjection.Abstractions`, `Logging.Abstractions`, `Options`, `Diagnostics.HealthChecks`, floor `10.0.0`), and
references no ASP.NET Core, Marten, JasperFx or Weasel package. Target framework: `net10.0`.

```csharp
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

`AddPostgresDiagnostics()` is refused in an application that also calls `AddPostgresStudio()` or `AddMartenStudio()`: they
register the engine themselves. There are no mutes without a UI, and a finding's studio path is a string relative to a
mount that does not exist here.

The long form - the request and the snapshot, findings and their codes, `IncludeActivity`, cost, the JSON shape and what it
guarantees - is
[`docs/diagnostics.md`](https://github.com/lahma/marten-studio/blob/main/docs/diagnostics.md), and a complete worker is
[`samples/PostgresStudio.Worker`](https://github.com/lahma/marten-studio/tree/main/samples/PostgresStudio.Worker).
From 1.0 the package follows Semantic Versioning over the contract in
[`docs/stability.md`](https://github.com/lahma/marten-studio/blob/main/docs/stability.md): the read model is extend-only,
and a finding's code never changes meaning.
