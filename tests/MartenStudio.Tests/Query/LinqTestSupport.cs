using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Marten;

using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>How an order stands. Stored as a number unless a test's store says otherwise.</summary>
public enum LinqOrderStatus
{
    Open,
    Shipped,
    Cancelled,
}

/// <summary>A nested object of the host's own: part of the document's graph.</summary>
public sealed class LinqCustomer
{
    public string Name { get; set; } = string.Empty;

    public string? City { get; set; }
}

/// <summary>An element of a collection of objects.</summary>
public sealed class LinqLine
{
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

/// <summary>A document type the tests hide with <c>IsDocumentTypeVisible</c>.</summary>
public sealed class LinqSecret
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// A type with a converter of the host's own. Dynamic LINQ would run the converter on a string literal compared with
/// a member of this type while it parses, so the graph leaves the type out; <see cref="LinqConverterProbe" /> proves
/// the converter never runs.
/// </summary>
[TypeConverter(typeof(LinqConvertedConverter))]
public sealed class LinqConverted
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>Counts every conversion it is asked for.</summary>
public sealed class LinqConvertedConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        LinqConverterProbe.Record();
        return new LinqConverted { Value = value as string ?? string.Empty };
    }
}

/// <summary>How many times any <see cref="LinqConvertedConverter" /> ran.</summary>
public static class LinqConverterProbe
{
    private static int conversions;

    public static int Conversions => Volatile.Read(ref conversions);

    public static void Record() => Interlocked.Increment(ref conversions);
}

/// <summary>The document most tests query: scalars, a nested object, collections, and members the graph must leave out.</summary>
public sealed class LinqOrder
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public decimal Total { get; set; }

    public LinqOrderStatus Status { get; set; }

    public DateTime PlacedAt { get; set; }

    public bool Rush { get; set; }

    public int? Priority { get; set; }

    public LinqCustomer Customer { get; set; } = new();

    public string[] Tags { get; set; } = [];

    public List<LinqLine> Lines { get; set; } = [];

    // Left out of the graph - each for its own reason.
    public Type? Kind { get; set; }

    public object? Payload { get; set; }

    public Dictionary<string, string> Attributes { get; set; } = [];

    public JsonElement Raw { get; set; }

    public LinqConverted Converted { get; set; } = new();

    public LinqSecret? Secret { get; set; }
}

/// <summary>
/// A document whose members are named like the framework types an attacker reaches for, so the text screen lets
/// those names through and the later walls are what refuse.
/// </summary>
public sealed class LinqDecoy
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string System { get; set; } = string.Empty;

    public string IO { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public string Math { get; set; } = string.Empty;

    public string Convert { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public string DateTime { get; set; } = string.Empty;

    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// A static method a hostile expression calls. It does nothing but count, per token, so parallel tests cannot see each
/// other's calls - which is what lets a test prove both that Marten would run it and that the guard stops it first.
/// </summary>
public static class HostileProbe
{
    private static readonly ConcurrentDictionary<string, int> Calls = new(StringComparer.Ordinal);

    public static string Fire(string token)
    {
        Calls.AddOrUpdate(token, 1, static (_, count) => count + 1);
        return "fired";
    }

    public static int Fired(string token) => Calls.TryGetValue(token, out int count) ? count : 0;

    public static string NewToken() => Guid.NewGuid().ToString("N");
}

/// <summary>Stores that never connect, for the parts of LINQ mode that need a Marten but no database.</summary>
internal static class LinqTestStore
{
    /// <summary>A host that does not resolve: <c>DocumentStore.For</c> opens no connection, and nothing here runs anything.</summary>
    public const string Unreachable =
        "Host=marten-studio-linq-tests.invalid;Port=5432;Database=none;Username=none;Password=none";

    public const string Schema = "studio_linq";

    public static DocumentStore Create(Action<StoreOptions>? configure = null) => DocumentStore.For(options =>
    {
        options.Connection(Unreachable);
        options.DatabaseSchemaName = Schema;
        options.Schema.For<LinqOrder>();
        options.Schema.For<LinqDecoy>();
        options.Schema.For<LinqSecret>();
        configure?.Invoke(options);
    });

    public static LinqDocumentGraph Graph<T>(params Type[] hidden) => LinqDocumentGraph.For(typeof(T), new HashSet<Type>(hidden));

    public static LinqExpressionGuard Guard<T>(bool jsonEnumsAsName = false, params Type[] hidden) =>
        new(Graph<T>(hidden), static _ => null, jsonEnumsAsName);
}

/// <summary>Whether this run's Marten has JasperFx's DynamicQuery - 9.48 and later.</summary>
public static class DynamicLinqAvailability
{
    public const string SkipReason =
        "JasperFx's DynamicQuery arrives with Marten 9.48 (JasperFx 2.84) and this run resolved an older Marten - the " +
        "floor. Run the suite at the latest with -p:MartenStudioMartenVersion=9.51.0 (or dotnet fallout Test --marten-version latest).";

    public static bool IsAvailable => DynamicLinqAdapter.Current is not null && MartenLinqCommand.IsAvailable;
}

/// <summary>A fact that needs JasperFx's DynamicQuery, skipped with the command that would run it at the floor.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DynamicLinqFactAttribute : FactAttribute
{
    public DynamicLinqFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DynamicLinqAvailability.SkipReason;
        SkipType = typeof(DynamicLinqAvailability);
        SkipUnless = nameof(DynamicLinqAvailability.IsAvailable);
    }
}

/// <summary>A theory that needs JasperFx's DynamicQuery.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DynamicLinqTheoryAttribute : TheoryAttribute
{
    public DynamicLinqTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DynamicLinqAvailability.SkipReason;
        SkipType = typeof(DynamicLinqAvailability);
        SkipUnless = nameof(DynamicLinqAvailability.IsAvailable);
    }
}
