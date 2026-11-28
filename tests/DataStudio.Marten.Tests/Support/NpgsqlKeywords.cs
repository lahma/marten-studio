using Npgsql;

namespace DataStudio.Marten.Tests;

/// <summary>Which connection string keywords the Npgsql this run loaded knows: the suites run at Npgsql 9, 10 and 11.</summary>
internal static class NpgsqlKeywords
{
    /// <summary>The reason a test that sets <c>Multiplexing</c> gives when it is skipped.</summary>
    public const string MultiplexingRemoved = "Npgsql 11 removed Multiplexing";

    /// <summary>
    /// Whether a connection string builder of the loaded Npgsql accepts <c>Multiplexing</c> (Npgsql 11.0.0 removed it), worked
    /// out once.
    /// </summary>
    public static bool KnowsMultiplexing { get; } = AcceptsMultiplexing();

    /// <summary>Skips the running test unless the loaded Npgsql knows <c>Multiplexing</c>.</summary>
    public static void SkipUnlessMultiplexingIsKnown() => Assert.SkipUnless(KnowsMultiplexing, MultiplexingRemoved);

    private static bool AcceptsMultiplexing()
    {
        try
        {
            _ = new NpgsqlConnectionStringBuilder("Host=probe;Multiplexing=true");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
