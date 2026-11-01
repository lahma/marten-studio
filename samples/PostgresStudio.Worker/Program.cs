using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using PostgresStudio;
using PostgresStudio.SampleData;
using PostgresStudio.Worker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// No silent localhost fallback: either something configured a database, or a throwaway container is started, or the host
// refuses (D18).
string? connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = await EphemeralPostgres.StartAsync();
}

// The demo's schemas are written only into the throwaway container this process started, so there is something for the
// checks to find. A configured database is only ever read.
if (EphemeralPostgres.IsThrowaway(connectionString))
{
    await using NpgsqlConnection connection = new(connectionString);
    await RelationalDemoSchema.ApplyAsync(connection);
}

// The application's own data source - what AddNpgsqlDataSource(connectionString) from Npgsql.DependencyInjection
// registers, written out here so the sample needs no other package. The engine finds the unkeyed one by itself and lists it
// as the source "default"; it reads through it, with its role and its settings, and never holds a connection string.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

// The engine alone: no ASP.NET Core, no UI, no mounted path. Validated on start, so a bad option stops the host here
// rather than at the first read. Refused beside AddPostgresStudio() or AddMartenStudio(), which register it themselves.
builder.Services.AddPostgresDiagnostics(options =>
{
    // The schemas the checks that read objects look at; `public` is the default, named here so the line says what it does.
    options.BrowsableSchemas.Add("public");
    options.BrowsableSchemas.Add(RelationalDemoSchema.QuartzSchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.LegacySchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.SampleSchemaName);
});

// Summary:Interval (default 24 hours) and Summary:StudioBaseUrl (no links unless a host has a studio mounted).
builder.Services.Configure<SummaryOptions>(builder.Configuration.GetSection("Summary"));
builder.Services.AddHostedService<DiagnosticsSummaryService>();

await builder.Build().RunAsync();
