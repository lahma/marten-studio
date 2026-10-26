using System.Globalization;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The database browser's Copy WHERE and row statement (<see cref="SqlLiteralText" />), run as a person would paste them,
/// on a session that turns <c>standard_conforming_strings</c> off as well as on: each one reads exactly the row it was
/// copied from, whatever its key holds - quotes, backslashes, a trailing backslash, and the row a misread backslash would
/// land on instead.
/// </summary>
public class SqlLiteralTextLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>
    /// Every key, and its row's number (its position here, from 1). <c>a</c> followed by a backspace is the row the plain
    /// <c>'a\b'</c> lands on with the setting off, and <c>a\b</c> the row the plain <c>'a\\b'</c> does.
    /// </summary>
    private static readonly string[] Keys =
    [
        "plain", "O'Brien", "'", "''", "it's",
        "a\\b", "a\\\\b", "a\b",
        "\\", "\\\\", "\\'", "it\\'s", "trailing\\", "\\''\\",
    ];

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $"create table \"{Schema}\".keyed (key text primary key, n int not null)");

        await using var insert = new NpgsqlCommand(
            $"insert into \"{Schema}\".keyed (key, n) select key, n::int from unnest(@keys) with ordinality as x(key, n)",
            connection);
        insert.Parameters.AddWithValue("keys", Keys);

        (await insert.ExecuteNonQueryAsync()).Should().Be(Keys.Length);
    }

    [PostgresTheory]
    [InlineData("off")]
    [InlineData("on")]
    public async Task A_copied_where_clause_reads_exactly_the_row_it_came_from(string conforming)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await ExecuteAsync(connection, "set local standard_conforming_strings = " + conforming);
        (await ScalarAsync(connection, "show standard_conforming_strings")).Should().Be(conforming);

        for (int i = 0; i < Keys.Length; i++)
        {
            KeyValuePair<string, string?>[] key = [new("key", Keys[i])];

            string where = SqlLiteralText.WhereClause(key)!;
            (await NumbersAsync(connection, $"select n from \"{Schema}\".keyed\n" + where))
                .Should().ContainSingle("the WHERE copied from row {0} ({1}) must read that row and no other", i + 1, where)
                .Which.Should().Be(i + 1, "the WHERE copied from row {0} ({1}) must read that row and no other", i + 1, where);

            string statement = SqlLiteralText.RowStatement(Schema, "keyed", key)!;
            (await NumbersAsync(connection, statement))
                .Should().ContainSingle("the row statement for row {0} must read one row", i + 1)
                .Which.Should().Be(i + 1, "the row statement for row {0} must read that row and no other", i + 1);
        }

        if (conforming == "off")
        {
            // What the plain form this replaced read here, so the setting is shown to be in force: 'a\\b', which is what
            // the row a\\b used to be copied as, reads the row a\b - another row, silently.
            (await NumbersAsync(connection, $"select n from \"{Schema}\".keyed where key = 'a\\\\b'"))
                .Should().ContainSingle().Which.Should().Be(Array.IndexOf(Keys, "a\\b") + 1);
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<int>> NumbersAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        List<int> numbers = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            numbers.Add(Convert.ToInt32(reader.GetValue(reader.GetOrdinal("n")), CultureInfo.InvariantCulture));
        }

        return numbers;
    }
}
