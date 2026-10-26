using System.Globalization;
using System.Text.Json;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Json;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The database browser's Copy WHERE and row statement (<see cref="SqlLiteralText" />), and the JSON viewer's Postgres
/// expressions, run as a person would paste them, on a session that turns <c>standard_conforming_strings</c> off as well
/// as on: each one reads exactly the row or member it was copied from, whatever its key holds - quotes, backslashes, a
/// trailing backslash, and the row or member a misread backslash would land on instead.
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

    /// <summary>A member two levels down, behind a name with a trailing backslash and one with a quote.</summary>
    private static readonly JsonPathSegment[] Nested = [JsonPathSegment.ForKey("outer\\"), JsonPathSegment.ForKey("in'ner")];

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $"create table \"{Schema}\".keyed (key text primary key, n int not null)");

        await using var insert = new NpgsqlCommand(
            $"insert into \"{Schema}\".keyed (key, n) select key, n::int from unnest(@keys) with ordinality as x(key, n)",
            connection);
        insert.Parameters.AddWithValue("keys", Keys);

        (await insert.ExecuteNonQueryAsync()).Should().Be(Keys.Length);

        // One document whose members are the same keys, each holding its number as a string, and a nested member.
        Dictionary<string, object> document = new(StringComparer.Ordinal);
        for (int i = 0; i < Keys.Length; i++)
        {
            document[Keys[i]] = (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        document["outer\\"] = new Dictionary<string, string> { ["in'ner"] = "deep" };

        await ExecuteAsync(connection, $"create table \"{Schema}\".docs (data jsonb not null)");
        await using var doc = new NpgsqlCommand($"insert into \"{Schema}\".docs (data) values (@doc::jsonb)", connection);
        doc.Parameters.AddWithValue("doc", JsonSerializer.Serialize(document));

        (await doc.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>
    /// The JSON viewer's "Copy Postgres expression" and "Copy Postgres #&gt;&gt; path" (<see cref="JsonPathExpressions" />),
    /// pasted into a statement on a session with <c>standard_conforming_strings</c> off as well as on: each reads the
    /// member it names, and a member whose name would not read as itself (the backspace) is offered no expression.
    /// </summary>
    [PostgresTheory]
    [InlineData("off")]
    [InlineData("on")]
    public async Task A_copied_JSON_expression_reads_the_member_it_names(string conforming)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await ExecuteAsync(connection, "set local standard_conforming_strings = " + conforming);
        (await ScalarAsync(connection, "show standard_conforming_strings")).Should().Be(conforming);

        int read = 0;
        for (int i = 0; i < Keys.Length; i++)
        {
            JsonPathSegment[] path = [JsonPathSegment.ForKey(Keys[i])];
            string? arrows = JsonPathExpressions.ToPostgresExpression(path, asText: true);
            string? hashes = JsonPathExpressions.ToPostgresTextPath(path);

            if (!SqlLiteralText.IsPrintableText(Keys[i]))
            {
                arrows.Should().BeNull("a member named with a control character gets no expression");
                hashes.Should().BeNull();
                continue;
            }

            string expected = (i + 1).ToString(CultureInfo.InvariantCulture);
            (await ScalarAsync(connection, $"select {arrows} from \"{Schema}\".docs"))
                .Should().Be(expected, "{0} must read the member it was copied from", arrows);
            (await ScalarAsync(connection, $"select {hashes} from \"{Schema}\".docs"))
                .Should().Be(expected, "{0} must read the member it was copied from", hashes);
            read++;
        }

        read.Should().Be(Keys.Length - 1, "every key but the backspace one is printable");

        (await ScalarAsync(connection, $"select {JsonPathExpressions.ToPostgresExpression(Nested, asText: true)} from \"{Schema}\".docs"))
            .Should().Be("deep");
        (await ScalarAsync(connection, $"select {JsonPathExpressions.ToPostgresTextPath(Nested)} from \"{Schema}\".docs"))
            .Should().Be("deep");

        if (conforming == "off")
        {
            // The plain form this replaced: 'a\\b', which is what the member a\\b used to be copied as, reads the member a\b.
            (await ScalarAsync(connection, $"select data ->> 'a\\\\b' from \"{Schema}\".docs"))
                .Should().Be((Array.IndexOf(Keys, "a\\b") + 1).ToString(CultureInfo.InvariantCulture));
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
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
