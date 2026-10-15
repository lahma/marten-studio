namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// Marks a live test that produces the condition a health check looks for and asserts the check finds it.
/// </summary>
/// <remarks>
/// Every check needs at least one of these and one <see cref="HealthQuietAttribute" />:
/// <c>HealthLiveTestConventionTests</c> fails otherwise. An advisor whose checks were only ever unit-tested
/// against facts somebody typed is an advisor that has never been shown to see the real thing.
/// </remarks>
/// <param name="code">The check's code.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HealthFiresAttribute(string code) : Attribute
{
    /// <summary>The check's code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Marks a live test on a healthy store - the sample domain's, configured the way the check recommends -
/// that asserts the check says nothing.
/// </summary>
/// <remarks>
/// The anti-cry-wolf half (plan §3.2): an advisor that fires on a healthy store is one people learn to ignore.
/// </remarks>
/// <param name="code">The check's code.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HealthQuietAttribute(string code) : Attribute
{
    /// <summary>The check's code.</summary>
    public string Code { get; } = code;
}
