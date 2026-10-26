using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S08 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Posture">The source's connection: its login and when the login's password expires.</param>
internal sealed record PasswordExpiryFacts(DateTimeOffset ReadAt, ServerConnectionPosture Posture);

/// <summary>
/// S08: the password of the role the source's data source logs in as expires soon (<c>pg_roles.rolvaliduntil</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Warning within 30 days, Critical within 7</b> - and Critical once it has passed: PostgreSQL 19's
/// <c>password_expiration_warning_threshold</c> warns a login seven days ahead by default, and a pool that can open no new
/// connection takes the application down as its connections are recycled. No variant.
/// </para>
/// <para>
/// <b>Only a password login.</b> <c>VALID UNTIL</c> limits the password alone: a role that logs in through IAM, Entra ID or a
/// certificate is not refused when it passes, which the caveat says - the studio cannot tell how the data source
/// authenticates. The remedy is words: <c>ALTER ROLE … VALID UNTIL …</c> is a DBA's change.
/// </para>
/// </remarks>
[HealthCheck("S08", HealthArea.Server, HealthCostTier.Catalog, "Connecting role's password expires")]
internal sealed class PasswordExpiryCheck : HealthCheck<PasswordExpiryFacts>
{
    /// <summary>A password expiring within this long is a Warning…</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromDays(30);

    /// <summary>…and within this long, or already expired, Critical.</summary>
    public static readonly TimeSpan Critical = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public override async Task<PasswordExpiryFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerConnectionPosture posture = await ServerConnectionPosture.SharedAsync(context).ConfigureAwait(false);
        return new PasswordExpiryFacts(context.Now, posture);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PasswordExpiryFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ServerConnectionPosture posture = facts.Posture;

        if (posture.PasswordValidUntil is not { } until)
        {
            return HealthVerdict.Quiet("The login's password has no expiry (rolvaliduntil is not set).");
        }

        TimeSpan left = until - evaluation.Now;
        string when = until.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

        if (left > Warning)
        {
            return HealthVerdict.Quiet("The login's password expires at " + when + ", more than " + SessionText.Duration(Warning) + " away.");
        }

        bool expired = left <= TimeSpan.Zero;

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S08",
            Severity = left <= Critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = expired
                ? "The password of " + evaluation.Text.SourcePossessive + " login has expired"
                : "The password of " + evaluation.Text.SourcePossessive + " login expires in " + SessionText.Duration(left),
            WhyItMatters =
                "Once a role's password has expired, Postgres refuses every new password login as that role. The connections open "
                + "now go on working, but the pool can open no new one: as connections are recycled, or the application restarts, it "
                + "stops reaching the database at all. Whoever administers the role gives it a later expiry or a new password before "
                + "then (ALTER ROLE … VALID UNTIL …) - a DBA's change, which the studio never makes.",
            Evidence =
            [
                new("Logs in as", posture.LoginRole, HealthGate.Database),
                new("Password valid until", when),
                new("Time left", expired ? "none - it expired " + SessionText.Duration(-left) + " ago" : SessionText.Duration(left)),
            ],
            Caveat =
                "This applies only if the role logs in with a password; an IAM, Entra ID or certificate login ignores it. Read from "
                + "pg_roles.rolvaliduntil for session_user, the role the data source logs in as.",
        });
    }
}
