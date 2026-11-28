using Npgsql;

namespace DataStudio.Postgres.Services.Connection;

/// <summary>Whether a connection string asks Npgsql to multiplex, read by keyword so that one build runs on Npgsql 9, 10 and 11.</summary>
internal static class NpgsqlMultiplexing
{
    /// <summary>The connection string keyword Npgsql 9 and 10 know and Npgsql 11 does not.</summary>
    internal const string Keyword = "Multiplexing";

    /// <summary>
    /// Whether <paramref name="builder" /> has <c>Multiplexing</c> set to <see langword="true" />.
    /// </summary>
    /// <remarks>
    /// Read through the keyword and never through <c>NpgsqlConnectionStringBuilder.Multiplexing</c>: Npgsql 11.0.0 removed that
    /// property, so a compiled reference to it throws <see cref="MissingMethodException" /> on a host that takes Npgsql 11.
    /// <c>TryGetValue</c> is the same on every Npgsql: it answers <see langword="false" /> for a keyword the loaded Npgsql does
    /// not know, and a connection string that spells <c>Multiplexing</c> does not parse on Npgsql 11, so a data source there
    /// never carries it and the answer is <see langword="false" />.
    /// </remarks>
    internal static bool Multiplexes(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.TryGetValue(Keyword, out object? value) && value is true;
    }
}
