using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Query;

/// <summary>The one member <see cref="DynamicLinqAdapter.ReadsDatesInServerTime" /> probes Dynamic LINQ with.</summary>
internal sealed class LinqDateProbe
{
    /// <summary>A date, compared with a literal.</summary>
    public DateTime When { get; set; }
}

/// <summary>What one <see cref="DynamicLinqAdapter.Apply{T}" /> call came back with.</summary>
/// <param name="Query">The composed query, when the text was accepted.</param>
/// <param name="Refusal">Why it was not, with JasperFx's own reason and position.</param>
internal sealed record DynamicLinqApplied<T>(IQueryable<T>? Query, LinqRefusal? Refusal);

/// <summary>
/// JasperFx's <c>JasperFx.Linq.DynamicQuery</c>, reached by reflection - the one adapter over it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection.</b> <c>DynamicQuery</c> first ships in JasperFx 2.84.0, which first ships in Marten 9.48.0,
/// and the studio compiles against its floor, Marten 9.39.0 (JasperFx 2.74.0), where none of these types exist.
/// The mode lights up only where the F1 probe (<see cref="RuntimeFeature.DynamicLinqQuery" />) finds the type, and
/// this adapter then resolves every member it needs by name and signature, once. A member that is missing or has
/// changed shape makes the whole adapter absent - the mode says it does not recognise this JasperFx - rather than
/// a call that fails half-way. Read at JasperFx <c>V2.84.0</c> and <c>V2.87.1</c> (identical in
/// <c>src/JasperFx/Linq</c>, and still so at <c>V2.89.0</c>), and again at <c>v2.91.0</c>, where only
/// <c>DynamicQuery.cs</c>'s date handling moved (jasperfx#1024, <see cref="ReadsDatesInServerTime" />) and every
/// member below is unchanged:
/// </para>
/// <list type="bullet">
/// <item><description><c>static IQueryable&lt;T&gt; DynamicQuery.Apply&lt;T&gt;(IQueryable&lt;T&gt;, DynamicQueryText, DynamicQueryPolicy?)</c></description></item>
/// <item><description><c>record DynamicQueryText(string? Where, string? OrderBy = null, IReadOnlyList&lt;object?&gt;? Arguments = null)</c></description></item>
/// <item><description><c>record DynamicQueryPolicy</c> with <c>static Default</c>, <c>init</c> properties <c>MaxTextLength</c>, <c>MaxNodes</c>, <c>MaxArguments</c>, and <c>WithRules(params IDynamicQueryShapeRule[])</c></description></item>
/// <item><description><c>static DynamicQueryShapeRules.For(Func&lt;Expression, string?&gt;)</c> and <c>SqlNullSemantics()</c></description></item>
/// <item><description><c>class DynamicQueryException</c> with <c>Clause</c>, <c>Reason</c> and <c>int? Position</c></description></item>
/// </list>
/// <para>
/// <b>The policy is the strictest the feature can live with</b>, and new for every call because its rules keep
/// per-parse state: <see cref="LinqQueryLimits" />' caps in place of JasperFx's defaults, no arguments at all
/// (<c>MaxArguments = 0</c> - the studio never passes an <c>@n</c> value, and the screen refuses the placeholder),
/// the studio's guard as the <em>first</em> shape rule (<see cref="LinqExpressionGuard.RuleFor" />) and JasperFx's
/// <c>SqlNullSemantics</c> after it, for the predicate. JasperFx's own built-in allow-list cannot be turned off and
/// still runs, so every clause is held to both lists.
/// </para>
/// <para>
/// <b>And the library underneath is checked too.</b> JasperFx 2.84 depends on System.Linq.Dynamic.Core 1.7.4; a host
/// that forced an older one past NuGet's downgrade error would be parsing with a library that has had code-execution
/// advisories (CVE-2023-32571, fixed in 1.3.0; CVE-2024-51417, fixed in 1.6.0). Below
/// <see cref="LinqQueryLimits.MinimumDynamicLinq" />, or when its version cannot be read, the adapter is absent.
/// </para>
/// </remarks>
internal sealed class DynamicLinqAdapter
{
    private static readonly Lazy<DynamicLinqAdapter?> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly MethodInfo applyOfT;
    private readonly ConstructorInfo textConstructor;
    private readonly PropertyInfo policyDefault;
    private readonly MethodInfo withRules;
    private readonly PropertyInfo maxTextLength;
    private readonly PropertyInfo maxNodes;
    private readonly PropertyInfo maxArguments;
    private readonly MethodInfo ruleFor;
    private readonly MethodInfo sqlNullSemantics;
    private readonly Type ruleType;
    private readonly Type exceptionType;
    private readonly PropertyInfo exceptionClause;
    private readonly PropertyInfo exceptionReason;
    private readonly PropertyInfo exceptionPosition;

    private DynamicLinqAdapter(
        MethodInfo applyOfT,
        ConstructorInfo textConstructor,
        PropertyInfo policyDefault,
        MethodInfo withRules,
        PropertyInfo maxTextLength,
        PropertyInfo maxNodes,
        PropertyInfo maxArguments,
        MethodInfo ruleFor,
        MethodInfo sqlNullSemantics,
        Type ruleType,
        Type exceptionType,
        PropertyInfo exceptionClause,
        PropertyInfo exceptionReason,
        PropertyInfo exceptionPosition,
        Version dynamicLinqVersion)
    {
        this.applyOfT = applyOfT;
        this.textConstructor = textConstructor;
        this.policyDefault = policyDefault;
        this.withRules = withRules;
        this.maxTextLength = maxTextLength;
        this.maxNodes = maxNodes;
        this.maxArguments = maxArguments;
        this.ruleFor = ruleFor;
        this.sqlNullSemantics = sqlNullSemantics;
        this.ruleType = ruleType;
        this.exceptionType = exceptionType;
        this.exceptionClause = exceptionClause;
        this.exceptionReason = exceptionReason;
        this.exceptionPosition = exceptionPosition;
        DynamicLinqVersion = dynamicLinqVersion;
        datesInServerTime = new Lazy<bool>(ProbeDates, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<bool> datesInServerTime;

    /// <summary>
    /// Whether this JasperFx reads a date literal with no offset, compared with a <see cref="DateTime" /> member, in
    /// this server's time zone - measured, once, rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// JasperFx means every date literal to be UTC unless it carries an offset, and asks Dynamic LINQ for that twice:
    /// <c>DateTimeIsParsedAsUTC = true</c>, and a <c>UtcDateConverter</c> of its own for <see cref="DateTime" /> and
    /// <see cref="DateTimeOffset" />. For a <see cref="DateTime" /> the first one wins - Dynamic LINQ 1.7.4's
    /// <c>TypeConverterFactory.GetConverter</c> returns its <c>CustomDateTimeConverter</c> before it looks at the
    /// configured converters - and that converter is a <c>DateTimeOffsetConverter</c>, which reads text with no offset
    /// in the server's zone before converting it to UTC. So <c>"2024-01-01"</c> becomes 2023-12-31T22:00Z on a host
    /// two hours east of Greenwich (measured at JasperFx 2.87.1, Marten 9.50.2), which is the very trap JasperFx's
    /// comment says the converter closes. That is JasperFx 2.84.0 through 2.90.2.
    /// </para>
    /// <para>
    /// JasperFx 2.91.0 (jasperfx#1024, Marten 9.51.0) turns <c>DateTimeIsParsedAsUTC</c> off, so its own UTC converter
    /// answers for <see cref="DateTime" /> too, and this answers <see langword="false" /> there whatever the server's
    /// zone - the correction goes idle by itself, with no version check of the studio's own
    /// (<c>DynamicLinqAdapterTests</c> pins that at 2.91.0 and later).
    /// </para>
    /// <para>
    /// Two dates are probed, one in each half of the year, because a zone such as Europe/London is UTC in January
    /// and not in July. Where neither moves, a JasperFx that fixed this is not "corrected" a second time.
    /// </para>
    /// </remarks>
    public bool ReadsDatesInServerTime => datesInServerTime.Value;

    private bool ProbeDates()
    {
        foreach (string text in (string[]) ["2000-01-15T00:00:00", "2000-07-15T00:00:00"])
        {
            DateTime meant = DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

            DynamicLinqApplied<LinqDateProbe> applied = Invoke(
                Array.Empty<LinqDateProbe>().AsQueryable(), "When == \"" + text + "\"", null, policyDefault.GetValue(null)!, LinqClause.Where);

            if (applied.Query is null || DateConstant.Find(applied.Query.Expression) is not { } read)
            {
                // Unreadable: assume the behaviour every JasperFx with a DynamicQuery has had.
                return true;
            }

            if (read != meant)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first <see cref="DateTime" /> constant in an expression.</summary>
    private sealed class DateConstant : ExpressionVisitor
    {
        private DateTime? found;

        public static DateTime? Find(Expression expression)
        {
            var visitor = new DateConstant();
            visitor.Visit(expression);
            return visitor.found;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (found is null && node.Value is DateTime value)
            {
                found = value;
            }

            return node;
        }
    }

    /// <summary>The adapter, or <see langword="null" /> when this process has no <c>DynamicQuery</c> the studio recognises.</summary>
    public static DynamicLinqAdapter? Current => Resolved.Value;

    /// <summary>Why <see cref="Current" /> is <see langword="null" />, for the page's one line.</summary>
    public static (LinqAvailability Availability, string? Message) Availability(MartenRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            return (LinqAvailability.NoDynamicCode,
                "LINQ mode needs run-time code generation, which this process does not have (Native AOT).");
        }

        if (!RuntimeFeature.DynamicLinqQuery.IsAvailable(runtime))
        {
            return (LinqAvailability.MartenTooOld,
                "LINQ mode needs " + RuntimeFeature.DynamicLinqQuery.Requirement + "; this host runs " +
                runtime.Describe(RuntimePackage.Marten) + " (" + runtime.Describe(RuntimePackage.JasperFx) + ").");
        }

        if (Current is null)
        {
            return (LinqAvailability.Unrecognised,
                "LINQ mode is not available: the JasperFx DynamicQuery or System.Linq.Dynamic.Core this host loaded (" +
                runtime.Describe(RuntimePackage.JasperFx) + ") is not one the studio recognises, or System.Linq.Dynamic.Core is older than " +
                LinqQueryLimits.MinimumDynamicLinq.ToString(3) + ".");
        }

        if (!MartenLinqCommand.IsAvailable)
        {
            return (LinqAvailability.Unrecognised,
                "LINQ mode is not available: " + runtime.Describe(RuntimePackage.Marten) +
                " does not build LINQ statements the way the studio knows how to read without creating tables.");
        }

        return (LinqAvailability.Available, null);
    }

    /// <summary>The System.Linq.Dynamic.Core version this process loaded.</summary>
    public Version DynamicLinqVersion { get; }

    /// <summary>
    /// Composes <paramref name="where" /> and then <paramref name="orderBy" /> onto <paramref name="source" />,
    /// each under its own policy - as Marten's own diagnostics read does, so that a rule sees which clause it is in.
    /// </summary>
    /// <param name="source">The queryable to compose onto.</param>
    /// <param name="where">The predicate text, already through <see cref="LinqTextScreen" />.</param>
    /// <param name="orderBy">The ordering text, already through <see cref="LinqTextScreen" />.</param>
    /// <param name="guard">The studio's guard, whose rule leads each policy.</param>
    public DynamicLinqApplied<T> Apply<T>(IQueryable<T> source, string? where, string? orderBy, LinqExpressionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(guard);

        IQueryable<T> query = source;

        if (!string.IsNullOrWhiteSpace(where))
        {
            object policy = Policy(LinqClause.Where, LinqQueryLimits.MaxWhereLength, guard, includeNullSemantics: true);
            DynamicLinqApplied<T> applied = Invoke(query, where, null, policy, LinqClause.Where);

            if (applied.Refusal is not null || applied.Query is null)
            {
                return applied;
            }

            query = applied.Query;
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            object policy = Policy(LinqClause.OrderBy, LinqQueryLimits.MaxOrderByLength, guard, includeNullSemantics: false);
            DynamicLinqApplied<T> applied = Invoke(query, null, orderBy, policy, LinqClause.OrderBy);

            if (applied.Refusal is not null || applied.Query is null)
            {
                return applied;
            }

            query = applied.Query;
        }

        return new DynamicLinqApplied<T>(query, null);
    }

    private DynamicLinqApplied<T> Invoke<T>(IQueryable<T> source, string? where, string? orderBy, object policy, LinqClause clause)
    {
        object text = textConstructor.Invoke([where, orderBy, null]);

        try
        {
            var composed = (IQueryable<T>) applyOfT.MakeGenericMethod(typeof(T)).Invoke(null, [source, text, policy])!;

            return new DynamicLinqApplied<T>(composed, null);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner)
        {
            if (exceptionType.IsInstanceOfType(inner))
            {
                string reason = exceptionReason.GetValue(inner) as string ?? "was not accepted.";
                int position = exceptionPosition.GetValue(inner) is int at ? at : -1;
                string which = exceptionClause.GetValue(inner)?.ToString() ?? clause.ToString();
                LinqClause reported = string.Equals(which, nameof(LinqClause.OrderBy), StringComparison.Ordinal)
                    ? LinqClause.OrderBy
                    : LinqClause.Where;

                // A refusal - a policy rule's, or JasperFx's allow-list - is a sentence fragment written to follow the
                // clause ("calls string.PadLeft, which …"); a parse failure is Dynamic LINQ's own sentence, which
                // JasperFx wraps with the parser's exception as the inner one.
                string subject = reported == LinqClause.OrderBy ? "The ordering" : "The predicate";
                string message = inner.InnerException is null
                    ? subject + " " + Capped(reason)
                    : subject + " could not be parsed: " + Capped(reason);

                return new DynamicLinqApplied<T>(null, new LinqRefusal(LinqStage.Parse, reported, message, position));
            }

            if (inner is OutOfMemoryException or OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
            }

            // Not JasperFx's refusal: a parser failure it did not wrap. Its words are not shown - they may name
            // internals - only that the text was not accepted.
            return new DynamicLinqApplied<T>(null, new LinqRefusal(
                LinqStage.Parse,
                clause,
                (clause == LinqClause.OrderBy ? "The ordering" : "The predicate") +
                " could not be parsed (" + inner.GetType().Name + ")."));
        }
    }

    private object Policy(LinqClause clause, int textLimit, LinqExpressionGuard guard, bool includeNullSemantics)
    {
        object studioRule = ruleFor.Invoke(null, [guard.RuleFor(clause)])!;

        Array rules = Array.CreateInstance(ruleType, includeNullSemantics ? 2 : 1);
        rules.SetValue(studioRule, 0);

        if (includeNullSemantics)
        {
            rules.SetValue(sqlNullSemantics.Invoke(null, null), 1);
        }

        // WithRules returns a fresh copy of Default (`this with { ShapeRules = … }`), so setting its init-only
        // caps here changes that copy and never the shared Default.
        object policy = withRules.Invoke(policyDefault.GetValue(null), [rules])!;

        maxTextLength.SetValue(policy, textLimit);
        maxNodes.SetValue(policy, LinqQueryLimits.MaxNodes);
        maxArguments.SetValue(policy, 0);

        return policy;
    }

    /// <summary>The caps and the number of shape rules of the policy one clause is parsed under - read back, for a test.</summary>
    internal (int MaxTextLength, int MaxNodes, int MaxArguments, int Rules) DescribePolicy(LinqClause clause, LinqExpressionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        object policy = clause == LinqClause.OrderBy
            ? Policy(LinqClause.OrderBy, LinqQueryLimits.MaxOrderByLength, guard, includeNullSemantics: false)
            : Policy(LinqClause.Where, LinqQueryLimits.MaxWhereLength, guard, includeNullSemantics: true);

        int rules = policy.GetType().GetProperty("ShapeRules")?.GetValue(policy) is System.Collections.IEnumerable list
            ? list.Cast<object>().Count()
            : -1;

        return ((int) maxTextLength.GetValue(policy)!, (int) maxNodes.GetValue(policy)!, (int) maxArguments.GetValue(policy)!, rules);
    }

    private static string Capped(string reason) =>
        reason.Length <= 400 ? reason : reason[..400] + "…";

    private static DynamicLinqAdapter? Resolve()
    {
        try
        {
            Assembly jasperFx = typeof(JasperFx.AutoCreate).Assembly;

            Type? dynamicQuery = jasperFx.GetType("JasperFx.Linq.DynamicQuery", throwOnError: false);
            Type? text = jasperFx.GetType("JasperFx.Linq.DynamicQueryText", throwOnError: false);
            Type? policy = jasperFx.GetType("JasperFx.Linq.DynamicQueryPolicy", throwOnError: false);
            Type? rules = jasperFx.GetType("JasperFx.Linq.DynamicQueryShapeRules", throwOnError: false);
            Type? rule = jasperFx.GetType("JasperFx.Linq.IDynamicQueryShapeRule", throwOnError: false);
            Type? refusal = jasperFx.GetType("JasperFx.Linq.DynamicQueryException", throwOnError: false);

            if (dynamicQuery is null || text is null || policy is null || rules is null || rule is null || refusal is null)
            {
                return null;
            }

            MethodInfo? apply = dynamicQuery.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == "Apply"
                && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ } first, { } second, { } third]
                && first.ParameterType.IsGenericType
                && first.ParameterType.GetGenericTypeDefinition() == typeof(IQueryable<>)
                && second.ParameterType == text
                && third.ParameterType == policy);

            ConstructorInfo? textConstructor = text.GetConstructor([typeof(string), typeof(string), typeof(IReadOnlyList<object>)]);
            PropertyInfo? defaultPolicy = policy.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? withRules = policy.GetMethod("WithRules", BindingFlags.Public | BindingFlags.Instance, [rule.MakeArrayType()]);
            PropertyInfo? maxText = policy.GetProperty("MaxTextLength", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? maxNodes = policy.GetProperty("MaxNodes", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? maxArguments = policy.GetProperty("MaxArguments", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? ruleFor = rules.GetMethod("For", BindingFlags.Public | BindingFlags.Static, [typeof(Func<Expression, string>)]);
            MethodInfo? nullSemantics = rules.GetMethod("SqlNullSemantics", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            PropertyInfo? clause = refusal.GetProperty("Clause", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? reason = refusal.GetProperty("Reason", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? position = refusal.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);

            if (apply is null || textConstructor is null || defaultPolicy is null || withRules is null
                || !IsWritableInt(maxText) || !IsWritableInt(maxNodes) || !IsWritableInt(maxArguments)
                || ruleFor is null || !rule.IsAssignableFrom(ruleFor.ReturnType)
                || nullSemantics is null || !rule.IsAssignableFrom(nullSemantics.ReturnType)
                || clause is null || reason?.PropertyType != typeof(string) || position?.PropertyType != typeof(int?))
            {
                return null;
            }

            if (DynamicLinqVersionLoaded() is not { } version || version < LinqQueryLimits.MinimumDynamicLinq)
            {
                return null;
            }

            return new DynamicLinqAdapter(
                apply, textConstructor, defaultPolicy, withRules, maxText!, maxNodes!, maxArguments!, ruleFor, nullSemantics,
                rule, refusal, clause, reason!, position!, version);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool IsWritableInt(PropertyInfo? property) =>
        property is { CanWrite: true } && property.PropertyType == typeof(int);

    /// <summary>
    /// The System.Linq.Dynamic.Core JasperFx references, as it was loaded - read from its informational version, as
    /// <see cref="MartenRuntime" /> reads every package's.
    /// </summary>
    private static Version? DynamicLinqVersionLoaded()
    {
        AssemblyName? reference = typeof(JasperFx.AutoCreate).Assembly.GetReferencedAssemblies()
            .FirstOrDefault(static x => string.Equals(x.Name, "System.Linq.Dynamic.Core", StringComparison.Ordinal));

        if (reference is null)
        {
            return null;
        }

        Assembly loaded = Assembly.Load(reference);

        return MartenRuntime.ParseVersion(
            loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
}
