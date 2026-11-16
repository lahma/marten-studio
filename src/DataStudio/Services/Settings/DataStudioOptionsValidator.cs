using Microsoft.Extensions.Options;

namespace DataStudio.Services.Settings;

/// <summary>
/// Validates <see cref="DataStudioOptions" />: the mount path, the application path and the refresh interval. One validator class, registered
/// with <c>TryAddEnumerable</c>, so <c>AddDataStudio()</c> called twice reports a failure once.
/// </summary>
internal sealed class DataStudioOptionsValidator : IValidateOptions<DataStudioOptions>
{
    private const string Type = nameof(DataStudioOptions);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DataStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Path) || !options.Path.StartsWith('/'))
        {
            failures.Add(Type + ".Path must start with '/'");
        }

        if (!StudioPath.IsRoutable(options.Path))
        {
            failures.Add(Type + ".Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')");
        }

        if (options.RefreshInterval < TimeSpan.FromSeconds(1) || options.RefreshInterval > TimeSpan.FromMinutes(5))
        {
            failures.Add(Type + ".RefreshInterval must be between one second and five minutes");
        }

        if (!IsApplicationPath(options.ApplicationPath))
        {
            failures.Add(
                Type + ".ApplicationPath must be null (no link), a host-relative path that starts with a single '/' ('/' or '/app'), or an absolute http or https URL (with no user information: 'https://user@host/' and 'https://user:pass@host/' are refused, because the part before the '@' reads as the destination and the browser goes to what follows it)");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Whether <paramref name="value" /> may be the "Back to application" target: <see langword="null" />, a host-relative path that
    /// starts with a single <c>/</c>, or an absolute <c>http</c> or <c>https</c> URL.
    /// </summary>
    /// <remarks>
    /// <c>//host</c> and <c>/\host</c> are refused: they start with <c>/</c> but a browser reads them as a network-path reference
    /// to another host, which is neither of the two shapes the option names. A value with whitespace or a control character in it
    /// is refused whole, so a stray space in a configuration file is a startup error rather than a link that does not work.
    /// An empty string is refused as well: the option's "no link" is <see langword="null" />. An absolute URL with user information
    /// (<c>https://app.example.com@evil.com/</c>, <c>https://user:pass@host/</c>) is refused: the part before the <c>@</c> reads as the
    /// destination to a person looking at the link, and the browser goes to what follows it.
    /// </remarks>
    internal static bool IsApplicationPath(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length == 0 || value.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (value[0] == '/')
        {
            return value.Length == 1 || (value[1] != '/' && value[1] != '\\');
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
            && uri.UserInfo.Length == 0;
    }
}
