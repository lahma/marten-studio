using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health;

/// <summary>
/// One visitor's answers to the three gates, asked lazily and at most once each per report - or per run of a
/// finding's action (<see cref="HealthActionService" />).
/// </summary>
/// <remarks>
/// <para>
/// <c>scope</c> is the swept database as a whole, by the store's registered key and the database's identity and with
/// no tenant - never the visitor's spelling of either (D29) - so the gates' tenant-less questions and a finding's
/// tenant question name the database the report's audit records.
/// </para>
/// <para>
/// This is the one implementation of the gates: the report and an action ask the same questions in the same order,
/// so the Activity order (the capability, the store policy, the write policy with the capability named) cannot drift
/// between two copies.
/// </para>
/// </remarks>
internal sealed class HealthVisitorGate(
    IStudioSettings settings,
    StudioAuthorization authorization,
    StudioCapabilityGuard capabilities,
    StudioScope scope)
{
    private readonly Dictionary<string, bool> tenants = new(StringComparer.Ordinal);
    private bool? database;
    private bool? activity;
    private string? activityRefusal;

    /// <summary>Whether this visitor passes <paramref name="gate" />.</summary>
    /// <param name="gate">The gate.</param>
    public async ValueTask<bool> PassesAsync(HealthGate gate) => gate switch
    {
        HealthGate.None => true,
        HealthGate.Database => await DatabaseAsync().ConfigureAwait(false),
        HealthGate.Activity => await ActivityAsync().ConfigureAwait(false),
        _ => false,
    };

    /// <summary>Whether the store policy shows this visitor <paramref name="tenantId" /> in the database.</summary>
    /// <param name="tenantId">The tenant.</param>
    public async ValueTask<bool> TenantAsync(string tenantId)
    {
        if (!tenants.TryGetValue(tenantId, out bool allowed))
        {
            allowed = await authorization.IsAuthorizedAsync(scope with { TenantId = tenantId }, capability: null).ConfigureAwait(false);
            tenants[tenantId] = allowed;
        }

        return allowed;
    }

    /// <summary>What it would take to pass <paramref name="gate" />, naming the setting or the policy.</summary>
    /// <param name="gate">The gate.</param>
    public string WhyNot(HealthGate gate) => gate switch
    {
        HealthGate.Database => settings.Text.DatabaseGateReason,
        HealthGate.Activity => activityRefusal ?? "needs " + capabilities.OptionFor(StudioCapability.ViewDatabaseActivity),
        _ => "withheld",
    };

    private async ValueTask<bool> DatabaseAsync()
    {
        database ??= await authorization.IsAuthorizedAsync(scope with { TenantId = null }, capability: null).ConfigureAwait(false);
        return database.Value;
    }

    /// <summary>
    /// <c>ViewDatabaseActivity</c>, in the database browser's order: the capability, then the store policy
    /// for the database as a whole, then the write policy with the capability named - all tenant-less.
    /// </summary>
    private async ValueTask<bool> ActivityAsync()
    {
        if (activity is { } known)
        {
            return known;
        }

        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            activityRefusal = capabilities.ReadOnly
                ? "needs ViewDatabaseActivity, and " + settings.ReadOnlyOption() + " is true, which turns every capability off"
                : "needs " + capabilities.OptionFor(StudioCapability.ViewDatabaseActivity);
            activity = false;
            return false;
        }

        if (!await DatabaseAsync().ConfigureAwait(false))
        {
            activityRefusal = WhyNot(HealthGate.Database);
            activity = false;
            return false;
        }

        activity = await authorization
            .IsAuthorizedAsync(scope with { TenantId = null }, StudioCapability.ViewDatabaseActivity)
            .ConfigureAwait(false);

        if (activity == false)
        {
            activityRefusal = HealthService.ActivityPolicyRefusal;
        }

        return activity.Value;
    }
}
