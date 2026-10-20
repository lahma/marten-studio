using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IHealthTableRemedies" />: the migration SQL a DBA would hand to whoever owns the schema.
/// </summary>
/// <remarks>A minimal body by PS-3a - no remedy for any table; PS-3b owns the implementation (issue #8, G12).</remarks>
internal sealed class PostgresHealthTableRemedies : IHealthTableRemedies
{
    /// <inheritdoc />
    public HealthRemedy? IndexTheFilteredMember(StudioTable table) => null;

    /// <inheritdoc />
    public HealthRemedy? IndexTheUnnamedMember(StudioTable table) => null;

    /// <inheritdoc />
    public HealthRemedy? RebuildInvalidIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin) => null;

    /// <inheritdoc />
    public HealthRemedy? RemoveRedundantIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, HealthIndexRow coveredBy) => null;

    /// <inheritdoc />
    public HealthRemedy? RemoveUnusedIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin) => null;

    /// <inheritdoc />
    public HealthRemedy? LeaveRoomForHotUpdates(StudioTable table, int fillFactor) => null;

    /// <inheritdoc />
    public HealthRemedy? IndexAColumnInPlaceOfAnExpression(StudioTable table) => null;

    /// <inheritdoc />
    public HealthRemedy? ReenableAutovacuum(StudioTable table) => null;

    /// <inheritdoc />
    public HealthRemedy? AutovacuumSetting(StudioTable table, string parameter, string value, string explanation) => null;
}
