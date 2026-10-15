using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Hard rule 4 for health checks: a health statement is built under <c>Internal/Sql/</c> and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// A check's gatherer hands <c>context.Sql</c> a <c>HealthQuery</c>, and a <c>HealthQuery</c> is SQL text. The
/// rule that makes the studio's SQL reviewable is that the one place an identifier becomes SQL text is the
/// builders' folder, so a check that wrote <c>new HealthQuery("select ... " + name)</c> in its own file would
/// be the second place.
/// </para>
/// <para>
/// <b>Two fences, because a regex over source has holes.</b> The first version matched three spellings of a
/// construction and missed <c>static HealthQuery Q(string s) =&gt; new(sql + s)</c>, a property
/// <c>HealthQuery Q =&gt; new(...)</c>, a <c>return new(...)</c> from a method typed <c>HealthQuery</c> and
/// <c>X.Build() with { Sql = sql + name }</c> (H1 review). So:
/// </para>
/// <list type="number">
/// <item>The <b>source</b> fence refuses the identifier <c>HealthQuery</c> itself anywhere in <c>src/</c> outside
/// <c>Internal/Sql/</c> and the runner (<c>Services/Health/HealthCheckContext.cs</c>, which must name the type
/// it takes): a check holds a query as a <c>var</c> a builder returned, and has no reason to spell the type. It
/// also refuses, everywhere outside the builders, a target-typed <c>new(</c> handed to the runner - which
/// needs no identifier - and, in the health files, a <c>with { Sql = ... }</c>. That last one is a compile
/// error anyway, because <c>HealthQuery</c>'s two properties are get-only, and a reflection fact pins that.</item>
/// <item>The <b>compiled</b> fence reads the IL of every method in <c>MartenStudio.dll</c> and refuses a
/// <c>newobj</c> of a <c>HealthQuery</c> constructor from any type outside <c>MartenStudio.Internal.Sql</c>. It
/// cannot be spelled around: a conditional, a switch expression, a local function and a lambda all compile to
/// the same instruction in a method whose outermost type is the check.</item>
/// </list>
/// <para>
/// The source scan strips comments and strings first, which is what lets prose quote the pattern - and what
/// could make it vacuous; each fence has a theory or fact that proves it still catches an offender.
/// </para>
/// </remarks>
public class HealthQueryConventionTests
{
    /// <summary>Where a health statement may be built.</summary>
    private const string Builders = "src/MartenStudio/Internal/Sql/";

    /// <summary>The runner: it takes a query, so it names the type, and it builds none.</summary>
    private const string Runner = "src/MartenStudio/Services/Health/HealthCheckContext.cs";

    private const string BuildersNamespace = "MartenStudio.Internal.Sql";

    private static readonly Regex Identifier = new(@"\bHealthQuery\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>Where the checks and the health framework live: the scope of the <c>with</c> rule.</summary>
    private const string HealthServices = "src/MartenStudio/Services/Health/";

    /// <summary>
    /// A construction, in the runner as anywhere else: <c>new HealthQuery(</c>, a target-typed <c>new(</c> into
    /// something typed <c>HealthQuery</c> (a variable, a property, a method's expression body), and a target-typed
    /// <c>new(</c> handed to the runner - by position, after any receiver, or by name - which needs no identifier.
    /// </summary>
    private static readonly Regex Construction = new(
        @"\bnew\s+HealthQuery\s*\("
        + @"|\bHealthQuery\??\s+\w+\s*(?:\([^)]*\))?\s*(?:=>|=)\s*new\s*\("
        + @"|\b(?:QueryAsync|ScalarAsync)\s*(?:<[^>]*>)?\s*\(\s*(?:query\s*:\s*)?new\s*\("
        + @"|\bquery\s*:\s*new\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A <c>with</c> that sets a query's text or parameters. Type-blind - the database browser's own page record has
    /// a <c>Sql</c> of its own - so it is asked only of the health files, and the fence that holds everywhere is
    /// that the two properties have no setter (<see cref="A_query_cannot_be_rewritten_by_a_with_expression" />).
    /// </summary>
    private static readonly Regex Rewrite = new(@"\bwith\s*\{[^}]*\b(?:Sql|Parameters)\s*=", RegexOptions.None, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_shipped_file_outside_the_builders_names_or_builds_a_health_query()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");
        files.Select(SourceScanner.Relative).Should().Contain(Runner, "the exemption must name a file that exists");

        List<string> offenders =
        [
            .. files
                .Select(static file => (Path: SourceScanner.Relative(file), Text: File.ReadAllText(file)))
                .Where(static x => Offends(x.Path, x.Text))
                .Select(static x => x.Path),
        ];

        offenders.Should().BeEmpty(
            "a health statement is SQL text, and SQL text is built in Internal/Sql/ and nowhere else (hard rule 4); a check "
            + "holds the query a builder returned in a var: " + string.Join(", ", offenders));
    }

    [Theory]
    // The four the H1 review found, plus the original three.
    [InlineData("static HealthQuery Q(string s) => new(sql + s);", true)]
    [InlineData("HealthQuery Q => new(\"select 1\");", true)]
    [InlineData("HealthQuery Build(string name) { return new(\"select \" + name); }", true)]
    [InlineData("var q = HealthTableQueries.Stats(schemas) with { Sql = sql + name };", true)]
    [InlineData("var q = HealthTableQueries.Stats(schemas) with { Parameters = [] };", true)]
    [InlineData("var q = new HealthQuery(\"select 1\");", true)]
    [InlineData("HealthQuery q = new(\"select 1\", []);", true)]
    [InlineData("HealthQuery? q = new(\"select 1\");", true)]
    [InlineData("Func<string, HealthQuery> f = s => new(s);", true)]
    [InlineData("await context.Sql.QueryAsync(new(\"select 1\"), Map, token);", true)]
    [InlineData("await sql.QueryAsync(new(\"select 1\"), Map, token);", true)]
    [InlineData("await context.Sql.ScalarAsync<long>(new(\"select 1\"), token);", true)]
    [InlineData("await context.Sql.QueryAsync(map: Map, query: new(\"select 1\"), cancellationToken: token);", true)]
    // What a check may write.
    [InlineData("var query = HealthTableQueries.Stats(schemas);\nawait context.Sql.QueryAsync(query, Map, token);", false)]
    [InlineData("await context.Sql.QueryAsync(HealthTableQueries.Stats(schemas), Map, token);", false)]
    [InlineData("var statement = previous with { Duration = elapsed };", false)]
    [InlineData("// new HealthQuery(\"select 1\") and HealthQuery Q => new(...)\nclass C { }", false)]
    [InlineData("/// <c>new HealthQuery(sql)</c>, <see cref=\"HealthQuery\" />\nclass C { }", false)]
    [InlineData("string s = \"new HealthQuery(x)\";", false)]
    [InlineData("var builder = new HealthTableQueries();", false)]
    public void The_source_fence_catches_a_query_outside_the_builders_and_ignores_prose(string source, bool offends)
    {
        Offends("src/MartenStudio/Services/Health/Checks/SomeCheck.cs", source).Should().Be(offends);
    }

    /// <summary>The builders may do anything; the runner may name the type, and may not build one.</summary>
    [Theory]
    [InlineData(Builders + "HealthTableQueries.cs", "public static HealthQuery Stats(string s) => new(\"select \" + s);", false)]
    [InlineData(Runner, "public Task<T?> ScalarAsync<T>(HealthQuery query, CancellationToken cancellationToken)", false)]
    [InlineData(Runner, "HealthQuery probe = new(\"select 1\");", true)]
    [InlineData(Runner, "var probe = new HealthQuery(\"select 1\");", true)]
    [InlineData(Runner, "await QueryAsync(new(\"select 1\"), Map, token);", true)]
    [InlineData(Runner, "private static HealthQuery Probe() => new(\"select 1\");", true)]
    [InlineData(Runner, "var copy = query with { Sql = query.Sql + suffix };", true)]
    // The with rule is the health files' only: the database browser's page record has a Sql of its own.
    [InlineData("src/MartenStudio/Services/Database/TableRowService.cs", "page = page with { Sql = statement.Sql };", false)]
    public void The_exemptions_are_exactly_the_builders_and_the_runners_parameter_types(string path, string source, bool offends)
    {
        Offends(path, source).Should().Be(offends);
    }

    /// <summary>
    /// The fence for <c>with</c> that holds everywhere, the builders' folder included: neither property has a
    /// setter, so <c>query with { Sql = query.Sql + name }</c> does not compile. Not vacuous: a positional record
    /// property's <c>init</c> accessor is a setter by reflection, as <c>HealthStatement.Sql</c>'s shows.
    /// </summary>
    [Fact]
    public void A_query_cannot_be_rewritten_by_a_with_expression()
    {
        typeof(MartenStudio.Services.Health.HealthStatement).GetProperty("Sql")!.SetMethod.Should().NotBeNull(
            "a positional record property is init-only, which reflection reports as a setter - the shape HealthQuery must not have");

        foreach (string name in new[] { nameof(HealthQuery.Sql), nameof(HealthQuery.Parameters) })
        {
            typeof(HealthQuery).GetProperty(name)!.SetMethod.Should().BeNull("HealthQuery.{0} is get-only, so no with expression can set it", name);
        }
    }

    [Fact]
    public void No_type_outside_Internal_Sql_constructs_a_health_query_in_the_compiled_assembly()
    {
        IReadOnlyList<MethodBase> sites = IlScan.Constructions(typeof(HealthQuery).Assembly, typeof(HealthQuery));

        // Not vacuous: the record's own Clone method constructs one, inside Internal/Sql - so the scan read this
        // assembly's bodies and recognised the instruction.
        sites.Should().Contain(static x => x.DeclaringType == typeof(HealthQuery),
            "the scan must see the construction every record makes of itself, or it is reading nothing");

        List<string> offenders =
        [
            .. sites
                .Where(static x => IlScan.Outermost(x.DeclaringType!).Namespace != BuildersNamespace)
                .Select(static x => x.DeclaringType!.FullName + "." + x.Name),
        ];

        offenders.Should().BeEmpty(
            "only a builder under Internal/Sql/ constructs a HealthQuery, however the construction is spelled: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The compiled fence's anti-vacuity: every spelling the source fence once missed, compiled into this test
    /// assembly, is found - and a method that only reads a query is not.
    /// </summary>
    [Fact]
    public void The_compiled_fence_finds_every_spelling_of_a_construction()
    {
        List<MethodBase> found =
        [
            .. IlScan.Constructions(typeof(HealthQueryConventionTests).Assembly, typeof(HealthQuery))
                .Where(static x => IlScan.Encloses(typeof(Offenders), x.DeclaringType!)),
        ];

        found.Select(static x => x.Name).Should().Contain([nameof(Offenders.Expression), "get_" + nameof(Offenders.Property), nameof(Offenders.Returned), nameof(Offenders.Conditional)]);
        found.Should().Contain(static x => x.DeclaringType != typeof(Offenders), "a lambda's body is a method of a compiler-generated nested type");
        found.Select(static x => x.Name).Should().NotContain(nameof(Offenders.NotAnOffender), "reading a query is not building one");
    }

    private static bool Offends(string path, string source)
    {
        if (path.StartsWith(Builders, StringComparison.Ordinal))
        {
            return false;
        }

        string code = SourceScanner.StripCommentsAndStrings(source);

        return Construction.IsMatch(code)
            || (path.StartsWith(HealthServices, StringComparison.Ordinal) && Rewrite.IsMatch(code))
            || (!string.Equals(path, Runner, StringComparison.Ordinal) && Identifier.IsMatch(code));
    }

    /// <summary>Every way the H1 review found to build a query without the original three spellings.</summary>
    private static class Offenders
    {
        private const string Select = "select 1";

        public static Func<string, HealthQuery> Lambda { get; } = static tail => new(Select + tail);

        public static HealthQuery Property => new(Select);

        public static HealthQuery Expression(string tail) => new(Select + tail);

        public static HealthQuery Returned(bool withParameters)
        {
            if (withParameters)
            {
                return new(Select, []);
            }

            return new(Select);
        }

        public static HealthQuery Conditional(bool first) => first ? new("select 1") : new("select 2");

        public static string NotAnOffender(HealthQuery query) => query.Sql;
    }

    /// <summary>Reads method bodies for one instruction: <c>newobj</c> of a given type's constructor.</summary>
    private static class IlScan
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => (OpCode) field.GetValue(null)!)
            .GroupBy(static code => code.Value)
            .ToDictionary(static group => group.Key, static group => group.First());

        /// <summary>Every method or constructor of <paramref name="assembly" /> that constructs <paramref name="target" />.</summary>
        public static List<MethodBase> Constructions(Assembly assembly, Type target)
        {
            List<MethodBase> sites = [];

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    if (method.GetMethodBody()?.GetILAsByteArray() is { } il && Constructs(method, il, target))
                    {
                        sites.Add(method);
                    }
                }
            }

            return sites;
        }

        /// <summary>The type a nested (or compiler-generated) type is declared in, all the way out.</summary>
        public static Type Outermost(Type type)
        {
            while (type.DeclaringType is { } outer)
            {
                type = outer;
            }

            return type;
        }

        /// <summary>Whether <paramref name="type" /> is <paramref name="outer" /> or nested inside it.</summary>
        public static bool Encloses(Type outer, Type type)
        {
            for (Type? each = type; each is not null; each = each.DeclaringType)
            {
                if (each == outer)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Constructs(MethodBase method, byte[] il, Type target)
        {
            int position = 0;

            while (position < il.Length)
            {
                short value = il[position] == 0xFE && position + 1 < il.Length
                    ? unchecked((short) (0xFE00 | il[++position]))
                    : il[position];
                position++;

                OpCode code = ByValue[value];

                if (code == OpCodes.Newobj
                    && Resolve(method, BitConverter.ToInt32(il, position)) is { } constructor
                    && constructor.DeclaringType == target)
                {
                    return true;
                }

                position += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                    _ => 4,
                };
            }

            return false;
        }

        private static MethodBase? Resolve(MethodBase method, int token)
        {
            try
            {
                return method.Module.ResolveMethod(
                    token,
                    method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
                // A token this context cannot resolve is not a constructor of a non-generic sealed record.
                return null;
            }
        }
    }
}
