using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

namespace KStudio.Internal;

/// <summary>One <c>ExternalLink(label, template)</c> call of the host.</summary>
/// <param name="Label">The link's text.</param>
/// <param name="Template">The URL template, with <c>{name}</c> placeholders.</param>
internal sealed record KafkaExternalLinkRegistration(string Label, string Template);

/// <summary>What a template is filled from: only values the visitor already sees on the record (D44).</summary>
/// <param name="TraceId">The record's trace id, set only when the header it came from is a literal, or <see langword="null" />.</param>
/// <param name="CorrelationId">The first literal <c>correlation-id</c> header, or <see langword="null" />.</param>
/// <param name="Timestamp">The record's timestamp.</param>
internal sealed record KafkaExternalLinkValues(string? TraceId, string? CorrelationId, DateTimeOffset Timestamp);

/// <summary>A link to the host's own tracing or log tool, filled for one record.</summary>
/// <param name="Label">The link's text.</param>
/// <param name="Href">The absolute address.</param>
internal sealed record KafkaExternalLink(string Label, string Href);

/// <summary>
/// The host's external link templates (D44, decision 9 of D-CORR): parsed, validated at start, and filled for a record. A template is rendered
/// into every visitor's page, so it holds no credential, and a value that is masked, cut or absent means no link, never a link with a hole.
/// </summary>
internal static partial class ExternalLinkTemplate
{
    /// <summary>The most links.</summary>
    public const int MaxLinks = 8;

    /// <summary>The longest label.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>The longest template.</summary>
    public const int MaxTemplateLength = 2_000;

    /// <summary>How far either side of the record's timestamp <c>{from}</c> and <c>{to}</c> reach.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(15);

    /// <summary>The placeholders.</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["traceId", "correlationId", "from", "to", "fromMs", "toMs"];

    /// <summary>Query parameters (or fragment parameters) that name a credential, compared ignoring case.</summary>
    private static readonly string[] CredentialNames =
        ["access_token", "token", "api_key", "apikey", "x-api-key", "password", "passwd", "pwd", "secret", "client_secret", "signature", "sig", "auth"];

    /// <summary>The problems with the host's links; the text names a label and a rule, and never a template.</summary>
    /// <param name="links">The registrations.</param>
    public static IReadOnlyList<string> Validate(IReadOnlyList<KafkaExternalLinkRegistration> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        List<string> problems = [];

        if (links.Count > MaxLinks)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"KafkaStudioOptions.ExternalLink was called {links.Count} times; at most {MaxLinks} links are allowed."));
        }

        HashSet<string> labels = new(StringComparer.OrdinalIgnoreCase);

        foreach (KafkaExternalLinkRegistration link in links)
        {
            string name = Name(link.Label);

            if (link.Label.Length is 0 or > MaxLabelLength)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"External link {name}: the label must be 1 to {MaxLabelLength} characters."));
            }
            else if (!labels.Add(link.Label))
            {
                problems.Add($"External link {name}: the label is used twice (labels are unique, ignoring case).");
            }

            string? problem = Check(link.Template);

            if (problem is not null)
            {
                problems.Add($"External link {name}: {problem}");
            }
        }

        return problems;
    }

    /// <summary>
    /// The link for a record, or <see langword="null" /> when the template names a placeholder the record has no value for. Every value is
    /// <see cref="Uri.EscapeDataString(string)" />-escaped, wherever the placeholder stands.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="values">The record's values.</param>
    public static KafkaExternalLink? Fill(KafkaExternalLinkRegistration registration, KafkaExternalLinkValues values)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(values);

        bool missing = false;

        string href = PlaceholderPattern().Replace(registration.Template, match =>
        {
            string? value = ValueOf(match.Groups[1].Value, values);

            if (value is null)
            {
                missing = true;
                return string.Empty;
            }

            return Uri.EscapeDataString(value);
        });

        return missing ? null : new KafkaExternalLink(registration.Label, href);
    }

    /// <summary>The placeholders a template names, in order of appearance.</summary>
    /// <param name="template">The template.</param>
    public static IReadOnlyList<string> Named(string template) =>
        [.. PlaceholderPattern().Matches(template).Select(static x => x.Groups[1].Value)];

    private static string? ValueOf(string placeholder, KafkaExternalLinkValues values)
    {
        DateTimeOffset utc = values.Timestamp.ToUniversalTime();
        DateTimeOffset from = utc - Margin;
        DateTimeOffset to = utc + Margin;

        return placeholder switch
        {
            "traceId" => NonEmpty(values.TraceId),
            "correlationId" => NonEmpty(values.CorrelationId),
            "from" => Iso(from),
            "to" => Iso(to),
            "fromMs" => from.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "toMs" => to.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A label bounded for an error message, in quotes.</summary>
    private static string Name(string label)
    {
        StringBuilder text = new("'");
        text.Append(label.Length > MaxLabelLength ? label[..MaxLabelLength] + "…" : label).Append('\'');
        return text.ToString();
    }

    /// <summary>The first rule the template breaks, or <see langword="null" />. The text is a rule and never the template's own words.</summary>
    private static string? Check(string template)
    {
        if (template.Length > MaxTemplateLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the template is longer than {MaxTemplateLength:N0} characters.");
        }

        // Every well-formed placeholder is replaced by 'x'; what is left must hold no brace at all, or a name was misspelt or a brace unbalanced.
        string probe = PlaceholderPattern().Replace(template, "x");

        foreach (string named in Named(template))
        {
            if (!Placeholders.Contains(named, StringComparer.Ordinal))
            {
                return "the template names a placeholder KStudio does not know (the placeholders are {traceId}, {correlationId}, {from}, {to}, {fromMs} and {toMs}).";
            }
        }

        if (probe.Contains('{', StringComparison.Ordinal) || probe.Contains('}', StringComparison.Ordinal))
        {
            return "the template has an unbalanced or empty brace.";
        }

        if (!Uri.TryCreate(probe, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            return "the template is not an absolute http or https address.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "the template holds user info (user:password@), which is a credential every visitor would see.";
        }

        foreach (string name in ParameterNames(uri))
        {
            if (CredentialNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return "the template has a parameter that names a credential (a token, key, password, secret or signature), which every visitor would see.";
            }
        }

        return null;
    }

    /// <summary>The names of the query's parameters, and of a fragment that is itself a list of parameters.</summary>
    private static IEnumerable<string> ParameterNames(Uri uri)
    {
        foreach (string part in new[] { uri.Query, uri.Fragment })
        {
            string text = part.TrimStart('?', '#');

            foreach (string pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                string name = equals < 0 ? pair : pair[..equals];

                yield return Uri.UnescapeDataString(name);
            }
        }
    }

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

/// <summary>Refuses a host whose <c>ExternalLink</c> calls break a rule of D44, at the first read of the options (the startup guard reads them).</summary>
internal sealed class KafkaExternalLinkOptionsValidator : IValidateOptions<KafkaStudioOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, KafkaStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<string> problems = ExternalLinkTemplate.Validate(options.ExternalLinks);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
