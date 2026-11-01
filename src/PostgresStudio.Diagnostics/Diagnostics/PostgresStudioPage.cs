namespace PostgresStudio;

/// <summary>
/// A studio page a host can link to for one database (<see cref="IPostgresDiagnostics.GetStudioPath" />). May gain members.
/// </summary>
public enum PostgresStudioPage
{
    /// <summary>The Health page.</summary>
    Health = 0,

    /// <summary>Database › Sessions.</summary>
    Sessions = 1,

    /// <summary>Database › Sessions' blocking view.</summary>
    Blocking = 2,

    /// <summary>The Statements page.</summary>
    Statements = 3,

    /// <summary>The Maintenance page.</summary>
    Maintenance = 4,

    /// <summary>The Storage page.</summary>
    Storage = 5,

    /// <summary>The Server page.</summary>
    Server = 6,

    /// <summary>Query › Plan, the plan analyzer.</summary>
    Plan = 7,

    /// <summary>Database › Sessions' holders view: who holds the whole server's connections (<c>?view=holders</c>).</summary>
    Holders = 8,

    /// <summary>The Replication page.</summary>
    Replication = 9,

    /// <summary>The Overview, the studio's landing page.</summary>
    Overview = 10,
}
