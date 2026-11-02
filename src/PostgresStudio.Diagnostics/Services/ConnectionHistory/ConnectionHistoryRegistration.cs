using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #48: which of the two registrations the container holds - <c>AddPostgresConnectionHistory()</c> (this process samples and
/// reads) or <c>AddPostgresConnectionHistoryReader()</c> (it only reads a history another process fills). A marker singleton, found
/// by its descriptor so the second registration can be refused at registration time, and read by Holders to say whose history it is.
/// </summary>
/// <param name="ReaderOnly">Whether the registration is the reader-only one.</param>
internal sealed record ConnectionHistoryRegistration(bool ReaderOnly);

/// <summary>
/// Issue #48: what a reader-only history needs at start - the store another process appends to (an in-memory store nothing
/// appends to is always empty) and the studio or engine whose servers it reads. Registered by
/// <c>AddPostgresConnectionHistoryReader()</c> only, and run by <c>ValidateOnStart</c>, so the host may register the store before or
/// after the call.
/// </summary>
internal sealed class ConnectionHistoryReaderStartCheck(IServiceProvider services) : IValidateOptions<PostgresConnectionHistoryOptions>
{
    internal const string NoStore =
        "AddPostgresConnectionHistoryReader() reads a history another process fills: register the IPostgresConnectionHistoryStore "
        + "that process appends to (an in-memory history nothing appends to is always empty).";

    internal const string NoStudio =
        "AddPostgresConnectionHistoryReader() reads the history of the servers a studio or the diagnostics engine reads: "
        + "register AddPostgresStudio(), AddMartenStudio() or AddPostgresDiagnostics() as well.";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PostgresConnectionHistoryOptions options)
    {
        List<string> failures = [];

        if (!Registered(typeof(IPostgresConnectionHistoryStore)))
        {
            failures.Add(NoStore);
        }

        if (!Registered(typeof(IDiagnosticsSettings)))
        {
            failures.Add(NoStudio);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // This runs while PostgresConnectionHistoryOptions is being created. A host store (or settings) whose constructor reads
    // IOptions<PostgresConnectionHistoryOptions>.Value would re-enter that creation if it were built here, so ask whether the
    // service is registered and never construct it. A container without IServiceProviderIsService falls back to resolving it.
    private bool Registered(Type serviceType) =>
        services.GetService(typeof(IServiceProviderIsService)) is IServiceProviderIsService isService
            ? isService.IsService(serviceType)
            : services.GetService(serviceType) is not null;
}
