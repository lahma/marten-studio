namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// Marks a live test that produces the condition a health check looks for and asserts the check finds it.
/// </summary>
/// <remarks>
/// Every check this suite proves on a plain database needs at least one of these and one
/// <see cref="HealthQuietAttribute" />: <c>HealthLiveTestConventionTests</c> fails otherwise, and says where each other
/// check is proven. An advisor whose checks were only ever unit-tested against facts somebody typed is an advisor that
/// has never been shown to see the real thing.
/// </remarks>
/// <param name="code">The check's code.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HealthFiresAttribute(string code) : Attribute
{
    /// <summary>The check's code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Marks a live test on a healthy database - tables shaped and used the way the check recommends -
/// that asserts the check says nothing.
/// </summary>
/// <remarks>
/// The anti-cry-wolf half (plan §3.2): an advisor that fires on a healthy database is one people learn to ignore.
/// </remarks>
/// <param name="code">The check's code.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HealthQuietAttribute(string code) : Attribute
{
    /// <summary>The check's code.</summary>
    public string Code { get; } = code;
}
