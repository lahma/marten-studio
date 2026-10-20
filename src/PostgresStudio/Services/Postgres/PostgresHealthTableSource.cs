using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IHealthTableSource" />: the tables of a plain database the table checks judge.
/// </summary>
/// <remarks>A minimal body by PS-3a - an empty set; PS-3b owns the implementation (issue #8, G1).</remarks>
internal sealed class PostgresHealthTableSource : IHealthTableSource
{
    /// <inheritdoc />
    public Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult<IStudioTableSet>(EmptyTableSet.Instance);
    }

    /// <summary>A table set with no tables in it.</summary>
    private sealed class EmptyTableSet : IStudioTableSet
    {
        public static EmptyTableSet Instance { get; } = new();

        public IReadOnlyList<StudioTable> Tables => [];

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) => null;

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
    }
}
