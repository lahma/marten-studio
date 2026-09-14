using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

namespace MartenStudio.Tests.Support;

/// <summary>
/// An <see cref="IAuthorizationService" /> that answers for <see cref="MartenStoreResource" /> from a
/// rule a test sets, and records every question it was asked.
/// </summary>
/// <remarks>
/// What the tests are about is which resource the studio passes and when it asks - the policy engine
/// itself is not ours to test. <see cref="Calls" /> is what lets a test assert that the capability name
/// travelled on a write and that a read asked with <see langword="null" />.
/// </remarks>
public sealed class TestStoreAuthorizationService : IAuthorizationService
{
    private Func<MartenStoreResource, bool> rule = static _ => true;

    /// <summary>Every resource the studio asked about, in order.</summary>
    public List<(string Policy, MartenStoreResource Resource)> Calls { get; } = [];

    /// <summary>
    /// A gate a test can hold shut to keep an answer pending, which is what the layout's "unknown" frame
    /// looks like from the inside.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Allows exactly these store keys and refuses everything else.</summary>
    public void AllowStores(params string[] storeKeys) =>
        rule = resource => storeKeys.Contains(resource.StoreName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Allows or refuses by an arbitrary rule over the resource.</summary>
    public void Allow(Func<MartenStoreResource, bool> predicate) => rule = predicate;

    /// <summary>Refuses everything.</summary>
    public void DenyEverything() => rule = static _ => false;

    public async Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements)
    {
        if (Gate is not null)
        {
            await Gate.Task;
        }

        return resource is MartenStoreResource store && rule(store)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed();
    }

    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        if (resource is MartenStoreResource store)
        {
            Calls.Add((policyName, store));
        }

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return resource is MartenStoreResource allowed && rule(allowed)
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed();
    }
}
