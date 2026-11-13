# KStudio for Apache Kafka®

A Kafka section for Marten Studio or Postgres Studio, inside your own ASP.NET Core application and over its own Kafka
credentials. Find a record by key, header, CloudEvents attribute or value; read it with redaction applied; see the
records related to it across topics; and jump to the Marten document it refers to and back. A read never writes: no
topic is created, no offset committed and no group joined.

Install: `dotnet add package KStudio --prerelease`, and `dotnet add package KStudio.Wolverine --prerelease` for a Wolverine
application (`KStudio.Core` comes with `KStudio`).

- `KStudio`: the pages. `services.AddKafkaStudio(k => k.AddCluster("orders", consumerConfig))` after `AddMartenStudio()`
  or `AddPostgresStudio()`.
- `KStudio.Core`: the engine, with no ASP.NET Core, over Confluent.Kafka.
- `KStudio.Wolverine`: `k.UseWolverineKafka()` lists a Wolverine application's clusters and topics.

Every capability is off by default (`SearchRecords`, `ReadPayloads`), and each is asked of your policies per topic.
See the documentation for authorization, redaction and limits.

This is a preview: its API and screens may change in a minor release.

Apache Kafka is a trademark of the Apache Software Foundation. KStudio is not affiliated with or endorsed by the Apache
Software Foundation.
