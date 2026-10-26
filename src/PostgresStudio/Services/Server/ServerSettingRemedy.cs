using System.Text.RegularExpressions;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Server;

/// <summary>
/// A server setting as a remedy (<see cref="HealthRemedyKind.ServerSetting" />): <c>setting = value</c> under the studio's
/// server-setting comment, and an explanation that ends with where the setting changes on the platform that runs the
/// server - and that a restart is needed, for a setting that waits for one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worded for the platform, in one place.</b> <see cref="WhereToSet" /> is the one switch on <c>(platform,
/// setting)</c>: today every setting changes where <see cref="PostgresPlatform.WhereSettingsChange" /> says, and AUR's
/// Aurora cases (issue #8 A6 - a setting Aurora fixes, or derives from the instance class) are arms added there, so no
/// check words a place of its own.
/// </para>
/// <para>
/// <b>The restart is read, never assumed.</b> <c>context</c> is <c>pg_settings.context</c> as the check read it - never
/// <c>current_setting</c>, which a session's placeholder can fake (B0 §5) - and <c>postmaster</c> adds
/// <see cref="RestartNote" />. A remedy whose context was not read says nothing about a restart.
/// </para>
/// <para>
/// <b>Text, never a command.</b> The code is the line as it goes in the server's configuration; the studio never runs
/// <c>ALTER SYSTEM</c> and never words a grant (<see cref="HealthRemedy" /> refuses both kinds of privilege change).
/// </para>
/// </remarks>
internal static partial class ServerSettingRemedy
{
    /// <summary>The <c>pg_settings.context</c> of a setting that takes effect only when the server starts.</summary>
    public const string PostmasterContext = "postmaster";

    /// <summary>What a postmaster setting's remedy adds at the end of its explanation.</summary>
    public const string RestartNote = "It takes effect only when the server restarts.";

    /// <summary>
    /// Where <paramref name="setting" /> changes on <paramref name="platform" />, as the end of "set it in ...". The one switch
    /// on <c>(platform, setting)</c> that AUR refines for Aurora's own cases (A6); every other pair is the platform's place.
    /// </summary>
    public static string WhereToSet(PostgresPlatform platform, string setting)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);

        return (platform.Kind, setting) switch
        {
            _ => platform.WhereSettingsChange,
        };
    }

    /// <summary>
    /// The plain form: <c># {comment}:</c> and <c>setting = value</c>, explained by <paramref name="explanation" /> (if any)
    /// followed by "Set it in {where}." and, for a postmaster setting, <see cref="RestartNote" />.
    /// </summary>
    /// <param name="platform">Who runs the server.</param>
    /// <param name="setting">The setting, as <c>pg_settings.name</c> spells it.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="explanation">What it changes and what it costs, before the place; or <see langword="null" />.</param>
    /// <param name="text">The studio's words, for the comment line.</param>
    /// <param name="context">The setting's <c>pg_settings.context</c>, or <see langword="null" /> when it was not read.</param>
    public static HealthRemedy For(PostgresPlatform platform, string setting, string value, string? explanation, StudioText text, string? context) =>
        Build(
            platform,
            setting,
            value,
            text,
            context,
            ":",
            where => (explanation is { Length: > 0 } ? explanation + " " : string.Empty) + "Set it in " + where + ".");

    /// <summary>
    /// The general form, for a remedy whose comment says more than the label or whose explanation names the place
    /// mid-sentence: <c># {comment}{commentSuffix}</c>, then <c>setting = value</c>, explained by what
    /// <paramref name="explanation" /> makes of <see cref="WhereToSet" />'s answer, and <see cref="RestartNote" /> for a
    /// postmaster setting.
    /// </summary>
    /// <param name="platform">Who runs the server.</param>
    /// <param name="setting">The setting, as <c>pg_settings.name</c> spells it.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="text">The studio's words, for the comment line.</param>
    /// <param name="context">The setting's <c>pg_settings.context</c>, or <see langword="null" /> when it was not read.</param>
    /// <param name="commentSuffix">What follows the comment's label on its line: <c>":"</c>, or a sentence of the check's.</param>
    /// <param name="explanation">The explanation, given where the setting changes on this platform.</param>
    /// <exception cref="ArgumentException">The setting is not a setting's name, or the value spans lines.</exception>
    public static HealthRemedy Build(
        PostgresPlatform platform,
        string setting,
        string value,
        StudioText text,
        string? context,
        string commentSuffix,
        Func<string, string> explanation)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(commentSuffix);
        ArgumentNullException.ThrowIfNull(explanation);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!SettingName().IsMatch(setting))
        {
            throw new ArgumentException("A server-setting remedy names a setting as pg_settings spells it.", nameof(setting));
        }

        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("A server setting's value is one line.", nameof(value));
        }

        string explained = explanation(WhereToSet(platform, setting));

        if (string.Equals(context, PostmasterContext, StringComparison.Ordinal))
        {
            explained = explained.Length == 0 ? RestartNote : explained + " " + RestartNote;
        }

        return HealthRemedy.ForServerSetting(
            "# " + text.ServerSettingComment + commentSuffix + "\n" + setting + " = " + value,
            explained.Length == 0 ? null : explained);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingName();
}
