namespace PostgresStudio;

/// <summary>Whose design a table is. May gain members; treat an unknown value as <see cref="Other" />.</summary>
public enum PostgresTableOwnership
{
    /// <summary>The application's own: its documents, its projections, its relational tables.</summary>
    Application = 0,

    /// <summary>A recognised library's: Quartz.NET's, Wolverine's, Marten's own, EF Core's or FluentMigrator's bookkeeping.</summary>
    Library = 1,

    /// <summary>Somebody else's: neither the application's nor a recognised library's.</summary>
    Other = 2,
}
