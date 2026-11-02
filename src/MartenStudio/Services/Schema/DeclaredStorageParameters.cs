using System.Collections;

using Marten.Schema;

using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Schema;

/// <summary>
/// The storage parameters a Marten document mapping declares - MartenStudio's half of <see cref="StorageParameters" />,
/// which compares them with a table's own.
/// </summary>
/// <remarks>
/// <c>Schema.For&lt;T&gt;().FillFactor(n)</c> and <c>.StorageParameter(name, value)</c> arrived in Marten 9.47. The
/// declared set is <c>DocumentMapping.StorageParameters</c>, an ordered dictionary keyed by the parameter's name.
/// </remarks>
internal static class DeclaredStorageParameters
{
    /// <summary>
    /// The storage parameters <paramref name="documentType" /> declares, or <see langword="null" /> when it is not a
    /// mapping that can declare any - which is "unknown", never "none".
    /// </summary>
    /// <param name="documentType">The document type.</param>
    public static IReadOnlyList<KeyValuePair<string, string>>? Of(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        if (documentType is not DocumentMapping mapping || mapping.StorageParameters is not IDictionary declared)
        {
            return null;
        }

        List<KeyValuePair<string, string>> parameters = [];
        foreach (DictionaryEntry entry in declared)
        {
            if (entry.Key?.ToString() is { Length: > 0 } name)
            {
                parameters.Add(new KeyValuePair<string, string>(name.ToLowerInvariant(), StorageParameters.Format(entry.Value)));
            }
        }

        return parameters;
    }
}
