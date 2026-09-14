using System.Text.Json.Serialization;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// A strong-typed id. Marten recognises a record struct with a single <c>Value</c> property, which is
/// exactly the shape the studio has to be able to read an id back out of and write one back in to.
/// </summary>
public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An order: a strong-typed id, soft deletion, a foreign key to <see cref="Customer" /> and optimistic
/// concurrency. Between them those four cover most of what makes a document interesting to edit.
/// </summary>
public sealed class Order : IGeneratedDocument
{
    public OrderId Id { get; set; }

    public Guid CustomerId { get; set; }

    public string Reference { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public string Status { get; set; } = "Placed";

    public List<OrderLine> Lines { get; set; } = [];

    public DateTimeOffset PlacedAt { get; set; }

    /// <summary>Which demo-data generation run wrote this order, or <see langword="null" /> for the seeder's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedRun { get; set; }
}

/// <summary>One line of an order, inside the order's JSON.</summary>
public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);
