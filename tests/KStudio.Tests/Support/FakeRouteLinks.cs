using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Support;

/// <summary>
/// A studio's route links for a test (what MartenStudio implements): it records every call and answers from a function, so that a test can say that the
/// module drew the address verbatim and what scope it passed.
/// </summary>
internal sealed class FakeRouteLinks : IStudioRouteLinks
{
    /// <summary>Every document asked for.</summary>
    public List<(StudioLinkScope Scope, string Type, string Id)> Documents { get; } = [];

    /// <summary>Every stream asked for.</summary>
    public List<(StudioLinkScope Scope, string Id)> Streams { get; } = [];

    /// <summary>The answer for a document; an address that carries what was asked, by default.</summary>
    public Func<StudioLinkScope, string, string, string?> DocumentAnswer { get; set; } =
        static (scope, type, id) => $"marten/documents/detail?store={scope.StoreKey}&tenant={scope.TenantId}&type={Uri.EscapeDataString(type)}&id={Uri.EscapeDataString(id)}";

    /// <summary>The answer for a stream.</summary>
    public Func<StudioLinkScope, string, string?> StreamAnswer { get; set; } =
        static (scope, id) => $"marten/events/stream?store={scope.StoreKey}&tenant={scope.TenantId}&id={Uri.EscapeDataString(id)}";

    /// <inheritdoc />
    public string? ToDocument(StudioLinkScope scope, string documentType, string id)
    {
        Documents.Add((scope, documentType, id));
        return DocumentAnswer(scope, documentType, id);
    }

    /// <inheritdoc />
    public string? ToStream(StudioLinkScope scope, string streamIdOrKey)
    {
        Streams.Add((scope, streamIdOrKey));
        return StreamAnswer(scope, streamIdOrKey);
    }
}
