using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace MartenStudio.Services.Query;

/// <summary>What a member's type is, as far as LINQ mode is concerned.</summary>
internal enum LinqTypeKind
{
    /// <summary>Nothing LINQ mode will let a predicate reach: it is not part of the graph.</summary>
    Excluded,

    /// <summary>A value a predicate can compare: text, a number, a date, a Guid, an enum - or a nullable one.</summary>
    Scalar,

    /// <summary>A collection of scalars or of objects in the graph.</summary>
    Collection,

    /// <summary>A nested object of the host's own whose members are themselves part of the graph.</summary>
    Object,
}

/// <summary>One member a predicate may name.</summary>
/// <param name="Member">The property or field.</param>
/// <param name="MemberType">Its type.</param>
/// <param name="Kind">How LINQ mode treats that type.</param>
/// <param name="ElementType">For a collection, the element type; otherwise <see langword="null" />.</param>
internal sealed record LinqMember(MemberInfo Member, Type MemberType, LinqTypeKind Kind, Type? ElementType);

/// <summary>
/// The members of one document type a LINQ predicate may name: its own public properties and fields, and
/// those of the objects and collections it holds, a few levels down - and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the allow-list's notion of "the document's own graph"</b>, and both halves of the studio's
/// defence read it: <see cref="LinqTextScreen" /> binds every identifier on the type it is read from, as the parser
/// will (<see cref="Bind" />), and refuses one that does not bind to a member here before any parser sees the text;
/// and <see cref="LinqExpressionGuard" /> refuses a member expression whose member is not one of these, compared by
/// metadata token so that a member found through a different reflected type is still recognised and a different
/// member of the same name is not.
/// </para>
/// <para>
/// <b>What is left out, and why.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// Types from the framework and from the libraries the studio itself runs on (<c>System.*</c>,
/// <c>Microsoft.*</c>, Npgsql, Marten, JasperFx, Weasel, Newtonsoft): a document that holds a
/// <c>System.Type</c>, a <c>JsonElement</c>, a <c>Stream</c> or an <c>Expression</c> holds something whose
/// members are not data, and a predicate has no business walking into them.
/// </description></item>
/// <item><description>
/// A type with a converter of the host's own - a <see cref="TypeConverterAttribute" />, or one added at run time
/// through <see cref="TypeDescriptor" />. System.Linq.Dynamic.Core converts a string
/// literal compared with a member <em>while it parses</em>, through
/// <c>TypeDescriptor.GetConverter(memberType)</c> (1.7.4, <c>ExpressionParser.ParseComparisonOperator</c>) -
/// which, for such a type, runs the host's converter on text the visitor typed, before any allow-list exists to
/// stop it. Leaving the type out of the graph means no predicate can name a member of it, so the comparison is
/// never parsed. The scalars below keep their framework converters (Guid, the date and time types, the numbers,
/// enums), which only ever parse a literal.
/// </description></item>
/// <item><description>
/// Dictionaries, collections of collections, delegates, pointers, open generics and <see cref="object" />.
/// </description></item>
/// <item><description>
/// Any document type the host hides with <see cref="MartenStudioOptions.IsDocumentTypeVisible" />: a member whose
/// type, or whose element type, is one of them is not in the graph, so a predicate cannot reach a hidden type
/// through a navigation either. (Marten's predicates cannot leave the document's own row anyway - there is no
/// join a predicate can express - but the graph does not rely on that.)
/// </description></item>
/// </list>
/// <para>
/// The structure is reflection over types that do not change while the process runs, so it is computed once per
/// document type. The hidden set is not: <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> is the host's
/// own predicate, and it is asked again for every request.
/// </para>
/// </remarks>
internal sealed class LinqDocumentGraph
{
    private static readonly ConcurrentDictionary<Type, Structure> Structures = new();

    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(string), typeof(bool), typeof(char),
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal),
        typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
    ];

    /// <summary>
    /// Namespace prefixes whose types are never part of a document graph: the framework, and every library the
    /// studio runs on.
    /// </summary>
    private static readonly string[] ExcludedNamespaces =
        ["System", "Microsoft", "Npgsql", "NpgsqlTypes", "Marten", "JasperFx", "Weasel", "Newtonsoft"];

    private readonly Structure structure;
    private readonly IReadOnlySet<Type> hidden;
    private readonly Lazy<IReadOnlySet<string>> names;

    private LinqDocumentGraph(Structure structure, IReadOnlySet<Type> hidden)
    {
        this.structure = structure;
        this.hidden = hidden;
        names = new Lazy<IReadOnlySet<string>>(ReachableNames, LazyThreadSafetyMode.None);
    }

    /// <summary>The document type the predicate is about.</summary>
    public Type Root => structure.Root;

    /// <summary>
    /// Every member name the graph lets a predicate reach from the document somewhere, compared without case. A member
    /// whose type the host hides, and everything only reachable through one, is not among them.
    /// </summary>
    /// <remarks>
    /// A description of the graph, and <b>never an allow-list</b>: a name allowed on one type says nothing about the
    /// same name on another, which may be a member the graph left out - and Dynamic LINQ binds a name on its receiver,
    /// not against a list. The screen asks <see cref="Bind" />, receiver by receiver (the F7 security review's B1).
    /// </remarks>
    public IReadOnlySet<string> MemberNames => names.Value;

    /// <summary>A few of the root's own scalar members, for the predicate box's hint.</summary>
    public IReadOnlyList<string> HintMembers => structure.Hint;

    /// <summary>
    /// The graph for <paramref name="root" />, with <paramref name="hiddenDocumentTypes" /> left out.
    /// </summary>
    /// <param name="root">The document type - a root mapping or a subclass of one.</param>
    /// <param name="hiddenDocumentTypes">
    /// Every document type the store knows that <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> hides,
    /// subclasses included.
    /// </param>
    public static LinqDocumentGraph For(Type root, IReadOnlySet<Type> hiddenDocumentTypes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(hiddenDocumentTypes);

        return new LinqDocumentGraph(Structures.GetOrAdd(root, Build), hiddenDocumentTypes);
    }

    /// <summary>
    /// Whether <paramref name="member" />, read off a value of <paramref name="owner" />, is one the graph allows -
    /// and if so, how it is classified.
    /// </summary>
    /// <remarks>
    /// The match is by module and metadata token, not by reference: <c>typeof(Derived).GetProperty("X")</c> and
    /// <c>typeof(Base).GetProperty("X")</c> are different <see cref="PropertyInfo" /> objects for one member, and a
    /// different member that happens to share the name is not the same member.
    /// </remarks>
    public bool TryGetMember(Type owner, MemberInfo member, out LinqMember allowed)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(member);

        allowed = null!;

        if (IsHidden(owner)
            || !structure.Members.TryGetValue(owner, out Dictionary<string, LinqMember>? own)
            || !own.TryGetValue(member.Name, out LinqMember? candidate)
            || candidate.Member.Module != member.Module
            || candidate.Member.MetadataToken != member.MetadataToken
            || IsHidden(candidate.MemberType)
            || (candidate.ElementType is { } element && IsHidden(element)))
        {
            return false;
        }

        allowed = candidate;
        return true;
    }

    /// <summary>
    /// The graph member Dynamic LINQ binds <paramref name="name" /> to on a value of <paramref name="receiver" />, or
    /// <see langword="null" /> when what it binds is not one the graph allows - or when it binds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bound the way the parser binds it</b>, which is the whole point. System.Linq.Dynamic.Core 1.7.4's
    /// <c>ExpressionParser.FindPropertyOrField</c> walks the receiver's own type and then its base types (for an
    /// interface, it and every interface it inherits), and on each asks for public properties and fields declared
    /// there - instance <em>and static</em> - matching the name without case, taking the first it finds. So the screen
    /// asks the same question of the same type and then checks that the member found is the graph's: a name the graph
    /// allows on some other type, or a member the graph leaves out that happens to share an allowed name, is not let
    /// through. Two members on one level that match without case are refused, because which one the parser takes is
    /// reflection's order.
    /// </para>
    /// <para>
    /// This is what keeps a host <c>TypeConverter</c> from running on the visitor's text: the parser converts a
    /// literal compared with a member through the <em>bound</em> member's type, so a member the graph left out for its
    /// converter must never be the one bound.
    /// </para>
    /// </remarks>
    public LinqMember? Bind(Type receiver, string name)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(name);

        return FindAsDynamicLinq(receiver, name) is { } bound && TryGetMember(receiver, bound, out LinqMember allowed) ? allowed : null;
    }

    /// <summary>
    /// The property or field Dynamic LINQ's <c>FindPropertyOrField</c> finds for <paramref name="name" /> on
    /// <paramref name="type" /> (1.7.4, with <c>IsCaseSensitive = false</c> as JasperFx sets it), or
    /// <see langword="null" /> when it finds none - or more than one on the same level, where reflection's order picks.
    /// </summary>
    internal static MemberInfo? FindAsDynamicLinq(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(name);

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static;

        foreach (Type level in SelfAndBaseTypesAsDynamicLinq(type))
        {
            MemberInfo[] found = level.FindMembers(MemberTypes.Property | MemberTypes.Field, Flags, Type.FilterNameIgnoreCase, name);

            if (found.Length != 0)
            {
                return found.Length == 1 ? found[0] : null;
            }
        }

        return null;
    }

    /// <summary>Dynamic LINQ's <c>TypeHelper.GetSelfAndBaseTypes</c>: a class and its base classes, or an interface and every interface it inherits.</summary>
    private static List<Type> SelfAndBaseTypesAsDynamicLinq(Type type)
    {
        var types = new List<Type>();

        if (type.IsInterface)
        {
            AddInterface(types, type);
            return types;
        }

        for (Type? current = type; current is not null; current = current.BaseType)
        {
            types.Add(current);
        }

        return types;

        static void AddInterface(List<Type> types, Type type)
        {
            if (types.Contains(type))
            {
                return;
            }

            types.Add(type);

            foreach (Type inherited in type.GetInterfaces())
            {
                AddInterface(types, inherited);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="type" /> is one the graph describes: the root, a nested object or a collection element
    /// it reaches, or a collection of those or of scalars. Says nothing of whether the host hides it.
    /// </summary>
    public bool IsGraphType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return structure.Members.ContainsKey(type)
               || (ElementTypeOf(type) is { } element && (IsScalar(element) || structure.Members.ContainsKey(element)));
    }

    /// <summary>Whether <paramref name="type" /> is the type of some member in the graph, so an enum constant of it is data.</summary>
    public bool IsGraphEnum(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsEnum && structure.Enums.Contains(underlying);
    }

    /// <summary>Whether <paramref name="type" />, or a type it holds, is a document type the host hides.</summary>
    public bool IsHidden(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        return hidden.Contains(underlying) || (ElementTypeOf(underlying) is { } element && hidden.Contains(element));
    }

    /// <summary>Whether <paramref name="type" /> is one of the values a predicate compares.</summary>
    public static bool IsScalar(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (ScalarTypes.Contains(underlying))
        {
            return true;
        }

        // An enum of the host's own is data, unless it carries a converter of its own - which Dynamic LINQ would
        // run on a string literal compared with it, at parse time.
        return underlying.IsEnum && !HasConverterAttribute(underlying);
    }

    /// <summary>How LINQ mode treats <paramref name="type" />.</summary>
    public static LinqTypeKind Classify(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (IsScalar(type))
        {
            return LinqTypeKind.Scalar;
        }

        if (Nullable.GetUnderlyingType(type) is not null || IsDictionary(type))
        {
            return LinqTypeKind.Excluded;
        }

        if (ElementTypeOf(type) is { } element)
        {
            return IsScalar(element) || IsObjectType(element) ? LinqTypeKind.Collection : LinqTypeKind.Excluded;
        }

        return IsObjectType(type) ? LinqTypeKind.Object : LinqTypeKind.Excluded;
    }

    /// <summary>
    /// The element type of a one-dimensional array or of a type that implements exactly one
    /// <see cref="IEnumerable{T}" />, or <see langword="null" />. A string is not a collection here.
    /// </summary>
    public static Type? ElementTypeOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 ? type.GetElementType() : null;
        }

        if (!type.IsGenericType && !type.IsInterface)
        {
            return null;
        }

        Type[] enumerables =
        [
            .. SelfAndInterfaces(type)
                .Where(static x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)),
        ];

        return enumerables.Length == 1 ? enumerables[0].GetGenericArguments()[0] : null;
    }

    private static IEnumerable<Type> SelfAndInterfaces(Type type) =>
        type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();

    private static bool IsDictionary(Type type) =>
        typeof(System.Collections.IDictionary).IsAssignableFrom(type)
        || SelfAndInterfaces(type).Any(static x => x.IsGenericType
            && (x.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || x.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static bool IsObjectType(Type type)
    {
        if (type == typeof(object)
            || type.IsPrimitive
            || type.IsEnum
            || type.IsPointer
            || type.IsByRef
            || type.IsGenericTypeDefinition
            || type.ContainsGenericParameters
            || typeof(Delegate).IsAssignableFrom(type)
            || Nullable.GetUnderlyingType(type) is not null
            || IsDictionary(type)
            || ElementTypeOf(type) is not null
            || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        if (!type.IsClass && !type.IsValueType && !type.IsInterface)
        {
            return false;
        }

        string? ns = type.Namespace;

        foreach (string excluded in ExcludedNamespaces)
        {
            if (ns is not null
                && (string.Equals(ns, excluded, StringComparison.Ordinal)
                    || ns.StartsWith(excluded + ".", StringComparison.Ordinal)))
            {
                return false;
            }
        }

        // Types in the framework's own assembly are left out whatever namespace they claim.
        if (type.Assembly == typeof(object).Assembly)
        {
            return false;
        }

        return !HasConverterAttribute(type);
    }

    /// <summary>The names reachable from the root without passing through a hidden type - per instance, because the hidden set is.</summary>
    private HashSet<string> ReachableNames()
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Type> { structure.Root };
        var queue = new Queue<Type>();

        queue.Enqueue(structure.Root);

        while (queue.Count > 0)
        {
            Type type = queue.Dequeue();

            if (IsHidden(type) || !structure.Members.TryGetValue(type, out Dictionary<string, LinqMember>? own))
            {
                continue;
            }

            foreach (LinqMember member in own.Values)
            {
                if (IsHidden(member.MemberType) || (member.ElementType is { } element && IsHidden(element)))
                {
                    continue;
                }

                reachable.Add(member.Member.Name);

                Type? next = member.Kind == LinqTypeKind.Object ? member.MemberType : member.ElementType;

                if (next is not null && structure.Members.ContainsKey(next) && visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    /// <summary>
    /// Whether Dynamic LINQ, asking <c>TypeDescriptor.GetConverter</c> for the type as it does, would get a converter
    /// that is not the framework's own: one declared with the attribute, one added at run time with
    /// <c>TypeDescriptor.AddAttributes</c>, or one a provider the host registered supplies.
    /// </summary>
    /// <remarks>
    /// Asked the way Dynamic LINQ asks it, so the two cannot disagree. That constructs the converter, once per type,
    /// here rather than on a visitor's text - the framework's own converters (the base one for a class,
    /// <c>EnumConverter</c>, <c>CollectionConverter</c>, <c>ArrayConverter</c>) all live in the assembly that defines
    /// <see cref="TypeConverter" />, and anything else is the host's. A converter registered after a type's graph is
    /// built is not seen until the process restarts.
    /// </remarks>
    private static bool HasConverterAttribute(Type type) =>
        type.GetCustomAttributes(typeof(TypeConverterAttribute), inherit: true).Length > 0
        || TypeDescriptor.GetConverter(type).GetType().Assembly != typeof(TypeConverter).Assembly;

    private static Type? TypeOf(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => null,
    };

    private static IEnumerable<MemberInfo> PublicInstanceMembers(Type type)
    {
        IEnumerable<PropertyInfo> properties = type.IsInterface
            ? type.GetInterfaces().Prepend(type).SelectMany(static x => x.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (PropertyInfo property in properties)
        {
            if (property.GetIndexParameters().Length == 0 && property.GetGetMethod() is { IsStatic: false })
            {
                yield return property;
            }
        }

        if (type.IsInterface)
        {
            yield break;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.IsStatic && !field.IsSpecialName)
            {
                yield return field;
            }
        }
    }

    private static Structure Build(Type root)
    {
        var members = new Dictionary<Type, Dictionary<string, LinqMember>>();
        var enums = new HashSet<Type>();
        var hint = new List<string>();
        var queue = new Queue<(Type Type, int Depth)>();
        var seen = new HashSet<Type> { root };
        int count = 0;

        queue.Enqueue((root, 0));

        while (queue.Count > 0 && members.Count < LinqQueryLimits.GraphMaxTypes)
        {
            (Type type, int depth) = queue.Dequeue();
            var own = new Dictionary<string, LinqMember>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            members[type] = own;

            foreach (MemberInfo member in PublicInstanceMembers(type))
            {
                if (count >= LinqQueryLimits.GraphMaxMembers)
                {
                    break;
                }

                // Two members whose names differ only in case, or one hiding another with `new`, are a name
                // Dynamic LINQ - which matches without case - cannot resolve to one member. Neither is offered.
                if (!spelled.Add(member.Name))
                {
                    ambiguous.Add(member.Name);
                    continue;
                }

                Type? memberType = TypeOf(member);
                if (memberType is null)
                {
                    continue;
                }

                LinqTypeKind kind = Classify(memberType);
                if (kind == LinqTypeKind.Excluded)
                {
                    continue;
                }

                Type? element = kind == LinqTypeKind.Collection ? ElementTypeOf(memberType) : null;

                own[member.Name] = new LinqMember(member, memberType, kind, element);
                count++;

                if ((EnumOf(memberType) ?? (element is null ? null : EnumOf(element))) is { } enumType)
                {
                    enums.Add(enumType);
                }

                if (type == root && kind == LinqTypeKind.Scalar && hint.Count < 12)
                {
                    hint.Add(member.Name);
                }

                Type? next = kind == LinqTypeKind.Object
                    ? memberType
                    : element is not null && Classify(element) == LinqTypeKind.Object ? element : null;

                if (next is not null && depth + 1 <= LinqQueryLimits.GraphMaxDepth && seen.Add(next))
                {
                    queue.Enqueue((next, depth + 1));
                }
            }

            foreach (string name in ambiguous)
            {
                foreach (string key in own.Keys.Where(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    own.Remove(key);
                }
            }
        }

        return new Structure(root, members, enums, hint);
    }

    private static Type? EnumOf(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsEnum ? underlying : null;
    }

    private sealed record Structure(
        Type Root,
        Dictionary<Type, Dictionary<string, LinqMember>> Members,
        HashSet<Type> Enums,
        List<string> Hint);
}
