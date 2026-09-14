using System.Text.Json.Serialization;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// A customer: the plainest shape Marten has - a Guid id, a duplicated column with a unique index, a
/// value object serialized inline, and a collection.
/// </summary>
public sealed class Customer : IGeneratedDocument
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Duplicated into its own column with a unique index, so the studio has a duplicated field to render
    /// and an index to advise against.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>A value object, which lives inside the document's JSON rather than in a column.</summary>
    public Address Address { get; set; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public List<string> Tags { get; set; } = [];

    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    /// Which demo-data generation run wrote this customer, or <see langword="null" /> for the seeder's
    /// own. Absent from the JSON when it is null, so a seeded document round-trips unchanged (D7).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedRun { get; set; }
}

/// <summary>A value object. Never its own table - it is part of the customer's JSON.</summary>
public sealed record Address(string Street, string City, string PostalCode, string Country);
