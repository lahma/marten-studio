using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace MartenStudio.Tests.Support;

/// <summary>
/// A navigation manager for a circuit-shaped document: its base is the studio root (<c>http://localhost/marten/</c>), as the rendered
/// <c>&lt;base href&gt;</c> makes it on the interactive circuit, so a relative <c>href</c> from a page resolves to an address under the
/// mount. <see cref="Click" /> is the browser reporting an intercepted link; a studio <c>NavigateTo</c> lands at once and is reported as
/// not intercepted, the way a page's own push and the shell's replace are.
/// </summary>
internal sealed class ClickableNavigationManager : NavigationManager
{
    public ClickableNavigationManager(string startAt = "http://localhost/marten/") => Initialize("http://localhost/marten/", startAt);

    /// <summary>Every <c>NavigateTo</c> the components made, as the absolute address and its options.</summary>
    public List<(string Uri, NavigationOptions Options)> Requests { get; } = [];

    /// <summary>The visitor clicked a link with this <c>href</c>: the browser reports the new location as an intercepted navigation.</summary>
    public void Click(string href) => Move(href, intercepted: true);

    /// <summary>The browser moved on its own without a link (Back or Forward): reported as not intercepted.</summary>
    public void HistoryMove(string href) => Move(href, intercepted: false);

    private void Move(string href, bool intercepted)
    {
        Uri = ToAbsoluteUri(href).ToString();
        NotifyLocationChanged(intercepted);
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        string absolute = ToAbsoluteUri(uri).ToString();
        Requests.Add((absolute, options));

        if (options.ForceLoad)
        {
            return;
        }

        Uri = absolute;
        NotifyLocationChanged(isInterceptedLink: false);
    }
}

/// <summary>An <see cref="INavigationInterception" /> that does nothing, for a document that is not a browser.</summary>
internal sealed class QuietNavigationInterception : INavigationInterception
{
    public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
}

/// <summary>An <see cref="IScrollToLocationHash" /> that does nothing.</summary>
internal sealed class QuietScrollToLocationHash : IScrollToLocationHash
{
    public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
}
