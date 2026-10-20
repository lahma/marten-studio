using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The three doors the statement checks opened, fenced so that nothing else walks through them: the read as the store's
/// own role (<c>HealthSqlRunner.QueryAsApplicationRoleAsync</c>, and the session field behind it), the query whose
/// "Show SQL" is not its own text (<c>HealthQuery(sql, parameters, shown)</c>), and the one statement run outside a
/// transaction (<c>HealthSqlRunner.ExecuteOutsideTransactionAsync</c>, T02's second try at its deallocate).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why each needs a fence.</b> Every other health read runs as <c>SqlConsoleRole</c>, which is how a host narrows
/// what the studio can see; the application-role read departs from that for <c>pg_stat_statements</c> alone (R1
/// decision 3), and a second caller handing it a catalog read would be that read run past the host's narrowing. A
/// query with a <c>shown</c> text prints something other than what it runs, and a second builder using it would make
/// "Show SQL" say less than the truth about a statement the studio wrote.
/// </para>
/// <para>
/// <b>Source and IL, as <c>HealthQueryConventionTests</c> does it.</b> The source fence holds the shape - only
/// <c>StatementReads</c> names the method, and only as a call whose first argument is <c>HealthStatementQueries</c>'
/// <c>Counters(</c> or <c>Texts(</c> - and the IL fence holds what a source regex cannot see: a method group, a lambda,
/// an async state machine and a <c>using static</c> all compile to a <c>call</c> in a method whose outermost type is the
/// caller's. Each has its anti-vacuity: a theory of spellings for the source, and offenders compiled into this assembly
/// for the IL.
/// </para>
/// <para>
/// <b>The shown text is three builders', not one.</b> The review that asked for this fence named
/// <c>HealthStatementQueries.Explain</c> alone; <c>Counters</c> and <c>Texts</c> use it too, because they name the
/// extension's schema as an identifier (<c>search_path</c> is pinned to <c>pg_catalog</c>, so the function is called
/// qualified) and "Show SQL" masks it as any qualifier is masked. The fence pins exactly those three.
/// </para>
/// </remarks>
public class StatementChecksFenceTests
{
    private const string Runner = "src/PostgresStudio/Services/Health/HealthCheckContext.cs";

    private const string Reads = "src/PostgresStudio/Services/Health/Checks/StatementReads.cs";

    private const string Planner = "src/PostgresStudio/Services/Health/Checks/ExpensiveStatementsCheck.cs";

    private static readonly Regex Name = new(@"\bQueryAsApplicationRoleAsync\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>A call whose first argument is a counters or text read built on the spot - by position or by name.</summary>
    private static readonly Regex AllowedCall = new(
        @"\bQueryAsApplicationRoleAsync\s*(?:<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?\s*\(\s*(?:query\s*:\s*)?HealthStatementQueries\s*\.\s*(?:Counters|Texts)\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>The runner's one declaration of it.</summary>
    private static readonly Regex Declaration = new(
        @"\bpublic\s+Task\s*<\s*IReadOnlyList\s*<\s*T\s*>\s*>\s*QueryAsApplicationRoleAsync\s*<\s*T\s*>\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Explain = new(@"\bHealthStatementQueries\s*\.\s*Explain\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex Outside = new(@"\bExecuteOutsideTransactionAsync\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>The one call outside a transaction there may be: the plan's deallocate, built on the spot.</summary>
    private static readonly Regex AllowedOutside = new(
        @"\bExecuteOutsideTransactionAsync\s*\(\s*(?:query\s*:\s*)?HealthStatementQueries\s*\.\s*Deallocate\s*\(\s*\)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex OutsideDeclaration = new(
        @"\binternal\s+async\s+Task\s+ExecuteOutsideTransactionAsync\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly MethodInfo ApplicationRoleRead =
        typeof(HealthSqlRunner).GetMethod(nameof(HealthSqlRunner.QueryAsApplicationRoleAsync))!;

    private static readonly MethodInfo OutsideTransaction =
        typeof(HealthSqlRunner).GetMethod(nameof(HealthSqlRunner.ExecuteOutsideTransactionAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo Deallocate = typeof(HealthStatementQueries).GetMethod(nameof(HealthStatementQueries.Deallocate))!;

    private static readonly ConstructorInfo ShownConstructor =
        typeof(HealthQuery).GetConstructor([typeof(string), typeof(IReadOnlyList<NpgsqlParameter>), typeof(string)])!;

    private static readonly MethodInfo[] StatementBuilders =
    [
        typeof(HealthStatementQueries).GetMethod(nameof(HealthStatementQueries.Counters))!,
        typeof(HealthStatementQueries).GetMethod(nameof(HealthStatementQueries.Texts))!,
    ];

    // ------------------------------------------------------------------------------------------------
    // Source
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_statement_reads_name_the_application_role_read_and_only_to_hand_it_counters_or_texts()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");
        files.Select(SourceScanner.Relative).Should().Contain([Runner, Reads, Planner], "every exemption must name a file that exists");

        List<string> offenders =
        [
            .. files
                .Select(static file => (Path: SourceScanner.Relative(file), Text: File.ReadAllText(file)))
                .Where(static x => Offends(x.Path, x.Text))
                .Select(static x => x.Path),
        ];

        offenders.Should().BeEmpty(
            "the store role's read is for pg_stat_statements' counters and texts alone, which StatementReads asks for: "
            + string.Join(", ", offenders));

        string reads = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(Reads)));
        AllowedCall.Count(reads).Should().Be(2, "the counters and the texts, one call each - or this fence is reading nothing");

        string planner = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(Planner)));
        AllowedOutside.Count(planner).Should().Be(1, "the second try at taking the plan back - or this fence is reading nothing");
    }

    [Theory]
    // What StatementReads writes.
    [InlineData(Reads, "await context.Sql\n    .QueryAsApplicationRoleAsync(HealthStatementQueries.Counters(extension, Selection), Map, token);", false)]
    [InlineData(Reads, "await context.Sql.QueryAsApplicationRoleAsync(HealthStatementQueries.Texts(extension, missing), Map, token);", false)]
    [InlineData(Reads, "await context.Sql.QueryAsApplicationRoleAsync<StatementTextRow>(query: HealthStatementQueries.Texts(e, ids), map: Read, cancellationToken: token);", false)]
    [InlineData(Reads, "await sql.QueryAsApplicationRoleAsync<IReadOnlyList<int>>(HealthStatementQueries.Counters(e, s), Map, token);", false)]
    // What it may not.
    [InlineData(Reads, "await context.Sql.QueryAsApplicationRoleAsync(HealthStatementQueries.Extension(), Map, token);", true)]
    [InlineData(Reads, "await context.Sql.QueryAsApplicationRoleAsync(HealthTableQueries.Stats(schemas), Map, token);", true)]
    [InlineData(Reads, "var q = HealthStatementQueries.Counters(e, s);\nawait context.Sql.QueryAsApplicationRoleAsync(q, Map, token);", true)]
    [InlineData(Reads, "Func<HealthQuery, Func<NpgsqlDataReader, int>, CancellationToken, Task<IReadOnlyList<int>>> f = context.Sql.QueryAsApplicationRoleAsync;", true)]
    [InlineData(Reads, "await context.Sql.QueryAsApplicationRoleAsync(map: Map, query: HealthStatementQueries.Counters(e, s), cancellationToken: token);", true)]
    // Anywhere else, any call at all.
    [InlineData(Planner, "await context.Sql.QueryAsApplicationRoleAsync(HealthStatementQueries.Counters(e, s), Map, token);", true)]
    [InlineData("src/MartenStudio/Services/Health/Checks/SomeCheck.cs", "string n = nameof(HealthSqlRunner.QueryAsApplicationRoleAsync);", true)]
    // The runner declares it, and calls it from nowhere.
    [InlineData(Runner, "public Task<IReadOnlyList<T>> QueryAsApplicationRoleAsync<T>(\n    HealthQuery query,\n    Func<NpgsqlDataReader, T> map,", false)]
    [InlineData(Runner, "public Task<T?> ScalarAsync<T>(HealthQuery q, CancellationToken c) => QueryAsApplicationRoleAsync(q, Map, c);", true)]
    // Prose is prose.
    [InlineData(Planner, "// QueryAsApplicationRoleAsync(HealthTableQueries.Stats(schemas)) in prose\nclass C { }", false)]
    [InlineData(Planner, "/// <see cref=\"HealthSqlRunner.QueryAsApplicationRoleAsync{T}\" />\nclass C { }", false)]
    [InlineData(Planner, "string s = \"QueryAsApplicationRoleAsync(x)\";", false)]
    // The plan is the planner's.
    [InlineData(Planner, "var query = HealthStatementQueries.Explain(statement.QueryId, statement.Text!, statement.TextTruncated);", false)]
    [InlineData(Reads, "var query = HealthStatementQueries.Explain(id, text, false);", true)]
    [InlineData("src/PostgresStudio/Services/Health/Checks/JitShareCheck.cs", "var plan = HealthStatementQueries .Explain(id, text, false);", true)]
    // The one statement outside a transaction is T02's deallocate, and the runner only declares it.
    [InlineData(Planner, "await context.Sql.ExecuteOutsideTransactionAsync(HealthStatementQueries.Deallocate(), again.Token);", false)]
    [InlineData(Planner, "await context.Sql.ExecuteOutsideTransactionAsync(HealthStatementQueries.Extension(), token);", true)]
    [InlineData(Planner, "var q = HealthStatementQueries.Deallocate();\nawait context.Sql.ExecuteOutsideTransactionAsync(q, token);", true)]
    [InlineData(Reads, "await context.Sql.ExecuteOutsideTransactionAsync(HealthStatementQueries.Deallocate(), token);", true)]
    [InlineData(Runner, "internal async Task ExecuteOutsideTransactionAsync(HealthQuery query, CancellationToken cancellationToken)", false)]
    [InlineData(Runner, "public Task ResetSessionAsync(HealthQuery q, CancellationToken c) => ExecuteOutsideTransactionAsync(q, c);", true)]
    public void The_source_fence_catches_every_other_spelling_and_ignores_prose(string path, string source, bool offends)
    {
        Offends(path, source).Should().Be(offends);
    }

    // ------------------------------------------------------------------------------------------------
    // IL
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Both studio assemblies: the runner, the builders and the statement checks are the shared studio's, and
    /// MartenStudio's own checks call the runner from the other side of an assembly boundary - where a caller of one of
    /// the three doors would be as much a caller as one beside them.
    /// </summary>
    private static Assembly[] StudioAssemblies => [typeof(HealthQuery).Assembly, StudioProjects.MartenAssembly];

    /// <summary>Every site in either studio assembly whose body reaches <paramref name="target" />.</summary>
    private static List<MethodBase> StudioSites(MethodBase target) => [.. StudioAssemblies.SelectMany(x => IlScan.Sites(x, target))];

    /// <summary>
    /// The IL fences read both assemblies, and resolve a call across the boundary: MartenStudio's daemon and event checks
    /// call the shared runner's <c>QueryAsync</c>, so the scan of <c>MartenStudio.dll</c> finds them. A token it could not
    /// resolve would be dropped silently, and every fence below would pass over that assembly for the wrong reason.
    /// </summary>
    [Fact]
    public void The_compiled_fences_read_both_studio_assemblies_and_resolve_calls_across_them()
    {
        StudioAssemblies.Should().OnlyHaveUniqueItems("the shared studio and MartenStudio are two assemblies since the move");

        MethodInfo query = typeof(HealthSqlRunner).GetMethod(nameof(HealthSqlRunner.QueryAsync))!;

        foreach (Assembly assembly in StudioAssemblies)
        {
            IlScan.Sites(assembly, query).Should().NotBeEmpty(
                "{0}'s checks run their statements through the runner, so a scan of it that saw no call is reading nothing",
                assembly.GetName().Name);
        }

        IlScan.Sites(StudioProjects.MartenAssembly, query)
            .Should().OnlyContain(static x => x.DeclaringType!.Assembly == StudioProjects.MartenAssembly)
            .And.Contain(static x => x.DeclaringType!.Namespace!.StartsWith("MartenStudio.Services.Health", StringComparison.Ordinal),
                "MartenStudio's own checks are the callers on that side");
    }

    [Fact]
    public void In_the_compiled_assembly_only_the_statement_reads_call_the_application_role_read_and_each_builds_what_it_hands_it()
    {
        List<MethodBase> sites = StudioSites(ApplicationRoleRead);

        sites.Should().NotBeEmpty("StatementReads calls it, so a scan that finds nothing is reading nothing");

        List<string> offenders =
        [
            .. sites
                .Where(static x => IlScan.Outermost(x.DeclaringType!) != typeof(StatementReads))
                .Select(static x => x.DeclaringType!.FullName + "." + x.Name),
        ];

        offenders.Should().BeEmpty("only StatementReads reads as the store's own role: " + string.Join(", ", offenders));

        sites.Should().OnlyContain(
            static x => StatementBuilders.Any(builder => IlScan.Calls(x, builder)),
            "every method that reads as the store's role builds a counters or a text read in the same body");
    }

    [Fact]
    public void In_the_compiled_assembly_only_the_three_statement_builders_construct_a_query_whose_show_sql_is_not_its_text()
    {
        List<string> sites =
        [
            .. StudioSites(ShownConstructor).Select(static x => x.DeclaringType!.FullName + "." + x.Name),
        ];

        sites.Should().BeEquivalentTo(
            [
                typeof(HealthStatementQueries).FullName + "." + nameof(HealthStatementQueries.Counters),
                typeof(HealthStatementQueries).FullName + "." + nameof(HealthStatementQueries.Texts),
                typeof(HealthStatementQueries).FullName + "." + nameof(HealthStatementQueries.Explain),
            ],
            "the plan prints its batch's shape instead of another program's text, and the two reads mask the extension's schema; "
            + "nothing else prints anything but what it runs");
    }

    [Fact]
    public void In_the_compiled_assembly_only_the_expensive_statements_check_plans_a_recorded_statement()
    {
        MethodInfo explain = typeof(HealthStatementQueries).GetMethod(nameof(HealthStatementQueries.Explain))!;
        List<MethodBase> sites = StudioSites(explain);

        sites.Should().NotBeEmpty().And.OnlyContain(
            static x => IlScan.Outermost(x.DeclaringType!) == typeof(ExpensiveStatementsCheck),
            "the generic plan is T02's evidence, run under its ForgetAsync cleanup, and nobody else's");
    }

    /// <summary>
    /// The IL fences' anti-vacuity: every spelling a source fence could miss, compiled into this test assembly, is found -
    /// and a method that only touches the runner is not.
    /// </summary>
    [Fact]
    public void The_compiled_fences_find_every_spelling_of_a_call_and_a_construction()
    {
        List<MethodBase> calls =
        [
            .. IlScan.Sites(typeof(StatementChecksFenceTests).Assembly, ApplicationRoleRead)
                .Where(static x => IlScan.Encloses(typeof(Offenders), x.DeclaringType!)),
        ];

        calls.Select(static x => x.Name).Should().Contain(nameof(Offenders.Direct), "a direct call");
        calls.Should().Contain(static x => x.DeclaringType != typeof(Offenders) && x.Name == "MoveNext", "an awaited call lives in a state machine");
        calls.Should().Contain(static x => x.DeclaringType != typeof(Offenders) && x.Name != "MoveNext", "a lambda's body is a method of a closure type");
        calls.Should().Contain(static x => x.Name == nameof(Offenders.MethodGroup), "a method group is an ldftn, and the scan reads it as a call");
        calls.Select(static x => x.Name).Should().NotContain(nameof(Offenders.NotAnOffender), "reading the runner's statements is not a read as the store's role");

        calls.Where(static x => x.Name == nameof(Offenders.Direct)).Should().OnlyContain(
            static x => !StatementBuilders.Any(builder => IlScan.Calls(x, builder)), "an offender handed a query it did not build is told apart");

        IlScan.Sites(typeof(StatementChecksFenceTests).Assembly, ShownConstructor)
            .Where(static x => IlScan.Encloses(typeof(Offenders), x.DeclaringType!))
            .Select(static x => x.Name).Should().Equal(nameof(Offenders.Shown));

        List<MethodBase> outside =
        [
            .. IlScan.Sites(typeof(StatementChecksFenceTests).Assembly, OutsideTransaction)
                .Where(static x => IlScan.Encloses(typeof(Offenders), x.DeclaringType!)),
        ];

        outside.Select(static x => x.Name).Should().Equal(nameof(Offenders.Outside));
        outside.Should().OnlyContain(static x => !IlScan.Calls(x, Deallocate), "an offender handed a statement it did not build is told apart");
    }

    /// <summary>
    /// Re-review F6: the fence on the method is a fence on the session only if nothing else reaches the runner's
    /// store-role session - so the field that holds it is loaded by that one method and nowhere else.
    /// </summary>
    [Fact]
    public void In_the_compiled_assembly_only_the_application_role_read_loads_the_application_role_session()
    {
        FieldInfo? session = typeof(HealthSqlRunner).GetField("applicationRoleSession", BindingFlags.Instance | BindingFlags.NonPublic);
        session.Should().NotBeNull("the runner's store-role session is the field this fence is about; a rename must move the fence with it");

        StudioAssemblies.SelectMany(x => IlScan.Readers(x, session!))
            .Select(static x => x.DeclaringType!.FullName + "." + x.Name)
            .Should().Equal(
                [typeof(HealthSqlRunner).FullName + "." + nameof(HealthSqlRunner.QueryAsApplicationRoleAsync)],
                "no other member of the runner may run a statement on the session that reads as the store's own role");
    }

    /// <summary>The field fence's anti-vacuity: a read of a field is found however it is spelled, and a write is not one.</summary>
    [Fact]
    public void The_field_fence_finds_every_read_and_no_write()
    {
        FieldInfo field = typeof(SessionHolder).GetField("applicationRoleSession", BindingFlags.Instance | BindingFlags.NonPublic)!;

        List<MethodBase> readers = IlScan.Readers(typeof(StatementChecksFenceTests).Assembly, field);

        readers.Select(static x => x.Name).Should().Contain(nameof(SessionHolder.Read), "a direct read");
        readers.Should().Contain(static x => x.Name != nameof(SessionHolder.Read), "a lambda's read is a method of its own");
        readers.Select(static x => x.Name).Should().NotContain([nameof(SessionHolder.Write), ".ctor"], "storing into a field is not reading it");
    }

    /// <summary>A field read directly, through a lambda, and written - for the field fence to find, or not.</summary>
    private sealed class SessionHolder
    {
        private object? applicationRoleSession = new();

        public object? Read() => applicationRoleSession;

        public Func<object?> Lambda() => () => applicationRoleSession;

        public void Write(object value) => applicationRoleSession = value;
    }

    [Fact]
    public void In_the_compiled_assembly_only_T02_runs_a_statement_outside_a_transaction_and_only_its_deallocate()
    {
        OutsideTransaction.Should().NotBeNull();
        List<MethodBase> sites = StudioSites(OutsideTransaction);

        sites.Should().NotBeEmpty("ForgetAsync calls it, so a scan that finds nothing is reading nothing");
        sites.Should().OnlyContain(
            static x => IlScan.Outermost(x.DeclaringType!) == typeof(ExpensiveStatementsCheck),
            "the second try at taking the plan back is the only health statement that runs outside a read-only transaction");
        sites.Should().OnlyContain(static x => IlScan.Calls(x, Deallocate), "and what it runs there is the deallocate, built in the same body");
    }

    private static bool Offends(string path, string source)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);
        int named = Name.Count(code);

        bool role = path switch
        {
            Runner => named != Declaration.Count(code) || named > 1,
            Reads => named != AllowedCall.Count(code),
            _ => named != 0,
        };

        int outside = Outside.Count(code);

        bool session = path switch
        {
            Runner => outside != OutsideDeclaration.Count(code) || outside > 1,
            Planner => outside != AllowedOutside.Count(code),
            _ => outside != 0,
        };

        return role || session || (!string.Equals(path, Planner, StringComparison.Ordinal) && Explain.IsMatch(code));
    }

    /// <summary>Every way to reach the two doors that a source regex could miss.</summary>
    private static class Offenders
    {
        public static Task<IReadOnlyList<int>> Direct(HealthSqlRunner sql, HealthQuery query) =>
            sql.QueryAsApplicationRoleAsync(query, static _ => 0, CancellationToken.None);

        public static async Task<int> Awaited(HealthSqlRunner sql, HealthQuery query) =>
            (await sql.QueryAsApplicationRoleAsync(query, static _ => 0, CancellationToken.None)).Count;

        public static Func<HealthQuery, Task<IReadOnlyList<int>>> Lambda(HealthSqlRunner sql) =>
            query => sql.QueryAsApplicationRoleAsync(query, static _ => 0, CancellationToken.None);

        public static Func<HealthQuery, Func<NpgsqlDataReader, int>, CancellationToken, Task<IReadOnlyList<int>>> MethodGroup(HealthSqlRunner sql) =>
            sql.QueryAsApplicationRoleAsync;

        public static HealthQuery Shown() => new("select 1", [], "select 2");

        public static Task Outside(HealthSqlRunner sql, HealthQuery query) => sql.ExecuteOutsideTransactionAsync(query, CancellationToken.None);

        public static int NotAnOffender(HealthSqlRunner sql) => sql.Statements.Count;
    }

    /// <summary>Reads method bodies for a <c>call</c>, <c>callvirt</c>, <c>newobj</c> or <c>ldftn</c> of one method.</summary>
    private static class IlScan
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => (OpCode) field.GetValue(null)!)
            .GroupBy(static code => code.Value)
            .ToDictionary(static group => group.Key, static group => group.First());

        private static readonly HashSet<OpCode> Reaches = [OpCodes.Call, OpCodes.Callvirt, OpCodes.Newobj, OpCodes.Ldftn, OpCodes.Ldvirtftn];

        private static readonly HashSet<OpCode> Loads = [OpCodes.Ldfld, OpCodes.Ldflda];

        /// <summary>Every method or constructor of <paramref name="assembly" /> whose body reaches <paramref name="target" />.</summary>
        public static List<MethodBase> Sites(Assembly assembly, MethodBase target)
        {
            List<MethodBase> sites = [];

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    if (Calls(method, target))
                    {
                        sites.Add(method);
                    }
                }
            }

            return sites;
        }

        /// <summary>Whether <paramref name="method" />'s body reaches <paramref name="target" />, any instantiation of it included.</summary>
        public static bool Calls(MethodBase method, MethodBase target) =>
            Instructions(method).Any(x => Reaches.Contains(x.Code) && Resolve(method, x.Operand) is { } reached && Same(reached, target));

        /// <summary>Every method or constructor of <paramref name="assembly" /> whose body loads <paramref name="field" />.</summary>
        public static List<MethodBase> Readers(Assembly assembly, FieldInfo field)
        {
            List<MethodBase> sites = [];

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    if (Instructions(method).Any(x => Loads.Contains(x.Code) && ResolveField(method, x.Operand) is { } loaded
                            && loaded.Module == field.Module && loaded.MetadataToken == field.MetadataToken))
                    {
                        sites.Add(method);
                    }
                }
            }

            return sites;
        }

        /// <summary>Each instruction of <paramref name="method" />'s body, with its four-byte operand where it has one.</summary>
        private static IEnumerable<(OpCode Code, int Operand)> Instructions(MethodBase method)
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            {
                yield break;
            }

            int position = 0;

            while (position < il.Length)
            {
                short value = il[position] == 0xFE && position + 1 < il.Length
                    ? unchecked((short) (0xFE00 | il[++position]))
                    : il[position];
                position++;

                OpCode code = ByValue[value];
                int size = code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                    _ => 4,
                };

                yield return (code, size == 4 ? BitConverter.ToInt32(il, position) : 0);
                position += size;
            }
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

        private static bool Same(MethodBase reached, MethodBase target)
        {
            if (reached is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } constructed)
            {
                reached = constructed.GetGenericMethodDefinition();
            }

            return reached.Module == target.Module && reached.MetadataToken == target.MetadataToken;
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
                // A token this context cannot resolve is not one of the non-generic owners' methods this fence looks for.
                return null;
            }
        }

        private static FieldInfo? ResolveField(MethodBase method, int token)
        {
            try
            {
                return method.Module.ResolveField(
                    token,
                    method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
                // Not a field of a non-generic type, which is the only kind this fence looks for.
                return null;
            }
        }
    }
}
