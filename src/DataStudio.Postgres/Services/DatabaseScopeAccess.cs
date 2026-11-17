namespace DataStudio.Postgres.Services;

/// <summary>What the database's frame knows about the visitor's right to the scope the studio is about.</summary>
internal enum ScopeAccess
{
    /// <summary>The policy has not answered yet, and until it does the page is not rendered.</summary>
    Unknown,

    /// <summary>The policy admitted the visitor to the active scope, or there is no scope to be about.</summary>
    Granted,

    /// <summary>The policy refused the active scope.</summary>
    Denied,
}

/// <summary>
/// The answer of the database's scope gate for one circuit (D34): written by <see cref="Components.Layout.DatabaseScopeHost" />, which asks
/// the policy, and read by <see cref="Components.Layout.DatabaseScopeGate" />, which draws the page, the refusal or the spinner.
/// </summary>
/// <remarks>
/// Scoped, so it is one circuit's. <see cref="Changed" /> is synchronous: a handler asks for a render and returns.
/// </remarks>
internal sealed class DatabaseScopeAccess
{
    private int value = (int)ScopeAccess.Unknown;

    /// <summary>What the host last answered; <see cref="ScopeAccess.Unknown" /> until it has.</summary>
    public ScopeAccess Value => (ScopeAccess)Volatile.Read(ref value);

    /// <summary>Raised when <see cref="Value" /> moved, never when it was set to what it already was.</summary>
    public event Action? Changed;

    /// <summary>Sets <see cref="Value" />, and raises <see cref="Changed" /> when that moved it.</summary>
    /// <param name="access">The answer.</param>
    public void Set(ScopeAccess access)
    {
        if (Interlocked.Exchange(ref value, (int)access) != (int)access)
        {
            Changed?.Invoke();
        }
    }
}
