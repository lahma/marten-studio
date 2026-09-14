namespace MartenStudio.Services.Configuration;

/// <summary>
/// What the Configuration page reads: a read-only dump of what the host told Marten.
/// </summary>
/// <remarks>
/// There is no write path here and there will not be one. Every value on that screen came from the host
/// application's own <c>AddMarten(...)</c> call, and the studio's whole premise is that registering it
/// changes nothing about the application it is embedded in - a screen that could edit
/// <c>StoreOptions</c> would be the end of that.
/// </remarks>
internal interface IConfigurationService
{
    /// <summary>
    /// Describes the store, event store and document types in scope.
    /// </summary>
    /// <param name="scope">The store and database to describe.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<StudioConfiguration> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
