using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text;

using PostgresStudio.Services.Health;

using Npgsql;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// Reads an application's Npgsql settings off the data source's connection string: every keyword of the loaded Npgsql's
/// builder, where its value came from, and nothing that is a secret (NP-DESIGN §2, §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The source string never leaves this class.</b> It is parsed in memory by <see cref="NpgsqlConnectionStringBuilder" />
/// and dropped. The model holds values the catalogue lets be shown, <c>set</c> / <c>not set</c> for a secret, and nothing
/// else; a parse failure keeps the exception's type and never its message, which can quote the value Npgsql rejected.
/// Nothing here logs.
/// </para>
/// <para>
/// <b>Keywords come from the loaded builder</b>: its public properties that carry a <c>[DisplayName]</c>, grouped by
/// <c>[Category]</c>, read through their getters - never <c>ToString()</c> of the builder, which would print the password. A
/// keyword the loaded Npgsql has and <see cref="NpgsqlSettingsCatalog" /> does not is listed by name, its value withheld.
/// </para>
/// <para>
/// <b>Explicit or default</b> is whether the builder's underlying dictionary holds the keyword
/// (<see cref="DbConnectionStringBuilder.ShouldSerialize(string)" /> on the canonical name). Not
/// <see cref="DbConnectionStringBuilder.ContainsKey(string)" />: Npgsql overrides it to answer for every keyword it
/// <i>knows</i>, set or not (<c>NpgsqlConnectionStringBuilder.GeneratedActions</c>, <c>GeneratedAction.GetCanonical</c>, in
/// <c>v9.0.4</c> and <c>v10.0.3</c>), so NP-DESIGN §3's "explicit comes from <c>ContainsKey</c>" does not hold.
/// </para>
/// <para>
/// <b>The environment</b> is consulted only for the variables the catalogue lists for the loaded Npgsql major, in the order
/// Npgsql applies them: the connection string, then the variable (<c>Settings.X ?? PostgresEnvironment.X</c>). The default
/// password file and the default certificate files are never probed.
/// </para>
/// </remarks>
internal static class NpgsqlSettingsReader
{
    /// <summary>The newest Npgsql major the catalogue's environment table covers.</summary>
    public const int KnownMajor = 10;

    private const int MaxValueLength = 256;

    private const int MaxOptionNames = 50;

    private static readonly Lazy<List<BuilderProperty>> BuilderProperties = new(DiscoverProperties);

    /// <summary>The loaded Npgsql's major version.</summary>
    public static int LoadedMajor { get; } = typeof(NpgsqlConnection).Assembly.GetName().Version?.Major ?? KnownMajor;

    /// <summary>The loaded Npgsql's version as it spells itself: <c>9.0.4</c>.</summary>
    public static string LoadedVersion { get; } = VersionOfLoadedNpgsql();

    /// <summary>The settings of <paramref name="dataSource" />, or the unreadable result for a data source that is not there.</summary>
    /// <param name="dataSource">The data source whose connection string is read and dropped.</param>
    /// <param name="context">The environment and the facts the hints need.</param>
    public static ConnectionSettings Read(NpgsqlDataSource? dataSource, ConnectionReadContext context) =>
        Read(dataSource?.ConnectionString, context);

    /// <summary>The settings of a connection string, parsed in memory and not kept.</summary>
    /// <param name="connectionString">The data source's connection string, as Npgsql keeps it (no password unless <c>Persist Security Info</c>).</param>
    /// <param name="context">The environment and the facts the hints need.</param>
    public static ConnectionSettings Read(string? connectionString, ConnectionReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int major = context.NpgsqlMajor ?? LoadedMajor;
        ConnectionSettings empty = new()
        {
            NpgsqlVersion = LoadedVersion,
            NpgsqlMajor = major,
            NewerThanStudioKnows = major > KnownMajor,
            Notes = Notes(major),
        };

        if (connectionString is null)
        {
            return empty with { Readable = false, UnreadableReason = "no connection string" };
        }

        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The type only: Npgsql's message can quote the value it rejected, and a value can be a password.
            return empty with { Readable = false, UnreadableReason = exception.GetType().Name };
        }

        ConnectionSettings settings = Build(builder, empty, major, context);

        return settings with { Hints = ConnectionHints.Evaluate(settings, context) };
    }

    /// <summary>
    /// The names among the <c>Options</c> the connection string (or <c>PGOPTIONS</c>) sets that are worth asking the server
    /// about: lower-case, no custom (dotted) setting, at most 50. For the observed read's <c>pg_settings</c> check; the
    /// names are shown on the page anyway, and the connection string stays here.
    /// </summary>
    /// <param name="connectionString">The data source's connection string.</param>
    /// <param name="environment">The process environment.</param>
    /// <param name="npgsqlMajor">The Npgsql major, or <see langword="null" /> for the loaded one.</param>
    public static IReadOnlyList<string> OptionNames(string? connectionString, IProcessEnvironment environment, int? npgsqlMajor = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (connectionString is null)
        {
            return [];
        }

        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            string? options = builder.Options;

            if (string.IsNullOrWhiteSpace(options) && NpgsqlSettingsCatalog.Find("Options")?.EnvironmentFor(npgsqlMajor ?? LoadedMajor) is { } variable)
            {
                options = environment.Get(variable);
            }

            return string.IsNullOrWhiteSpace(options)
                ? []
                : [.. ParseOptions(options, serverNames: null).Items.Select(static x => x.Name).Where(static x => !x.Contains('.', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(MaxOptionNames)];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses an <c>Options</c> string (<c>-c name=value</c>, <c>-cname=value</c>, <c>--name=value</c>; a backslash escapes a
    /// space) into the settings it names. Pure.
    /// </summary>
    /// <param name="options">The options text.</param>
    /// <param name="serverNames">The names that exist in <c>pg_settings</c>, or <see langword="null" /> when the server was not asked.</param>
    public static ConnectionOptions ParseOptions(string options, IReadOnlySet<string>? serverNames)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<ConnectionOption> items = [];
        bool other = false;
        List<string> tokens = Tokenize(options);

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            string? assignment = token switch
            {
                "-c" when i + 1 < tokens.Count => tokens[++i],
                _ when token.StartsWith("--", StringComparison.Ordinal) => token[2..],
                _ when token.StartsWith("-c", StringComparison.Ordinal) && token.Length > 2 => token[2..],
                _ => null,
            };

            int equals = assignment?.IndexOf('=', StringComparison.Ordinal) ?? -1;

            if (assignment is null || equals <= 0 || !IsSettingName(assignment.AsSpan(0, equals)))
            {
                other = true;
                continue;
            }

            string name = assignment[..equals].Replace('-', '_').ToLowerInvariant();
            string value = assignment[(equals + 1)..];

            if (name.Contains('.', StringComparison.Ordinal))
            {
                items.Add(new(name, null, "a custom setting's value is not shown"));
            }
            else if (serverNames is null)
            {
                items.Add(new(name, null, "not checked against the server's settings"));
            }
            else if (!serverNames.Contains(name))
            {
                items.Add(new(name, null, "not a server setting the studio could confirm"));
            }
            else
            {
                items.Add(new(name, Bound(value), null));
            }
        }

        return new ConnectionOptions(items, other);
    }

    private static ConnectionSettings Build(NpgsqlConnectionStringBuilder builder, ConnectionSettings empty, int major, ConnectionReadContext context)
    {
        IProcessEnvironment environment = context.Environment;
        NpgsqlConnectionStringBuilder fresh = new();
        List<ConnectionSettingRow> rows = [];
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        ConnectionOptions? options = null;
        bool passwordEnvironmentSet = false;
        bool sslPasswordSet = false;
        bool persistSecurityInfo = ReadOrDefault(builder, "Persist Security Info") is true;

        // A pasted string's explicit keywords say "pasted" where the data source's say "connection string", and its Options
        // values are never looked up against the server: the sanitiser has taken them out already (PastedConnectionString).
        PastedStrip? pasted = context.Pasted;
        ConnectionSettingOrigin explicitOrigin = pasted is null ? ConnectionSettingOrigin.ConnectionString : ConnectionSettingOrigin.Pasted;
        IReadOnlySet<string>? optionServerNames = pasted is null ? context.ServerSettingNames : null;

        // The hosts first: PGTARGETSESSIONATTRS applies only to a data source with more than one.
        string? hostText = ReadOrDefault(builder, "Host") as string;
        int port = ReadOrDefault(builder, "Port") is int p ? p : 5432;
        IReadOnlyList<(string Host, int Port)> endpoints = DataSourceEndpoints.Parse(hostText, port);

        foreach (BuilderProperty property in BuilderProperties.Value)
        {
            NpgsqlSettingEntry? entry = NpgsqlSettingsCatalog.Find(property.Keyword);
            bool explicitly = ((DbConnectionStringBuilder) builder).ShouldSerialize(property.Keyword);
            IReadOnlyList<string> hintIds = entry?.Hints ?? [];

            if (entry is null)
            {
                rows.Add(new(property.Keyword, property.Category, explicitly ? explicitOrigin : ConnectionSettingOrigin.Default,
                    "value withheld: this studio does not know this setting", null, null, null, property.Obsolete, explicitly, hintIds));
                continue;
            }

            string? variable = entry.EnvironmentFor(major);
            string? note = null;

            switch (entry.Exposure)
            {
                case NpgsqlSettingExposure.Secret:
                    rows.Add(SecretRow(property, entry, builder, explicitly, persistSecurityInfo, pasted, environment, variable, hintIds, out bool environmentSet));

                    if (string.Equals(property.Keyword, "Password", StringComparison.OrdinalIgnoreCase))
                    {
                        passwordEnvironmentSet = environmentSet;
                    }
                    else
                    {
                        sslPasswordSet = pasted is null
                            ? explicitly && HasText(SafeGet(property, builder, out _))
                            : pasted.RemovedSecrets.Contains(property.Keyword);
                    }

                    continue;

                case NpgsqlSettingExposure.Options:
                {
                    string? written = explicitly ? builder.Options : null;
                    ConnectionSettingOrigin origin = explicitOrigin;

                    if (string.IsNullOrWhiteSpace(written) && variable is not null && environment.Get(variable) is { Length: > 0 } fromEnvironment)
                    {
                        written = fromEnvironment;
                        origin = ConnectionSettingOrigin.Environment;
                    }

                    if (string.IsNullOrWhiteSpace(written))
                    {
                        rows.Add(new(property.Keyword, property.Category, ConnectionSettingOrigin.Default, "not set", entry.Exposure, variable, null, property.Obsolete, explicitly, hintIds));
                        continue;
                    }

                    options = ParseOptions(written, optionServerNames);
                    rows.Add(new(property.Keyword, property.Category, origin, options.Render(), entry.Exposure,
                        origin == ConnectionSettingOrigin.Environment ? variable : null,
                        pasted is not null && origin == explicitOrigin
                            ? "A setting's name is shown; the value of one in a pasted string never is."
                            : "A setting's name is always shown; its value only for a server setting, never for a custom one (a name with a dot).",
                        property.Obsolete, origin == explicitOrigin, hintIds));
                    continue;
                }
            }

            // Shown and Path: the connection string over the environment over Npgsql's default.
            object? effective;
            ConnectionSettingOrigin from;
            object? explicitValue = explicitly ? SafeGet(property, builder, out _) : null;
            bool unreadable = false;

            if (explicitly && HasValue(property.Keyword, explicitValue))
            {
                effective = explicitValue;
                from = explicitOrigin;
                unreadable = explicitValue is UnreadableValue;
            }
            else if (variable is not null
                && (!entry.MultiHostOnly || endpoints.Count > 1)
                && environment.Get(variable) is { Length: > 0 } fromEnvironment)
            {
                effective = fromEnvironment;
                from = ConnectionSettingOrigin.Environment;
            }
            else
            {
                effective = SafeGet(property, fresh, out _);
                from = ConnectionSettingOrigin.Default;
                unreadable = effective is UnreadableValue;
            }

            if (variable is not null && entry.MultiHostOnly && endpoints.Count <= 1)
            {
                note = variable + " is read only for a data source with more than one host.";
            }

            if (entry.Exposure == NpgsqlSettingExposure.Path && from == ConnectionSettingOrigin.Default)
            {
                note = DefaultFileNote(property.Keyword);
            }

            // A variable only a newer Npgsql reads (PGAPPNAME, PGGSSENCMODE, PGREQUIREAUTH before Npgsql 10): somebody set it believing
            // it does something. Only its name is said, and only while nothing else sets the keyword; its value is not read out.
            bool ignored = variable is null
                && entry.EnvironmentVariable is { } unread
                && from == ConnectionSettingOrigin.Default
                && environment.Get(unread) is { Length: > 0 };

            if (ignored)
            {
                note = IgnoredEnvironmentNote(entry.EnvironmentVariable!, major, entry.EnvironmentFromMajor, property.Keyword);
            }

            values[property.Keyword] = unreadable ? null : effective;

            rows.Add(new(
                property.Keyword,
                property.Category,
                unreadable ? ConnectionSettingOrigin.Unreadable : from,
                unreadable ? "unreadable (" + ((UnreadableValue) effective!).ExceptionType + ")" : Format(effective),
                entry.Exposure,
                from == ConnectionSettingOrigin.Environment ? variable : null,
                note,
                property.Obsolete,
                from == explicitOrigin,
                hintIds,
                EnvironmentIgnored: ignored));
        }

        rows.Sort(static (a, b) => NpgsqlSettingsCatalog.OrderOf(a.Keyword).CompareTo(NpgsqlSettingsCatalog.OrderOf(b.Keyword)));

        // A variable only a newer Npgsql reads, for a keyword this Npgsql does not even have (GSS Encryption Mode and Require Auth
        // before Npgsql 10): there is no row to put the note on, so the page says it once, above the table.
        List<string> notes = [.. empty.Notes];

        foreach (NpgsqlSettingEntry entry in NpgsqlSettingsCatalog.Entries)
        {
            if (entry.EnvironmentVariable is { } variable
                && entry.EnvironmentFor(major) is null
                && !rows.Exists(x => string.Equals(x.Keyword, entry.Keyword, StringComparison.OrdinalIgnoreCase))
                && environment.Get(variable) is { Length: > 0 })
            {
                notes.Add(variable + " is set, but Npgsql " + major.ToString(CultureInfo.InvariantCulture) + " does not read it (Npgsql "
                    + entry.EnvironmentFromMajor.ToString(CultureInfo.InvariantCulture) + " does), and this Npgsql has no " + entry.Keyword
                    + " setting to put in the connection string instead.");
            }
        }

        return empty with
        {
            Notes = notes,
            Rows = [.. rows.Select(row => WithDefault(row, fresh))],
            Values = new ConnectionValues(values),
            Endpoints = endpoints,
            PoolerSigns = PoolerSigns.Find(endpoints),
            PersistSecurityInfo = persistSecurityInfo,
            SslPasswordSet = sslPasswordSet,
            PasswordEnvironmentSet = passwordEnvironmentSet,
            Options = options,
        };
    }

    /// <summary>
    /// The row with Npgsql's own default for its keyword and the builder property that carries it. A secret's and
    /// <c>Options</c>' default is <c>not set</c>, whatever the loaded Npgsql says, and a keyword the studio does not know has no
    /// default shown, as it has no value.
    /// </summary>
    private static ConnectionSettingRow WithDefault(ConnectionSettingRow row, NpgsqlConnectionStringBuilder fresh)
    {
        BuilderProperty? property = BuilderProperties.Value.FirstOrDefault(x => string.Equals(x.Keyword, row.Keyword, StringComparison.OrdinalIgnoreCase));

        string? shown = row.Exposure switch
        {
            null => null,
            NpgsqlSettingExposure.Secret or NpgsqlSettingExposure.Options => "not set",
            _ when property is null => null,
            _ => SafeGet(property, fresh, out _) is { } value && value is not UnreadableValue ? Format(value) : "not set",
        };

        return row with { DefaultValue = shown, BuilderProperty = property?.Name };
    }

    private static ConnectionSettingRow SecretRow(
        BuilderProperty property,
        NpgsqlSettingEntry entry,
        NpgsqlConnectionStringBuilder builder,
        bool explicitly,
        bool persistSecurityInfo,
        PastedStrip? pasted,
        IProcessEnvironment environment,
        string? variable,
        IReadOnlyList<string> hintIds,
        out bool environmentSet)
    {
        environmentSet = variable is not null && environment.Get(variable) is { Length: > 0 };

        if (pasted is not null)
        {
            // The text had its secrets removed before the builder saw it, so what is known is only that it carried one: "set" says
            // so and nothing else, and the "not visible" of the data source's own Password row does not apply.
            if (pasted.RemovedSecrets.Contains(property.Keyword))
            {
                return new(property.Keyword, property.Category, ConnectionSettingOrigin.Pasted, "set", entry.Exposure, null,
                    "It was in the pasted text and was removed before anything was read.", property.Obsolete, true, hintIds);
            }

            if (environmentSet)
            {
                return new(property.Keyword, property.Category, ConnectionSettingOrigin.Environment, "set (" + variable + ")", entry.Exposure, variable, null, property.Obsolete, false, hintIds);
            }

            return new(property.Keyword, property.Category, ConnectionSettingOrigin.Default, "not set", entry.Exposure, null,
                string.Equals(property.Keyword, "Password", StringComparison.OrdinalIgnoreCase) ? DefaultFileNote(property.Keyword) : null,
                property.Obsolete, false, hintIds);
        }

        bool setHere = explicitly && HasText(SafeGet(property, builder, out _));

        if (string.Equals(property.Keyword, "Password", StringComparison.OrdinalIgnoreCase) && !persistSecurityInfo)
        {
            // Npgsql removes the password from NpgsqlDataSource.ConnectionString unless Persist Security Info is on
            // (NpgsqlDataSource ctor, ToStringWithoutPassword), so the studio cannot tell set from unset and claims neither.
            return new(
                property.Keyword,
                property.Category,
                ConnectionSettingOrigin.NotVisible,
                "not visible: Npgsql removes it from the data source's connection string",
                entry.Exposure,
                null,
                environmentSet
                    ? variable + " is set in this process's environment, and Npgsql uses it when the data source has no password of its own. " + DefaultFileNote(property.Keyword)
                    : DefaultFileNote(property.Keyword),
                property.Obsolete,
                explicitly,
                hintIds);
        }

        if (setHere)
        {
            return new(property.Keyword, property.Category, ConnectionSettingOrigin.ConnectionString, "set", entry.Exposure, null, null, property.Obsolete, true, hintIds);
        }

        if (environmentSet)
        {
            return new(property.Keyword, property.Category, ConnectionSettingOrigin.Environment, "set (" + variable + ")", entry.Exposure, variable, null, property.Obsolete, false, hintIds);
        }

        return new(
            property.Keyword,
            property.Category,
            ConnectionSettingOrigin.Default,
            "not set",
            entry.Exposure,
            null,
            string.Equals(property.Keyword, "Password", StringComparison.OrdinalIgnoreCase) ? DefaultFileNote(property.Keyword) : null,
            property.Obsolete,
            false,
            hintIds);
    }

    /// <summary>"PGAPPNAME is set, but Npgsql 9 does not read it (Npgsql 10 does): set Application Name in the connection string."</summary>
    internal static string IgnoredEnvironmentNote(string variable, int loadedMajor, int readsFromMajor, string keyword) =>
        variable + " is set, but Npgsql " + loadedMajor.ToString(CultureInfo.InvariantCulture) + " does not read it (Npgsql "
        + readsFromMajor.ToString(CultureInfo.InvariantCulture) + " does): set " + keyword + " in the connection string.";

    private static string DefaultFileNote(string keyword)
    {
        bool windows = OperatingSystem.IsWindows();

        return keyword.ToLowerInvariant() switch
        {
            "password" or "passfile" =>
                "When no password is configured Npgsql may read the default password file ("
                + (windows ? @"%APPDATA%\postgresql\pgpass.conf" : "~/.pgpass") + "). The studio never looks for it.",
            "ssl certificate" or "ssl key" or "root certificate" =>
                "When none is configured Npgsql may read a default file in " + (windows ? @"%APPDATA%\postgresql" : "~/.postgresql") + ". The studio never looks for it.",
            _ => string.Empty,
        };
    }

    private static List<string> Notes(int major)
    {
        List<string> notes = [];

        if (major > KnownMajor)
        {
            notes.Add("This Npgsql (" + LoadedVersion + ") is newer than the studio knows: environment fallbacks may be incomplete, and a keyword the studio does not know is listed with its value withheld.");
        }

        return notes;
    }

    private static object? ReadOrDefault(NpgsqlConnectionStringBuilder builder, string keyword)
    {
        BuilderProperty? property = BuilderProperties.Value.FirstOrDefault(x => string.Equals(x.Keyword, keyword, StringComparison.OrdinalIgnoreCase));

        if (property is null)
        {
            return null;
        }

        object? value = SafeGet(property, builder, out _);
        return value is UnreadableValue ? null : value;
    }

    private static object? SafeGet(BuilderProperty property, NpgsqlConnectionStringBuilder builder, out bool failed)
    {
        try
        {
            failed = false;
            return property.Getter(builder);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failed = true;
            return new UnreadableValue((exception is TargetInvocationException { InnerException: { } inner } ? inner : exception).GetType().Name);
        }
    }

    private static bool HasValue(string keyword, object? value) => keyword.ToLowerInvariant() switch
    {
        // Npgsql treats an empty user name and an empty application name as none (NpgsqlConnector.GetUsernameAsync, WriteStartupMessage).
        "username" or "application name" => value is UnreadableValue || HasText(value),
        _ => value is not null,
    };

    private static bool HasText(object? value) => value is string { Length: > 0 };

    private static string Format(object? value)
    {
        string text = value switch
        {
            null => "not set",
            string { Length: 0 } => "not set",
            bool flag => flag ? "true" : "false",
            Enum named => named.ToString(),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "not set",
        };

        return Bound(text);
    }

    private static string Bound(string text)
    {
        StringBuilder clean = new(Math.Min(text.Length, MaxValueLength + 1));

        foreach (char c in text)
        {
            if (clean.Length >= MaxValueLength)
            {
                clean.Append('…');
                break;
            }

            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        return clean.ToString();
    }

    private static bool IsSettingName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > 128)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits on white space; a backslash keeps the next character, as the server reads <c>options</c>.</summary>
    private static List<string> Tokenize(string options)
    {
        List<string> tokens = [];
        StringBuilder current = new();

        for (int i = 0; i < options.Length; i++)
        {
            char c = options[i];

            if (c == '\\' && i + 1 < options.Length)
            {
                current.Append(options[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static List<BuilderProperty> DiscoverProperties()
    {
        List<BuilderProperty> found = [];

        // NonPublic too: "Replication Mode" is an internal property in 9.0.4 and still a keyword of the connection string.
        foreach (PropertyInfo property in typeof(NpgsqlConnectionStringBuilder).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetCustomAttribute<DisplayNameAttribute>() is not { DisplayName: { Length: > 0 } keyword }
                || !property.CanRead
                || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            string category = property.GetCustomAttribute<CategoryAttribute>()?.Category is { Length: > 0 } declared ? declared : "Other";
            ObsoleteAttribute? obsolete = property.GetCustomAttribute<ObsoleteAttribute>();
            MethodInfo getter = property.GetMethod!;

            found.Add(new BuilderProperty(property.Name, keyword, category, obsolete is null ? null : obsolete.Message ?? "obsolete", instance => getter.Invoke(instance, null)));
        }

        return found;
    }

    private static string VersionOfLoadedNpgsql()
    {
        Assembly assembly = typeof(NpgsqlConnection).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        Version? version = assembly.GetName().Version;
        return version is null ? "unknown" : version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture) + "." + version.Build.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record BuilderProperty(string Name, string Keyword, string Category, string? Obsolete, Func<NpgsqlConnectionStringBuilder, object?> Getter);

    private sealed record UnreadableValue(string ExceptionType);
}
