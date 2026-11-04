using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace PostgresStudio.Tests.Support;

/// <summary>
/// A navigation manager that records what it was asked for and lets a test say what the browser did: a studio <c>NavigateTo</c>
/// lands at once (the way bUnit's own does), a <c>forceLoad</c> is a document load and leaves the page, and
/// <see cref="Simulate" /> is a link click, Back or Forward, which the browser reports as an intercepted navigation.
/// </summary>
internal sealed class RecordingNavigationManager : NavigationManager
{
    public RecordingNavigationManager(string startAt = "http://localhost/postgres/") => Initialize("http://localhost/", startAt);

    /// <summary>Every <c>NavigateTo</c> the component under test made, in order, as the absolute address and its options.</summary>
    public List<(string Uri, NavigationOptions Options)> Requests { get; } = [];

    /// <summary>The requests that were a document load (<c>forceLoad</c>), which is what leaves the page.</summary>
    public IEnumerable<(string Uri, NavigationOptions Options)> ForceLoads => Requests.Where(static x => x.Options.ForceLoad);

    /// <summary>Every location the manager reported, with whether the browser said it came from an intercepted link, Back or Forward.</summary>
    public List<(string Uri, bool Intercepted)> Reported { get; } = [];

    /// <summary>The browser moved to <paramref name="uri" /> on its own - a link, Back or Forward - and reported it.</summary>
    public void Simulate(string uri, bool intercepted)
    {
        Uri = ToAbsoluteUri(uri).ToString();
        Reported.Add((Uri, intercepted));
        NotifyLocationChanged(intercepted);
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        string absolute = ToAbsoluteUri(uri).ToString();
        Requests.Add((absolute, options));

        if (options.ForceLoad)
        {
            // A document load: the page is replaced by the browser, nothing is reported to this circuit.
            return;
        }

        Uri = absolute;
        Reported.Add((absolute, false));
        NotifyLocationChanged(isInterceptedLink: false);
    }
}

/// <summary>An <see cref="INavigationInterception" /> that counts the asks, and can fail the way a lost circuit or a second renderer does.</summary>
internal sealed class RecordingNavigationInterception : INavigationInterception
{
    private int calls;

    /// <summary>How many times interception was asked for.</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <summary>What the ask throws, or <see langword="null" /> for success.</summary>
    public Exception? Failure { get; set; }

    public Task EnableNavigationInterceptionAsync()
    {
        Interlocked.Increment(ref calls);
        return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}

/// <summary>An <see cref="IScrollToLocationHash" /> that records the addresses it was asked to scroll to.</summary>
internal sealed class RecordingScrollToLocationHash : IScrollToLocationHash
{
    private readonly List<string> refreshed = [];

    /// <summary>The addresses a scroll to their fragment was asked for, in order.</summary>
    public IReadOnlyList<string> Refreshed
    {
        get
        {
            lock (refreshed)
            {
                return [.. refreshed];
            }
        }
    }

    /// <summary>What the scroll throws, or <see langword="null" /> for success.</summary>
    public Exception? Failure { get; set; }

    public Task RefreshScrollPositionForHash(string locationAbsolute)
    {
        lock (refreshed)
        {
            refreshed.Add(locationAbsolute);
        }

        return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
