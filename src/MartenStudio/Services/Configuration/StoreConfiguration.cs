namespace MartenStudio.Services.Configuration;

/// <summary>
/// One configured value, with the member it came from.
/// </summary>
/// <remarks>
/// The member name is the whole point of this screen. "Stream identity: Guid" is a fact; "Stream
/// identity: Guid, from <c>StoreOptions.Events.StreamIdentity</c>" is a fact somebody can act on without
/// going to look for where it is set. Every row on the Configuration page carries one.
/// </remarks>
/// <param name="Name">What the value is, in the studio's words.</param>
/// <param name="Value">What it is set to, already formatted.</param>
/// <param name="Member">The <c>StoreOptions</c> member it was read from.</param>
internal sealed record ConfigurationValue(string Name, string Value, string Member);

/// <summary>
/// One database of the store, described from <c>ITenancy.DescribeDatabasesAsync()</c>.
/// </summary>
/// <remarks>
/// Five fields, chosen rather than mapped: <c>DatabaseDescriptor</c> also carries a <c>Properties</c>
/// list, a <c>SubjectUri</c> and a <c>DatabaseUri()</c>, any of which a provider is free to build out of
/// the connection string. Marten Studio never renders a connection string (plan §3.2), so the safe
/// fields are named here one at a time and the rest are not carried at all - a DTO that held them would
/// be one refactor away from printing them.
/// </remarks>
/// <param name="Identity">Marten's <c>DatabaseId.Identity</c>, which is the key every studio URL uses.</param>
/// <param name="Engine">The database engine, as JasperFx reports it.</param>
/// <param name="Server">The server host name.</param>
/// <param name="Port">The port, when the descriptor knows one.</param>
/// <param name="DatabaseName">The database name.</param>
/// <param name="Schema">The schema or namespace the store owns in it.</param>
/// <param name="TenantIds">The tenants that live in it, when the store is multi-tenanted by database.</param>
internal sealed record ConfiguredDatabase(
    string Identity,
    string Engine,
    string Server,
    int? Port,
    string DatabaseName,
    string Schema,
    IReadOnlyList<string> TenantIds);

/// <summary>The store card.</summary>
/// <param name="StoreKey">The registration key.</param>
/// <param name="DisplayName">What the selector calls it.</param>
/// <param name="Values">Every store-level value, with the member it came from.</param>
/// <param name="Databases">The databases the store is configured over.</param>
/// <param name="DatabaseNotice">Why the database list is empty or partial, or <see langword="null" />.</param>
internal sealed record StoreConfiguration(
    string StoreKey,
    string DisplayName,
    IReadOnlyList<ConfigurationValue> Values,
    IReadOnlyList<ConfiguredDatabase> Databases,
    string? DatabaseNotice);
