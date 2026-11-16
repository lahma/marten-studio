# DataStudio

The shell of Data Studio: an embeddable Blazor Server admin UI that a host mounts once. It draws the document, the
layout, the rail, the capability chip, the Activity page and the JSON viewer, and serves the studio's one stylesheet and
script. It shows nothing by itself: the modules attach to it (`DataStudio.Postgres`, `DataStudio.Marten`,
`DataStudio.Kafka`).

It references ASP.NET Core and nothing else: no database driver, no Marten, no Kafka client.

MIT licensed. See the repository README for how a host registers the studio.
