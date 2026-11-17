# Data Studio for Apache Kafka®

```shell
dotnet add package DataStudio.Kafka --prerelease
dotnet add package DataStudio.Kafka.Wolverine --prerelease   # for a Wolverine application's clusters and topics
```

The Kafka module of [Data Studio](https://github.com/lahma/marten-studio#readme): a Kafka section inside your own ASP.NET
Core application and over its own Kafka credentials, beside the Marten or Postgres pages or alone. Find a record by key,
header, CloudEvents attribute or value; read it with redaction applied; see the records related to it across topics; and
jump to the Marten document it refers to and back. A read never writes: no topic is created, no offset committed and no
group joined.

- `DataStudio.Kafka`: the pages. `services.AddDataStudio().AddKafka(k => k.AddCluster("orders", consumerConfig))`, beside
  `.AddMarten()` or `.AddPostgres()` or alone; `app.MapDataStudio()` serves the pages, at `/studio/kafka`. It brings the shell
  (`DataStudio`) and the engine, and references no database module, no diagnostics engine and no Npgsql.
- `DataStudio.Kafka.Core`: the engine, with no ASP.NET Core, over Confluent.Kafka (it comes with `DataStudio.Kafka`).
- `DataStudio.Kafka.Wolverine`: `k.UseWolverineKafka()` lists a Wolverine application's clusters and topics.

Every capability is off by default (`SearchRecords`, `ReadPayloads`, `ExportRecords`, `LiveTail`), and each is asked of
your policies per topic. See the documentation for authorization, redaction and limits.

This is a preview: its API and screens may change in a minor release.

Apache Kafka is a trademark of the Apache Software Foundation. Data Studio is not affiliated with or endorsed by the Apache
Software Foundation.
