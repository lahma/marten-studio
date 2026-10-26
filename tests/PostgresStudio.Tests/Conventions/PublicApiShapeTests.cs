using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

using Microsoft.AspNetCore.Components;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// D31's extend-only rules, by reflection over the exported types (API design §5.2): every public record is sealed and
/// non-positional, so a host builds one with an object initializer and a new property breaks nothing; no public type has a
/// <c>required</c> member, which would break every host that builds one in a test the day a member is added; every public
/// enum member has its value written down, so a member added in the middle cannot renumber the rest; and the three
/// interfaces' abstract members are exactly the 0.5 set - a later member must be a default interface member, so a host's own
/// implementation keeps compiling.
/// </summary>
public class PublicApiShapeTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// PS-3's registration record, positional since 0.5's first baseline: it is the resource an authorization handler is
    /// handed, never built by a host, and changing its shape now would be the breaking change this test exists to prevent.
    /// </summary>
    private static readonly Type[] PositionalByDesign = [typeof(PostgresStudioResource)];

    private static Type[] Exported() =>
    [
        .. typeof(PostgresStudioOptions).Assembly.GetExportedTypes()
            .Where(static type => !typeof(IComponent).IsAssignableFrom(type) && type.Name != "_Imports")
            .OrderBy(static type => type.FullName, StringComparer.Ordinal),
    ];

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

    [Fact]
    public void Every_public_record_is_sealed_and_non_positional()
    {
        Type[] records = [.. Exported().Where(IsRecord)];
        records.Should().HaveCountGreaterThan(15, "the read model's records are found, or this checks nothing");

        foreach (Type record in records)
        {
            record.IsSealed.Should().BeTrue(record.Name + " is sealed: a host cannot derive from a result type");

            if (!PositionalByDesign.Contains(record))
            {
                record.GetMethod("Deconstruct").Should().BeNull(
                    record.Name + " is non-positional: a positional record's constructor is its shape, and a new property would change it");
            }
        }
    }

    [Fact]
    public void A_result_record_is_built_by_an_initializer_and_its_properties_are_init_only()
    {
        foreach (Type record in Exported().Where(IsRecord).Where(static x => !PositionalByDesign.Contains(x)))
        {
            foreach (PropertyInfo property in record.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(static x => x.Name != "EqualityContract"))
            {
                MethodInfo? setter = property.SetMethod;

                if (setter is not null && setter.IsPublic)
                {
                    setter.ReturnParameter.GetRequiredCustomModifiers().Should().Contain(
                        typeof(IsExternalInit),
                        record.Name + "." + property.Name + " is init, never set: a snapshot does not change once returned");
                }
            }
        }
    }

    [Fact]
    public void No_public_type_has_a_required_member()
    {
        foreach (Type type in Exported())
        {
            type.GetCustomAttribute<RequiredMemberAttribute>().Should().BeNull(
                type.Name + " has no required member: a host's own test that builds one must keep compiling when a member is added");

            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                member.GetCustomAttribute<RequiredMemberAttribute>().Should().BeNull(type.Name + "." + member.Name + " is not required");
            }
        }
    }

    [Fact]
    public void Every_public_enum_member_has_an_explicit_value()
    {
        Type[] enums = [.. Exported().Where(static x => x.IsEnum)];
        enums.Should().HaveCountGreaterThanOrEqualTo(6, "the read model's five enums and the platform are found");

        foreach (Type type in enums)
        {
            string body = EnumBody(type);
            string[] members = Members(body);

            members.Should().BeEquivalentTo(Enum.GetNames(type), type.Name + "'s source is the one that compiled");
            members.Should().OnlyContain(
                x => Regex.IsMatch(body, @"\b" + Regex.Escape(x) + @"\s*=\s*[0-9]+\s*,?", RegexOptions.None, RegexTimeout),
                type.Name + "'s every member says its value, so a member added between two cannot renumber the rest");
        }
    }

    [Fact]
    public void The_three_interfaces_abstract_members_are_the_0_5_set()
    {
        // A member added later must have a body - a default interface member - so that a host's implementation of the
        // interface, in its own tests or its own database source, keeps compiling. Add it to this list only if it has one.
        Abstract(typeof(IPostgresDiagnostics)).Should().Equal("GetSnapshotAsync", "ListDatabasesAsync", "get_Checks");
        Abstract(typeof(IPostgresDatabaseSource)).Should().Equal("GetDataSourceAsync", "ListAsync");
        Abstract(typeof(IPostgresCapacitySource)).Should().Equal("GetCapacityAsync");

        Exported().Where(static x => x.IsInterface).Select(static x => x.Name)
            .Should().BeEquivalentTo(["IPostgresDiagnostics", "IPostgresDatabaseSource", "IPostgresCapacitySource"]);

        static string[] Abstract(Type type) =>
        [
            .. type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(static x => x.IsAbstract)
                .Select(static x => x.Name).Order(StringComparer.Ordinal),
        ];
    }

    // -----------------------------------------------------------------------------------------------
    // The enum reader, and its anti-vacuity: an implicit member is caught, comments are not members
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{ A = 0, B = 1 }", true)]
    [InlineData("{\n    /// <summary>A.</summary>\n    A = 0,\n\n    // B = 7 was retired\n    B = 1,\n}", true)]
    [InlineData("{ A = 0, B }", false)]
    [InlineData("{ A, B = 1 }", false)]
    public void The_enum_reader_finds_an_implicit_member_and_ignores_comments(string body, bool explicitValues)
    {
        string stripped = Strip(body);
        Members(stripped).Should().Equal("A", "B");
        Members(stripped).All(x => Regex.IsMatch(stripped, @"\b" + x + @"\s*=\s*[0-9]+", RegexOptions.None, RegexTimeout)).Should().Be(explicitValues);
    }

    private static string EnumBody(Type type)
    {
        string path = Directory
            .EnumerateFiles(RepositoryRoot.Combine("src", "PostgresStudio"), type.Name + ".cs", SearchOption.AllDirectories)
            .Single(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        string source = Strip(File.ReadAllText(path));
        int declaration = source.IndexOf("enum " + type.Name, StringComparison.Ordinal);
        declaration.Should().BeGreaterThanOrEqualTo(0, type.Name + " is declared in its own file");

        int open = source.IndexOf('{', declaration);
        int close = source.IndexOf('}', open);
        return source[open..(close + 1)];
    }

    private static string Strip(string source) =>
        Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline, RegexTimeout);

    private static string[] Members(string body) =>
    [
        .. body.Trim('{', '}', ' ', '\n', '\r')
            .Split(',')
            .Select(static x => x.Split('=')[0].Trim())
            .Where(static x => x.Length > 0),
    ];
}
