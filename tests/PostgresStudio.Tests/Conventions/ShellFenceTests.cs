using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// The fence around the shell (phase A of the shell and modules plan): <c>src/PostgresStudio/Shell/</c> is the chrome of the studio - the
/// document, routes, layout, rail, header frame, capability chip, preferences, toasts, the Activity ring and page, the live-update seam, the
/// endpoint mapper and its startup guard, the module seam - and it knows no store, database, tenant, source, SQL, health or Npgsql. In phase B
/// the folder becomes an assembly of its own, so what it names is held at two levels now, and each has a theory proving it can fail.
/// </summary>
/// <remarks>
/// <para>
/// <b>The text fence</b> reads every <c>.cs</c> and <c>.razor</c> file under <c>Shell/</c>, comments removed, for the words that name the database
/// side (<c>Npgsql</c>, <c>StudioScope</c> and everything that begins with it (<c>StudioScopeQuery</c> too), <c>StudioState</c>, <c>StudioAuthorization</c>, <c>StudioCapabilityGuard</c>, <c>StudioDescriptor</c>,
/// <c>IStudioSettings</c>, <c>IStudioSource</c>, <c>TenantList</c>, <c>StudioText</c>, <c>StudioProduct</c>, a <c>Sql</c>- or <c>Health</c>-prefixed type
/// name) and for a <c>using</c> of a <c>PostgresStudio</c> namespace. A shell file reaches the database through nothing it has to import.
/// </para>
/// <para>
/// <b>The compiled fence</b> reads the built <c>PostgresStudio.dll</c>: no type in a <c>DataStudio</c> namespace may reference, in a base type, an
/// interface, a field, a method signature, an attribute or a method body, a type of a <c>PostgresStudio</c> namespace, except the types this test lists by
/// name with the reason. The <b>engine helpers</b> are the ones phase B copies into the shell's own assembly; the entries marked
/// <c>A3 finding</c> are what the shell reaches beyond them, listed and not fixed because moving or editing the file that would remove each is not
/// A3's decision.
/// </para>
/// </remarks>
public partial class ShellFenceTests
{
    private const string Shell = "DataStudio";

    /// <summary>
    /// Every type of a <c>PostgresStudio</c> namespace a <c>DataStudio</c> type may name, with why. Each entry is a name and a reason, nothing else:
    /// a type that is not here is a finding to report, never an entry to add.
    /// </summary>
    private static readonly (string Type, string Reason)[] Allowed =
    [
        ("PostgresStudio.Services.StudioPageRoutes", "an engine helper phase B copies: the mount-relative form of a page's path (StudioLink)"),
        ("PostgresStudio.Services.StudioLogThrottle", "an engine helper phase B copies: the throttle behind a repeated warning"),
    ];

    [Fact]
    public void No_shell_file_names_the_database_side()
    {
        string[] files = ShellFiles();

        files.Should().HaveCountGreaterThan(20, "scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .SelectMany(static path => Offences(path, File.ReadAllText(path)).Select(offence => $"{Relative(path)}: {offence}"))
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("the shell names no store, database, tenant, source, SQL, health or Npgsql: " + string.Join("; ", offenders));
    }

    [Theory]
    [InlineData("var x = new NpgsqlConnection();", true)]
    [InlineData("StudioScope scope;", true)]
    [InlineData("@inject StudioState State", true)]
    [InlineData("@inject StudioAuthorization Auth", true)]
    [InlineData("private readonly StudioCapabilityGuard guard;", true)]
    [InlineData("private readonly StudioDescriptor descriptor;", true)]
    [InlineData("private readonly IStudioSettings settings;", true)]
    [InlineData("private readonly IStudioSource source;", true)]
    [InlineData("TenantList list;", true)]
    [InlineData("StudioText.Of(settings)", true)]
    [InlineData("StudioProduct.PostgresStudio", true)]
    [InlineData("SqlDisclosure d;", true)]
    [InlineData("HealthMutes mutes;", true)]
    [InlineData("using PostgresStudio.Services;", true)]
    [InlineData("global using PostgresStudio.Services;", true)]
    [InlineData("using static PostgresStudio.Services.Foo;", true)]
    [InlineData("using Alias = PostgresStudio.Services.Foo;", true)]
    [InlineData("@using PostgresStudio.Components", true)]
    [InlineData("@using PostgresStudio", true)]
    [InlineData("// StudioState and Npgsql, in prose", false)]
    [InlineData("/// <c>StudioScope</c> and <see cref=\"StudioDescriptor\" />", false)]
    [InlineData("/* HealthMutes */ int x;", false)]
    [InlineData("@* @inject StudioState State *@", false)]
    [InlineData("<!-- SqlText -->", false)]
    [InlineData("using DataStudio.Services;", false)]
    [InlineData("@using DataStudio.Components", false)]
    [InlineData("@using Microsoft.AspNetCore.Components.Web", false)]
    [InlineData("var scopeQuery = StudioScopeQuery.Read(uri);", true)]
    [InlineData("var scope = studioScope;", false)]
    [InlineData("string sqlite = \"x\"; int healthy = 1; HealthyColor c;", false)]
    public void The_text_scan_can_fail(string text, bool offends)
    {
        Offences("x.razor", text).Any().Should().Be(offends);
    }

    [Fact]
    public void No_type_of_the_shell_references_a_type_of_the_database_side_beyond_the_allow_list()
    {
        Assembly assembly = typeof(PostgresStudioOptions).Assembly;
        Type[] shell = [.. assembly.GetTypes().Where(static x => IsShell(x))];

        shell.Should().HaveCountGreaterThan(20, "scanning nothing would pass for the wrong reason");

        HashSet<string> allowed = [.. Allowed.Select(static x => x.Type)];
        string[] offenders =
        [
            .. shell
                .SelectMany(static type => ReferencedTypes(type).Where(static x => IsDatabaseSide(x)).Select(reference => (Owner: type, Reference: reference)))
                .Where(pair => !allowed.Contains(pair.Reference.FullName ?? pair.Reference.Name))
                .Select(static pair => $"{pair.Owner.FullName} -> {pair.Reference.FullName}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("the shell reaches the database side only through the allow-list: " + string.Join("; ", offenders));
    }

    [Fact]
    public void The_allow_list_names_types_that_exist_and_the_one_the_shell_needs_is_used()
    {
        Assembly assembly = typeof(PostgresStudioOptions).Assembly;
        HashSet<string> referenced =
        [
            .. assembly.GetTypes()
                .Where(static x => IsShell(x))
                .SelectMany(static x => ReferencedTypes(x))
                .Select(static x => x.FullName ?? x.Name),
        ];

        foreach ((string type, string reason) in Allowed)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            AllTypes().Any(x => x.FullName == type).Should().BeTrue(type + " is a type of the PostgresStudio assemblies");
            referenced.Should().Contain(type, "an entry nobody uses is a hole in the fence");
        }
    }

    [Fact]
    public void The_compiled_scan_sees_a_field_and_a_method_body()
    {
        // DatabaseScopeHost is the database's own component: it names StudioState in a field and StudioScopeQuery in a method body.
        // If the scan did not see both, a shell type could hide the same way.
        string[] seen =
        [
            .. ReferencedTypes(typeof(PostgresStudio.Components.Layout.DatabaseScopeHost)).Select(static x => x.FullName ?? x.Name),
        ];

        seen.Should().Contain("PostgresStudio.Services.StudioState");
        seen.Should().Contain("PostgresStudio.Components.StudioScopeQuery");
    }

    [Fact]
    public void The_scan_classifies_the_namespaces()
    {
        IsShell(typeof(DataStudio.Services.StudioActivity)).Should().BeTrue();
        IsShell(typeof(PostgresStudio.Services.StudioState)).Should().BeFalse();
        IsDatabaseSide(typeof(PostgresStudio.Services.StudioState)).Should().BeTrue();
        IsDatabaseSide(typeof(DataStudio.Services.StudioActivity)).Should().BeFalse();
        IsDatabaseSide(typeof(string)).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The text fence

    private static string[] ShellFiles() =>
    [
        .. Directory.EnumerateFiles(RepositoryRoot.Combine("src", "PostgresStudio", "Shell"), "*", SearchOption.AllDirectories)
            .Where(static x => (x.EndsWith(".cs", StringComparison.Ordinal) || x.EndsWith(".razor", StringComparison.Ordinal))
                && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
    ];

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot.Combine("src", "PostgresStudio"), path).Replace('\\', '/');

    /// <summary>What a file names that a shell file must not, after its comments are removed.</summary>
    private static IEnumerable<string> Offences(string path, string text)
    {
        string code = StripComments(text, path.EndsWith(".razor", StringComparison.Ordinal));

        foreach (Match match in Banned().Matches(code))
        {
            yield return "names " + match.Value;
        }

        foreach (Match match in Using().Matches(code))
        {
            yield return "imports " + match.Value.Trim();
        }
    }

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments, and for Razor <c>@* *@</c> and <c>&lt;!-- --&gt;</c>, keeping string literals whole.</summary>
    private static string StripComments(string text, bool razor)
    {
        text = BlockComment().Replace(text, " ");

        if (razor)
        {
            text = RazorComment().Replace(text, " ");
            text = HtmlComment().Replace(text, " ");
        }

        return LineComment().Replace(text, "$1");
    }

    [GeneratedRegex(@"\bNpgsql|\bStudioScope\w*|\bStudioState\b|\bStudioAuthorization\b|\bStudioCapabilityGuard\b|\bStudioDescriptor\b|\bIStudioSettings\b|\bIStudioSource\b|\bTenantList\b|\bStudioText\b|\bStudioProduct\b|\bSql[A-Z]\w*|\bHealth[A-Z]\w*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Banned();

    [GeneratedRegex(@"(?m)^\s*(?:global\s+)?(?:@)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?PostgresStudio\b[\w.]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Using();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HtmlComment();

    // A line comment starts where a // is not part of a URL's scheme (http://) or a string; the shell has no such string, and the first group keeps the preceding character.
    [GeneratedRegex(@"(?m)(^|[^:""])//[^\n]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LineComment();

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The compiled fence

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    private static bool IsShell(Type type) =>
        type.Namespace is { } ns && (ns == Shell || ns.StartsWith(Shell + ".", StringComparison.Ordinal));

    private static bool IsDatabaseSide(Type type) =>
        type.Namespace is { } ns && (ns == "PostgresStudio" || ns.StartsWith("PostgresStudio.", StringComparison.Ordinal));

    private static IEnumerable<Type> AllTypes() =>
        typeof(PostgresStudioOptions).Assembly.GetTypes()
            .Concat(typeof(PostgresStudio.Services.StudioPageRoutes).Assembly.GetTypes());

    /// <summary>Every type a type names: its base, its interfaces, its attributes, its members' types and attributes, and the types in its method bodies.</summary>
    private static List<Type> ReferencedTypes(Type type)
    {
        List<Type> found = [];

        void Add(Type? each)
        {
            foreach (Type expanded in Expand(each))
            {
                found.Add(expanded);
            }
        }

        Add(type.BaseType);

        foreach (Type contract in type.GetInterfaces())
        {
            Add(contract);
        }

        AddAttributes(type.CustomAttributes, Add);

        foreach (FieldInfo field in type.GetFields(Declared))
        {
            Add(field.FieldType);
            AddAttributes(field.CustomAttributes, Add);
        }

        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            Add(property.PropertyType);
            AddAttributes(property.CustomAttributes, Add);
        }

        foreach (EventInfo declared in type.GetEvents(Declared))
        {
            Add(declared.EventHandlerType);
        }

        foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (method is MethodInfo info)
            {
                Add(info.ReturnType);
                AddAttributes(info.ReturnParameter.CustomAttributes, Add);
            }

            AddAttributes(method.CustomAttributes, Add);

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Add(parameter.ParameterType);
                AddAttributes(parameter.CustomAttributes, Add);
            }

            if (method.IsGenericMethodDefinition)
            {
                foreach (Type argument in method.GetGenericArguments())
                {
                    foreach (Type constraint in argument.GetGenericParameterConstraints())
                    {
                        Add(constraint);
                    }
                }
            }

            if (method.GetMethodBody() is { } body)
            {
                foreach (LocalVariableInfo local in body.LocalVariables)
                {
                    Add(local.LocalType);
                }

                foreach (Type referenced in BodyTypes(method, body.GetILAsByteArray()!))
                {
                    Add(referenced);
                }
            }
        }

        return found;
    }

    private static void AddAttributes(IEnumerable<CustomAttributeData> attributes, Action<Type?> add)
    {
        foreach (CustomAttributeData attribute in attributes)
        {
            add(attribute.AttributeType);

            foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
            {
                if (argument.Value is Type named)
                {
                    add(named);
                }
            }
        }
    }

    /// <summary>A type, its element type and its generic arguments, all the way down.</summary>
    private static IEnumerable<Type> Expand(Type? type)
    {
        if (type is null)
        {
            yield break;
        }

        yield return type;

        foreach (Type inner in (type.HasElementType ? [type.GetElementType()!] : Array.Empty<Type>())
            .Concat(type.IsGenericType ? type.GetGenericArguments() : [])
            .SelectMany(Expand))
        {
            yield return inner;
        }
    }

    /// <summary>The types a method body's instructions name: a called member's owner and signature, a field's, a newed or cast or loaded type.</summary>
    private static IEnumerable<Type> BodyTypes(MethodBase method, byte[] il)
    {
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int position = 0;

        while (position < il.Length)
        {
            short value = il[position] == 0xFE && position + 1 < il.Length
                ? unchecked((short) (0xFE00 | il[++position]))
                : il[position];
            position++;

            OpCode code = ByValue[value];

            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = Resolve(method, BitConverter.ToInt32(il, position), typeArguments, methodArguments);

                foreach (Type named in TypesOf(member))
                {
                    yield return named;
                }
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
    }

    private static MemberInfo? Resolve(MethodBase method, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            return method.Module.ResolveMember(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            // A token this context cannot resolve is a string, a signature or a generic parameter's: nothing that names a type of ours.
            return null;
        }
    }

    private static IEnumerable<Type> TypesOf(MemberInfo? member) => member switch
    {
        null => [],
        Type type => Expand(type),
        MethodInfo method => Expand(method.DeclaringType)
            .Concat(Expand(method.ReturnType))
            .Concat(method.GetParameters().SelectMany(static x => Expand(x.ParameterType)))
            .Concat(method.IsGenericMethod ? method.GetGenericArguments().SelectMany(Expand) : []),
        ConstructorInfo constructor => Expand(constructor.DeclaringType)
            .Concat(constructor.GetParameters().SelectMany(static x => Expand(x.ParameterType))),
        FieldInfo field => Expand(field.DeclaringType).Concat(Expand(field.FieldType)),
        _ => Expand(member.DeclaringType),
    };
}
