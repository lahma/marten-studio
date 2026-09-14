using System.Text.Json.Serialization;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// The root of a Marten document hierarchy: one table, several .NET types, told apart by the
/// <c>mt_doc_type</c> discriminator column.
/// </summary>
/// <remarks>
/// Abstract on purpose. A hierarchy is the one shape where a collection's alias and its table are not the
/// same thing - browsing <c>/marten/documents/car</c> reads the vehicle table with
/// <c>mt_doc_type = 'car'</c> added - and it is the shape most likely to be got wrong by a UI that assumes
/// one alias means one table.
/// </remarks>
public abstract class Vehicle : IGeneratedDocument
{
    public Guid Id { get; set; }

    public string Make { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int Year { get; set; }

    /// <summary>Which demo-data generation run wrote this vehicle, or <see langword="null" /> for the seeder's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedRun { get; set; }
}

/// <summary>A car. Shares <see cref="Vehicle" />'s table.</summary>
public sealed class Car : Vehicle
{
    public int Doors { get; set; }

    public bool IsConvertible { get; set; }
}

/// <summary>A truck. Shares <see cref="Vehicle" />'s table.</summary>
public sealed class Truck : Vehicle
{
    public decimal PayloadTonnes { get; set; }

    public int Axles { get; set; }
}
