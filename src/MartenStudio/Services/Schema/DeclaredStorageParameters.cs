using System.Collections;
using System.Reflection;

using Marten.Schema;

using MartenStudio.Services.Configuration;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Schema;

/// <summary>
/// The storage parameters a Marten document mapping declares - MartenStudio's half of <see cref="StorageParameters" />,
/// which compares them with a table's own.
/// </summary>
/// <remarks>
/// <c>Schema.For&lt;T&gt;().FillFactor(n)</c> and <c>.StorageParameter(name, value)</c> arrived in Marten 9.47
/// (<see cref="RuntimeFeature.DeclaredStorageParameters" />). Below it a document mapping declares none, so there is
/// nothing to compare against. Above it the declared set is read by reflection off <c>DocumentMapping.StorageParameters</c>,
/// a non-generic <c>OrderedDictionary</c> keyed by the parameter's name.
/// </remarks>
internal static class DeclaredStorageParameters
{
    /// <summary>
    /// The storage parameters <paramref name="documentType" /> declares, or <see langword="null" /> when this
    /// Marten cannot declare any - which is "unknown", never "none".
    /// </summary>
    /// <param name="documentType">The document type.</param>
    /// <param name="runtime">The Marten this host runs.</param>
    public static IReadOnlyList<KeyValuePair<string, string>>? Of(IDocumentType documentType, MartenRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!RuntimeFeature.DeclaredStorageParameters.IsAvailable(runtime) || documentType is not DocumentMapping mapping)
        {
            return null;
        }

        try
        {
            if (typeof(DocumentMapping).GetProperty("StorageParameters", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mapping)
                is not IDictionary declared)
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
        catch (Exception exception) when (exception is TargetInvocationException or InvalidCastException or MemberAccessException)
        {
            // A light-up read that cannot be made is "unknown", the same as a Marten that has no such member.
            return null;
        }
    }
}
