using AngleSharp.Dom;

namespace MartenStudio.Tests;

/// <summary>What a test asks of a typed confirmation's Confirm button.</summary>
internal static class ConfirmButtonExtensions
{
    /// <summary>
    /// Whether Confirm will do nothing if pressed: <c>disabled</c> (an action that is a no-op) or <c>aria-disabled</c> (the phrase does not
    /// match yet). Confirm is aria-disabled and not disabled while a phrase is awaited, so Tab still lands on it (1.2-UX-FIX-FU).
    /// </summary>
    /// <param name="button">The button.</param>
    public static bool IsBlocked(this IElement button) =>
        button.HasAttribute("disabled") || string.Equals(button.GetAttribute("aria-disabled"), "true", StringComparison.Ordinal);
}
