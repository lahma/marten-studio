using PostgresStudio.Internal.Sql;

namespace MartenStudio.Services.Tenants;

/// <summary>
/// The text of a list partition's bound for one tenant, as Postgres writes it with <c>pg_get_expr(relpartbound, 0)</c>
/// (<c>FOR VALUES IN ('acme')</c>), and the way back: the studio builds the texts it asks about and reads back the one value of each
/// bound that matched. Neither direction ever names a partition.
/// </summary>
internal static class TenantBoundText
{
    private const string Prefix = "FOR VALUES IN ('";

    /// <summary>
    /// The bound texts a partition of <paramref name="tenantId" /> may have: the id with its quotes doubled, and - when it holds a
    /// backslash - again with the backslashes doubled, for a server with <c>standard_conforming_strings = off</c>.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    public static IReadOnlyList<string> Variants(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        string quoted = tenantId.Replace("'", "''", StringComparison.Ordinal);
        string first = Prefix + quoted + "')";

        return tenantId.Contains('\\', StringComparison.Ordinal)
            ? [first, Prefix + quoted.Replace("\\", "\\\\", StringComparison.Ordinal) + "')"]
            : [first];
    }

    /// <summary>
    /// The tenant of <paramref name="bound" /> among <paramref name="tenants" />, or <see langword="null" />: the bound is read again with
    /// <see cref="PartitionBoundText.SingleListValue" /> and kept only where its one value is exactly one of the tenants, ordinally - so
    /// a bound of several values, or of a text that merely looks alike, matches nobody.
    /// </summary>
    /// <param name="bound">What Postgres returned.</param>
    /// <param name="tenants">The tenants asked about.</param>
    public static string? TenantOf(string? bound, IReadOnlySet<string> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        return PartitionBoundText.SingleListValue(bound) is { } value && tenants.Contains(value) ? value : null;
    }
}
