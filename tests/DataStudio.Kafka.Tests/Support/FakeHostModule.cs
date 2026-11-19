using DataStudio.Services;
using DataStudio.Services.Settings;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataStudio.Kafka.Tests.Support;

/// <summary>
/// What a module that guards its pages with a scope gate (the database module is one) is to the shell, defined by this suite: a root of its own, one page,
/// a header search, and a gate that asks <see cref="RecordingAuthorizationService" /> about <see cref="FakeHostResource" /> before the page's body starts.
/// The Kafka module is the real one beside it. Used wherever a test is about the shell's behaviour between two modules - which module's gate a page
/// meets, which module's header it draws, the tiles of the front page - and not about any particular database module, which this suite does not have.
/// </summary>
internal sealed class FakeHostModule : IStudioModule
{
    /// <summary>The module's key.</summary>
    public const string ModuleKey = "host";

    /// <summary>The compile-time root of its one page.</summary>
    public const string PageRoot = "/host";

    /// <summary>The page's address below the mount.</summary>
    public const string PageAddress = "host/page";

    private static readonly IReadOnlyList<StudioPageAssembly> Pages = [new(typeof(FakeHostModule).Assembly, PageRoot)];

    /// <summary>The badge key of the host page's entry: a rail's badges draw one on it.</summary>
    public const string BadgeKey = "host-badge";

    private static readonly IReadOnlyList<StudioNavSection> Rail = [new("Host", [new StudioNavItem("Host page", PageAddress, "cog", BadgeKey)])];

    /// <inheritdoc />
    public string Key => ModuleKey;

    /// <inheritdoc />
    public string ProductName => "Host";

    /// <inheritdoc />
    public IReadOnlyList<StudioPageAssembly> PageAssemblies => Pages;

    /// <inheritdoc />
    public IReadOnlyList<StudioNavSection> Navigation => Rail;

    /// <inheritdoc />
    public Type? ScopeSelector => null;

    /// <inheritdoc />
    public Type? HeaderSearch => typeof(FakeHostSearch);

    /// <inheritdoc />
    public Type? CircuitHost => null;

    /// <inheritdoc />
    public Type? ScopeGate => typeof(FakeHostGate);

    /// <inheritdoc />
    public string HomeRoute => PageAddress;

    /// <inheritdoc />
    public string Summary => "A module the tests define.";

    /// <inheritdoc />
    public string ExposedSentence => "The host page shows nothing.";

    /// <inheritdoc />
    public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

    /// <inheritdoc />
    public string? AllCapabilitiesOption => null;

    /// <inheritdoc />
    public IReadOnlyList<StudioShortcutGroup> Shortcuts { get; } = [];

    /// <inheritdoc />
    public string AuditEventIds => string.Empty;
}

/// <summary>What <see cref="FakeHostGate" /> asks the authorization service about: the host module's one scope.</summary>
public sealed record FakeHostResource;

/// <summary>Counts how often the host module's page body started, so a test can say whether a refused scope ever ran one.</summary>
public sealed class FakeHostProbe
{
    private int bodies;

    /// <summary>How many times the page's body was created.</summary>
    public int Bodies => Volatile.Read(ref bodies);

    /// <summary>Counts one.</summary>
    public void Started() => Interlocked.Increment(ref bodies);
}

/// <summary>The one page of <see cref="FakeHostModule" />.</summary>
[Route("/host/page")]
internal sealed class FakeHostPage : ComponentBase
{
    [Inject]
    private FakeHostProbe Probe { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() => Probe.Started();

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "h1");
        builder.AddContent(1, "Host page");
        builder.CloseElement();
    }
}

/// <summary>The header search of <see cref="FakeHostModule" />: a box on its pages, as the database's Find is.</summary>
internal sealed class FakeHostSearch : ComponentBase
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "ms-header-find");
        builder.AddContent(2, "Find");
        builder.CloseElement();
    }
}

/// <summary>
/// The host module's scope gate: pending while the policy is asked, the notice when it says no, the page's body when it says yes. Asked once per
/// time it is created, which is every time the layout draws a page of the module after one of another's.
/// </summary>
internal sealed class FakeHostGate : ComponentBase
{
    private enum State
    {
        Asking,
        Allowed,
        Refused,
    }

    private State state = State.Asking;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Inject]
    private IAuthorizationService Authorization { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider Authentication { get; set; } = default!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        // Asked once, on purpose: this fake stands for a host gate that is created again each time the layout draws a page of its module, and
        // the tests count how often its policy is asked. The .NET 11 SDK's BL0013 wants a component that reads the state to follow its
        // changes too, which would change that count.
#pragma warning disable BL0013
        AuthenticationState visitor = await Authentication.GetAuthenticationStateAsync();
#pragma warning restore BL0013
        AuthorizationResult result = await Authorization.AuthorizeAsync(visitor.User, new FakeHostResource(), "host-source");
        state = result.Succeeded ? State.Allowed : State.Refused;
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        switch (state)
        {
            case State.Asking:
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "ms-loading-spinner");
                builder.CloseElement();
                break;
            case State.Refused:
                builder.OpenElement(2, "p");
                builder.AddAttribute(3, "class", "ms-empty-title");
                builder.AddContent(4, "Not authorized");
                builder.CloseElement();
                break;
            default:
                builder.AddContent(5, ChildContent);
                break;
        }
    }
}

/// <summary>Adds <see cref="FakeHostModule" /> to a shell, as a module's own <c>Add…()</c> does.</summary>
internal static class FakeHostRegistration
{
    /// <summary>Adds the module and its probe.</summary>
    /// <param name="builder">The builder <c>AddDataStudio()</c> returned.</param>
    public static DataStudioBuilder AddFakeHost(this DataStudioBuilder builder)
    {
        builder.Services.TryAddSingleton<FakeHostProbe>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, FakeHostModule>());
        return builder;
    }
}
