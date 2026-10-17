namespace MartenStudio.Services.Events;

/// <summary>What the Event types screen shows of the health advisor.</summary>
/// <remarks>
/// <para>
/// One finding: <c>E05</c>, unregistered event types, which H6's check raises from the same read this
/// screen makes. The screen reads it through <c>IHealthService.CachedFindingsAsync</c>, the cache-only read
/// for inline banners: it returns the visitor-visible findings of the latest regular sweep, through the same
/// gates as the Health page, and never starts a sweep. So with no recent sweep - nobody has had the Health
/// page or the Overview open - the screen draws nothing, and in particular never a "healthy" claim: no
/// finding is "not checked", not "fine".
/// </para>
/// <para>
/// The screen's own "not registered" marking is independent of it and stays: it is this page's read, it
/// is there with or without the advisor, and it marks a type the visitor is looking at rather than a
/// finding somebody may have muted.
/// </para>
/// </remarks>
internal static class EventTypesHealth
{
    /// <summary>The unregistered-event-types finding.</summary>
    public const string UnregisteredTypes = "E05";

    /// <summary>The codes the screen asks the advisor's cache for.</summary>
    public static IReadOnlyCollection<string> BannerCodes { get; } = [UnregisteredTypes];
}
