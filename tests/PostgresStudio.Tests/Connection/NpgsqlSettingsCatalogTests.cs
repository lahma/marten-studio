using System.ComponentModel;
using System.Reflection;

using Npgsql;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// The tests that set real <c>PG*</c> environment variables process-wide, run alone: xunit runs a collection that disables
/// parallelization after every parallel one, so no other test sees a variable set for a moment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentSuite
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Process environment (PG* variables)";
}

/// <summary>
/// NP1: the catalogue is the loaded Npgsql's keyword list and its environment table, held to the real assembly - run in
/// both Npgsql passes (the floor, 9.0.4, in the whole-solution pass; 10.0.3 in the pass over <c>postgres-studio.slnf</c>),
/// so a keyword or a <c>PG*</c> variable a newer Npgsql adds fails here rather than going unlisted.
/// </summary>
[Collection(ProcessEnvironmentSuite.Name)]
public sealed class NpgsqlSettingsCatalogTests
{
    /// <summary>The keywords Npgsql 10 added: the loaded 9.x builder does not have them.</summary>
    private static readonly string[] NewInTen = ["GSS Encryption Mode", "Include Failed Batched Command", "Require Auth"];

    private static List<string> LoadedKeywords() =>
        [.. typeof(NpgsqlConnectionStringBuilder)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static x => x.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName)
            .OfType<string>()];

    [Fact]
    public void Every_keyword_of_the_loaded_builder_is_in_the_catalogue()
    {
        LoadedKeywords().Where(static x => NpgsqlSettingsCatalog.Find(x) is null)
            .Should().BeEmpty("a keyword the catalogue does not know is shown with its value withheld, and a new Npgsql's keyword should be classified");
    }

    [Fact]
    public void Every_catalogue_keyword_exists_in_the_loaded_builder_but_for_the_three_new_in_Npgsql_10()
    {
        List<string> loaded = LoadedKeywords();
        string[] missing = [.. NpgsqlSettingsCatalog.Entries.Select(static x => x.Keyword).Where(x => !loaded.Contains(x, StringComparer.OrdinalIgnoreCase))];

        if (NpgsqlSettingsReader.LoadedMajor >= 10)
        {
            missing.Should().BeEmpty();
        }
        else
        {
            missing.Should().BeEquivalentTo(NewInTen);
        }
    }

    [Fact]
    public void The_catalogue_lists_each_keyword_once()
    {
        NpgsqlSettingsCatalog.Entries.Select(static x => x.Keyword.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Only_the_two_passwords_are_secret_and_the_four_files_are_paths()
    {
        NpgsqlSettingsCatalog.Entries.Where(static x => x.Exposure == NpgsqlSettingExposure.Secret).Select(static x => x.Keyword)
            .Should().BeEquivalentTo("Password", "SSL Password");
        NpgsqlSettingsCatalog.Entries.Where(static x => x.Exposure == NpgsqlSettingExposure.Path).Select(static x => x.Keyword)
            .Should().BeEquivalentTo("Passfile", "SSL Certificate", "SSL Key", "Root Certificate");
        NpgsqlSettingsCatalog.Entries.Where(static x => x.Exposure == NpgsqlSettingExposure.Options).Select(static x => x.Keyword)
            .Should().BeEquivalentTo("Options");
    }

    /// <summary>
    /// The environment column against the real <c>Npgsql.PostgresEnvironment</c>: each variable the catalogue lists for the
    /// loaded major is read by exactly one of its properties, and it has no property the catalogue does not list. Sets each
    /// variable to a marker and restores it at once; nothing in this fast suite opens a connection.
    /// </summary>
    [Fact]
    public void The_environment_table_is_the_one_the_loaded_Npgsql_reads()
    {
        Type environment = typeof(NpgsqlConnection).Assembly.GetType("Npgsql.PostgresEnvironment", throwOnError: true)!;
        PropertyInfo[] readers = [.. environment
            .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static x => x.PropertyType == typeof(string) && !x.Name.EndsWith("Default", StringComparison.Ordinal))];

        string[] listed = [.. NpgsqlSettingsCatalog.Entries.Select(x => x.EnvironmentFor(NpgsqlSettingsReader.LoadedMajor)).OfType<string>()];

        readers.Should().HaveCount(listed.Length, "the catalogue lists exactly the variables the loaded Npgsql reads: " + string.Join(", ", listed));

        foreach (string variable in listed)
        {
            string? before = Environment.GetEnvironmentVariable(variable);
            const string Marker = "S3NT1NEL-catalogue-check";

            try
            {
                Environment.SetEnvironmentVariable(variable, Marker);
                readers.Count(x => Marker.Equals(x.GetValue(null) as string, StringComparison.Ordinal)).Should().Be(1, variable + " is read by one property of PostgresEnvironment");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, before);
            }
        }
    }

    [Theory]
    [InlineData("Application Name", 9, null)]
    [InlineData("Application Name", 10, "PGAPPNAME")]
    [InlineData("GSS Encryption Mode", 9, null)]
    [InlineData("GSS Encryption Mode", 10, "PGGSSENCMODE")]
    [InlineData("Require Auth", 10, "PGREQUIREAUTH")]
    [InlineData("Username", 9, "PGUSER")]
    [InlineData("SSL Negotiation", 9, "PGSSLNEGOTIATION")]
    [InlineData("Target Session Attributes", 9, "PGTARGETSESSIONATTRS")]
    [InlineData("SSL Password", 10, null)]
    public void An_environment_fallback_belongs_to_the_Npgsql_major_that_reads_it(string keyword, int major, string? expected)
    {
        NpgsqlSettingsCatalog.Find(keyword)!.EnvironmentFor(major).Should().Be(expected);
    }
}
