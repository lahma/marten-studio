using Microsoft.AspNetCore.WebUtilities;

using PostgresStudio.Components;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Query;

/// <summary>
/// Query › Plan's address (X3, issue #28, D9): the statement, the mode and the options travel in the query string, so a pasted
/// link reopens the page with the editor filled - and never runs it: a run needs a click. The plan itself is never in the address,
/// and nor are the values of a statement's placeholders, which are data.
/// </summary>
internal static class PlanLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "plan";

    /// <summary>The query-string name of the statement.</summary>
    public const string SqlParameter = "sql";

    /// <summary>The query-string name of the mode.</summary>
    public const string ModeParameter = "mode";

    /// <summary>The query-string name of the options: a comma-separated list of the ones that are on.</summary>
    public const string OptionsParameter = "options";

    /// <summary>The longest statement put into an address; a longer one is left out of it, as the console's is.</summary>
    public const int MaxUrlStatementLength = 4 * 1024;

    /// <summary>The page, with the scope carried and - when there is one - the statement, the mode and the options.</summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio's descriptor: its source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="statement">The statement for the editor, or <see langword="null" /> for none; one past <see cref="MaxUrlStatementLength" /> is left out.</param>
    /// <param name="mode">The mode; <see cref="PlanMode.Plan" /> is the default and is not written.</param>
    /// <param name="options">The options; the defaults are not written.</param>
    public static string ToPlan(
        IStudioSettings settings,
        StudioDescriptor descriptor,
        StudioScope? scope,
        string? statement = null,
        PlanMode mode = PlanMode.Plan,
        PlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);

        string url = StudioLink.To(settings, Route);

        if (!string.IsNullOrWhiteSpace(statement) && statement.Length <= MaxUrlStatementLength)
        {
            url = QueryHelpers.AddQueryString(url, SqlParameter, statement);
        }

        if (mode != PlanMode.Plan)
        {
            url = QueryHelpers.AddQueryString(url, ModeParameter, ModeKey(mode));
        }

        if (options is not null && options != new PlanOptions())
        {
            url = QueryHelpers.AddQueryString(url, OptionsParameter, OptionsKey(options));
        }

        return StudioScopeQuery.Append(url, scope, descriptor.SourceQueryParameter);
    }

    /// <summary>A mode's name in the address.</summary>
    public static string ModeKey(PlanMode mode) => mode switch
    {
        PlanMode.Analyze => "analyze",
        PlanMode.Generic => "generic",
        _ => "plan",
    };

    /// <summary>The mode an address names; <see cref="PlanMode.Plan" /> for anything else, as a typed address may say anything.</summary>
    public static PlanMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "analyze" => PlanMode.Analyze,
        "generic" => PlanMode.Generic,
        _ => PlanMode.Plan,
    };

    /// <summary>The options that are on, as the address writes them: <c>buffers,timing,verbose</c>, in the order the page lists them.</summary>
    public static string OptionsKey(PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> on = [];

        Add(options.Buffers, "buffers");
        Add(options.Timing, "timing");
        Add(options.Verbose, "verbose");
        Add(options.Settings, "settings");
        Add(options.Wal, "wal");
        Add(options.Memory, "memory");
        Add(options.Serialize, "serialize");

        return string.Join(',', on);

        void Add(bool enabled, string name)
        {
            if (enabled)
            {
                on.Add(name);
            }
        }
    }

    /// <summary>
    /// The options an address names. An address with no <c>options</c> at all means the defaults (<c>BUFFERS</c> and <c>TIMING</c>
    /// on); one with the parameter present means exactly the options it lists, so <c>options=</c> is none.
    /// </summary>
    public static PlanOptions ParseOptions(string? value)
    {
        if (value is null)
        {
            return new PlanOptions();
        }

        HashSet<string> on = new(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static x => x.ToLowerInvariant()),
            StringComparer.Ordinal);

        return new PlanOptions
        {
            Buffers = on.Contains("buffers"),
            Timing = on.Contains("timing"),
            Verbose = on.Contains("verbose"),
            Settings = on.Contains("settings"),
            Wal = on.Contains("wal"),
            Memory = on.Contains("memory"),
            Serialize = on.Contains("serialize"),
        };
    }
}
