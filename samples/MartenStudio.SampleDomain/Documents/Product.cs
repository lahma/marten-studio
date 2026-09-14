using System.Text.Json.Serialization;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// A product keyed by a string the application assigns, through Marten's <c>UseIdentityKey()</c>.
/// </summary>
/// <remarks>
/// <c>UseIdentityKey()</c> gives the table a <c>varchar</c> primary key rather than a <c>uuid</c>, which is
/// why the studio types every id parameter from the <em>column</em> rather than from
/// <c>IDocumentType.IdType</c>. A SKU also contains characters a path segment would have to escape, which
/// is the other half of why ids travel in the query string (D9).
/// </remarks>
public sealed class Product : IGeneratedDocument
{
    /// <summary>The SKU, assigned by the application rather than by Marten.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public bool Discontinued { get; set; }

    /// <summary>Which demo-data generation run wrote this product, or <see langword="null" /> for the seeder's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedRun { get; set; }
}
