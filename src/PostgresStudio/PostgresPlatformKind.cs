namespace PostgresStudio;

/// <summary>
/// Who runs the Postgres server, read from markers the provider documents, and used for wording only: it never raises
/// a finding or decides a severity. May gain members; treat an unknown value as <see cref="SelfHosted" />.
/// </summary>
public enum PostgresPlatformKind
{
    /// <summary>Nothing says otherwise: a server somebody runs themselves, or a provider the studio does not know.</summary>
    SelfHosted = 0,

    /// <summary>Amazon RDS for PostgreSQL.</summary>
    AmazonRds = 1,

    /// <summary>Amazon Aurora PostgreSQL-Compatible Edition.</summary>
    AmazonAurora = 2,

    /// <summary>Azure Database for PostgreSQL (flexible server).</summary>
    Azure = 3,

    /// <summary>Google Cloud SQL for PostgreSQL.</summary>
    CloudSql = 4,

    /// <summary>Neon.</summary>
    Neon = 5,

    /// <summary>Supabase.</summary>
    Supabase = 6,
}
