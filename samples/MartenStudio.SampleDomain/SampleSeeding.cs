namespace MartenStudio.SampleDomain;

/// <summary>
/// What the sample host decided, before the store was touched, that <see cref="SampleDataSeeder" /> may do beyond the
/// store's own documents and events. Registered as a singleton by the host; a seeder built without one does none of it.
/// </summary>
/// <param name="GrantConsoleRole">
/// Whether the seeding grants <see cref="SampleConsoleRole" /> the demo's schemas: only when the database is the demo's -
/// the throwaway container, or one <c>--allow-data-generation</c> names - and the role was made usable for this start
/// (<see cref="SampleConsoleRole.EnsureAsync(string, TextWriter?, CancellationToken)" />). Role membership is the server's,
/// not the database's, so a login that an earlier start made a member is still one on any other database of that server, and
/// <see cref="SampleConsoleRole.GrantAsync" />'s own membership check cannot tell the two apart.
/// </param>
public sealed record SampleSeeding(bool GrantConsoleRole);
