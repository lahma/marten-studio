using JasperFx.Events.Daemon;

namespace MartenStudio.Services.Events;

/// <summary>
/// Whether the host lets the studio show <c>DeadLetterEvent</c> - asked of
/// <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> like any document type, and failing closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule for every dead-letter read.</b> <c>DeadLetterEvent</c> is a Marten document in the event store's schema,
/// and a host that hides it hides the dead-letter screen's navigation entry and its Overview tile, the page's list,
/// Discard and Rewind (<see cref="EventDataService" />, which refuse with <see cref="HiddenMessage" />) and the advisor's
/// D06 together: "a hidden document type is absent" (D27) is not a rule with an exception for the one type Marten
/// registers on the host's behalf.
/// </para>
/// <para>
/// <b>Fail closed.</b> A delegate that throws has not said the type may be shown, so it is hidden, and the failure is
/// handed back for the caller to log - a host whose delegate throws for <c>typeof(DeadLetterEvent)</c> sees its dead
/// letters disappear, and the log is where it is told why.
/// </para>
/// </remarks>
internal static class DeadLetterVisibility
{
    /// <summary>
    /// What the dead-letter page, a refused Discard and a refused Rewind say while the type is hidden. Constant: it never
    /// names the type's alias or table, so a host that re-aliased the type it hides does not have the alias printed.
    /// </summary>
    public const string HiddenMessage = "Dead letters are hidden by MartenStudioOptions.IsDocumentTypeVisible.";

    /// <summary>Whether <paramref name="isDocumentTypeVisible" /> lets <c>DeadLetterEvent</c> be shown.</summary>
    /// <param name="isDocumentTypeVisible"><see cref="MartenStudioOptions.IsDocumentTypeVisible" />; <see langword="null" /> shows everything.</param>
    /// <param name="failure">What the delegate threw, or <see langword="null" />.</param>
    /// <returns><see langword="false" /> when the delegate hides the type or throws.</returns>
    public static bool IsVisible(Func<Type, bool>? isDocumentTypeVisible, out Exception? failure)
    {
        failure = null;

        if (isDocumentTypeVisible is null)
        {
            return true;
        }

        try
        {
            return isDocumentTypeVisible(typeof(DeadLetterEvent));
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }
}
