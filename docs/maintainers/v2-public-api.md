# The public API of 2.0

The whole public surface of DataStudio 2.0, with each type's 1.x name beside it. It is the target of the 2.0 plan
(`.alta/plans/2026-11-16-datastudio-v2.md`, step 4); until that step lands, the code still has the 1.x names. A member
that is not listed keeps its name, its type and its default.

Two rules place every type:

- **A type lives in its assembly's root namespace.** The two Postgres assemblies share `DataStudio.Postgres` and the
  three Kafka assemblies share `DataStudio.Kafka`, so a host never needs a `using` that names an engine.
- **The calls a host makes on the shell's builder live in `DataStudio`**, whichever assembly declares them: one
  `using DataStudio;` and every installed module's `Add…` is there.

## `DataStudio` (the shell)

| 2.0 | 1.x | Notes |
|---|---|---|
| `DataStudio.DataStudioServiceCollectionExtensions.AddDataStudio(this IServiceCollection, Action<DataStudioOptions>? = null)` → `DataStudioBuilder` | `AddMartenStudio`, `AddPostgresStudio` | Once per application. |
| `DataStudio.DataStudioEndpointRouteBuilderExtensions.MapDataStudio(this IEndpointRouteBuilder)` and `MapDataStudio(…, string pattern)` → `IEndpointConventionBuilder` | `MapMartenStudio`, `MapPostgresStudio` | The pattern wins over `DataStudioOptions.Path`. |
| `DataStudio.DataStudioBuilder` | new | Sealed; one public member, `IServiceCollection Services`. |
| `DataStudio.DataStudioOptions` | the shell half of `MartenStudioOptions` and `PostgresStudioOptions` | Below. |

`DataStudioOptions`:

| Member | Default | 1.x |
|---|---|---|
| `string Path` | `"/studio"` | `Path` (`"/marten"`, `"/postgres"`) |
| `string Title` | `"Data Studio"` | `Title` |
| `string? ApplicationPath` | `null` | `ApplicationPath` |
| `string? AuthorizationPolicy` | `null` | `AuthorizationPolicy` |
| `bool ReadOnly` | `false` | `ReadOnly`: turns every module's capabilities off |
| `TimeSpan RefreshInterval` | 5 s | `RefreshInterval` |

## `DataStudio.Postgres` (the module)

| 2.0 | 1.x | Notes |
|---|---|---|
| `DataStudio.PostgresDataStudioBuilderExtensions.AddPostgres(this DataStudioBuilder, Action<PostgresOptions>? = null)` → `DataStudioBuilder` | `AddPostgresStudio` | Refused beside `AddMarten()`. |
| `DataStudio.Postgres.PostgresOptions : PostgresDiagnosticsOptions` | `PostgresStudioOptions` | Loses `Path`, `Title`, `ApplicationPath`, `AuthorizationPolicy` and `RefreshInterval` (the shell's). Keeps `ReadOnly` (this module only), `Capabilities`, `SourceAuthorizationPolicy`, `WriteAuthorizationPolicy`, the page sizes and limits, `AddDataSource`, `AddDatabaseSource`. |
| `DataStudio.Postgres.PostgresCapabilities` | `PostgresStudioCapabilities` | Same four flags and `All()`. |
| `DataStudio.Postgres.PostgresResource` | `PostgresStudioResource` | Same three members. |

## `DataStudio.Postgres.Diagnostics` (the engine, namespace `DataStudio.Postgres`)

| 2.0 | 1.x | Notes |
|---|---|---|
| `AddPostgresDiagnostics(this IServiceCollection, …)` | same | Refused beside the shell's `AddPostgres()` or `AddMarten()`, as in 1.x. |
| `AddPostgresDiagnostics(this IHealthChecksBuilder, …)` | `AddPostgresStudio(this IHealthChecksBuilder, …)` | The health-check export. |
| `AddPostgresConnectionHistory`, `PostgresConnectionHistoryOptions`, `IPostgresConnectionHistoryStore`, `PostgresConnectionSample` | same | |
| `PostgresDiagnosticsOptions`, `IPostgresDiagnostics`, `IPostgresDatabaseSource`, `IPostgresCapacitySource` and every record and enum of the read model | same | Only the namespace changes. |
| `PostgresPageLink` | `PostgresStudioLink` | Its path is relative to the Postgres module's root, not to the mount. |
| `PostgresPage` | `PostgresStudioPage` | |

## `DataStudio.Marten`

| 2.0 | 1.x | Notes |
|---|---|---|
| `DataStudio.MartenDataStudioBuilderExtensions.AddMarten(this DataStudioBuilder, Action<MartenOptions>? = null)` → `DataStudioBuilder` | `AddMartenStudio` | Registers the Postgres module over the stores' databases itself. |
| `DataStudio.Marten.MartenOptions` | `MartenStudioOptions` | Loses the same five shell members. `MaxInlineDocumentBytes` becomes `MaxInlineValueBytes`, as the Postgres module spells it. Everything else keeps its name. |
| `DataStudio.Marten.MartenCapabilities` | `MartenStudioCapabilities` | Same fourteen flags and `All()`. |
| `DataStudio.Marten.MartenStoreResource` | same | |
| `DataStudio.Marten.ProjectionLagThreshold` | same | |
| `AddMartenDiagnostics(this IHealthChecksBuilder, …)` | `AddMartenStudio(this IHealthChecksBuilder, …)` | In `DataStudio.Marten`. |

## `DataStudio.Kafka` (the module, with `DataStudio.Kafka.Core` and `DataStudio.Kafka.Wolverine`)

| 2.0 | 1.x | Notes |
|---|---|---|
| `DataStudio.KafkaDataStudioBuilderExtensions.AddKafka(this DataStudioBuilder, Action<KafkaOptions>? = null)` → `DataStudioBuilder` | `AddKafkaStudio` | Needs no database module. |
| `DataStudio.Kafka.KafkaOptions : KafkaEngineOptions` | `KafkaStudioOptions` | Same members. |
| `DataStudio.Kafka.KafkaCapabilities` | `KafkaStudioCapabilities` | Same four flags, `All()` and `None()`. |
| `DataStudio.Kafka.KafkaTopicResource` | same | |
| every public type of the engine (`KafkaEngineOptions`, the builders, `KafkaScanLimits`, `KafkaProfile`, the decoder and index seams) | same | Only the namespace changes, from `KStudio`. |
| `UseWolverineKafka`, `KafkaWolverineOptions` | same | Only the namespace changes. |

## What has no successor

- `MapKafkaStudio` never existed, and no module has a map call: `MapDataStudio()` serves every module.
- A module's own mount path, title and entry policy. A host that mounted two studios under two paths mounts one shell
  and gets a segment per module.
