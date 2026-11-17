using DataStudio.Postgres.Services;

namespace DataStudio.Postgres.Tests.Shell;

/// <summary>Shell A2a: what the database's scope gate answered for a circuit, and the one event that says it moved.</summary>
public sealed class DatabaseScopeAccessTests
{
    [Fact]
    public void A_circuit_starts_with_no_answer()
    {
        new DatabaseScopeAccess().Value.Should().Be(ScopeAccess.Unknown);
    }

    [Fact]
    public void Changed_is_raised_synchronously_when_the_answer_moves_and_never_when_it_does_not()
    {
        DatabaseScopeAccess access = new();
        List<ScopeAccess> seen = [];
        access.Changed += () => seen.Add(access.Value);

        access.Set(ScopeAccess.Unknown);
        access.Set(ScopeAccess.Granted);
        access.Set(ScopeAccess.Granted);
        access.Set(ScopeAccess.Denied);

        seen.Should().Equal(ScopeAccess.Granted, ScopeAccess.Denied);
    }
}
