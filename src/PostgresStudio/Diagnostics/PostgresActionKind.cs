namespace PostgresStudio;

/// <summary>
/// What kind of change a finding's action is, and so whose desk it goes to. The studio never runs any of them, and none is
/// ever a privilege change. May gain members; treat an unknown value as text to read before acting on it.
/// </summary>
public enum PostgresActionKind
{
    /// <summary>A line of the application's own configuration - in Marten Studio a <c>StoreOptions</c> line. Never DDL.</summary>
    ApplicationConfiguration = 0,

    /// <summary>SQL for the application's own migrations, for a table they own.</summary>
    Migration = 1,

    /// <summary>A server setting, <c>name = value</c>: <c>postgresql.conf</c>, <c>ALTER SYSTEM</c>, a parameter group.</summary>
    ServerSetting = 2,

    /// <summary>A managed platform's own control, such as Aurora's maximum ACU.</summary>
    ManagedPlatform = 3,

    /// <summary>
    /// A one-off statement for whoever runs the database to run by hand, once - data, never schema, such as a <c>setval</c>
    /// that moves a sequence past the rows it is behind.
    /// </summary>
    Repair = 4,
}
