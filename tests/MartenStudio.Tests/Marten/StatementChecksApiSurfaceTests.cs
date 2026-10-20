using System.Reflection;

using Marten;
using Marten.Storage;

using MartenStudio.Tests.Sql;

using Npgsql;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// The Marten and Npgsql members T23's remedy line names, pinned the way <c>MartenApiSurfaceTest</c> pins the rest - the
/// line itself compiled here, and reflection for the facts - in a file of its own so the packet edits nothing it does not
/// own.
/// </summary>
/// <remarks>
/// Read on 2026-10-17: <c>StoreOptions.ConfigureNpgsqlDataSourceBuilder(Action&lt;NpgsqlDataSourceBuilder&gt;)</c> is public at
/// Marten V9.39.0 and V9.50.2, and wraps the data source builder factory, which a store's tenancy reads lazily - so it
/// applies whether it is called before <c>Connection(...)</c> or after. <c>NpgsqlDataSourceBuilder.ConnectionStringBuilder</c>
/// and <c>NpgsqlConnectionStringBuilder.Options</c> are in Npgsql 9.0.4, the version both ends resolve.
/// </remarks>
public class StatementChecksApiSurfaceTests
{
    [Fact]
    public void The_compile_only_probe_is_referenced_so_nothing_has_to_be_suppressed()
    {
        Action<StoreOptions> probe = CompileOnly;

        probe.Method.Name.Should().Be(nameof(CompileOnly));
    }

    [Fact]
    public void The_remedy_is_the_line_compiled_here()
    {
        JitShareCheck.RemedyCode.Should().Be(
            "opts.ConfigureNpgsqlDataSourceBuilder(b =>\n"
            + "    b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + \" -c jit=off\").Trim());",
            "the text the page offers is the C# below, which compiles against the Marten and Npgsql this build uses");
    }

    [Theory]
    [InlineData(null, "-c jit=off")]
    [InlineData("-c search_path=app", "-c search_path=app -c jit=off")]
    public void The_line_turns_jit_off_and_keeps_what_the_options_already_said(string? options, string expected)
    {
        NpgsqlDataSourceBuilder builder = new(SqlTestStore.Unreachable);
        builder.ConnectionStringBuilder.Options = options;

        Line(builder);

        builder.ConnectionStringBuilder.Options.Should().Be(expected);
    }

    [Fact]
    public void The_stores_own_data_source_is_built_with_it()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            CompileOnly(options);
        });

        // Never opened: the connection string is the data source's own.
        using NpgsqlConnection connection = store.Storage.Database.CreateConnection(ConnectionUsage.Read);
        new NpgsqlConnectionStringBuilder(connection.ConnectionString).Options.Should().Be("-c jit=off");
    }

    [Fact]
    public void The_members_the_line_names_exist_with_these_shapes()
    {
        typeof(StoreOptions).GetMethod(nameof(StoreOptions.ConfigureNpgsqlDataSourceBuilder), [typeof(Action<NpgsqlDataSourceBuilder>)])
            .Should().NotBeNull().And.Subject.As<MethodInfo>().IsPublic.Should().BeTrue();

        typeof(NpgsqlDataSourceBuilder).GetProperty(nameof(NpgsqlDataSourceBuilder.ConnectionStringBuilder))!
            .PropertyType.Should().Be<NpgsqlConnectionStringBuilder>();

        PropertyInfo options = typeof(NpgsqlConnectionStringBuilder).GetProperty(nameof(NpgsqlConnectionStringBuilder.Options))!;
        options.PropertyType.Should().Be<string>();
        options.CanWrite.Should().BeTrue();
    }

    /// <summary>The remedy's line, as C#: if a Marten or Npgsql release moves a member it names, this stops compiling.</summary>
    private static void CompileOnly(StoreOptions opts) =>
        opts.ConfigureNpgsqlDataSourceBuilder(b =>
            b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + " -c jit=off").Trim());

    /// <summary>The same line's body, to run against a builder.</summary>
    private static void Line(NpgsqlDataSourceBuilder b) =>
        b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + " -c jit=off").Trim();
}
