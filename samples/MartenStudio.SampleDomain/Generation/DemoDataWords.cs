using System.Globalization;
using System.Text;

namespace MartenStudio.SampleDomain.Generation;

/// <summary>
/// The word lists the generator builds names, addresses and SKUs out of, and the arithmetic that turns
/// an index into a value.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than a fake-data package, for the reason AGENTS.md hard rule 1 exists: a demo data
/// generator is not worth a dependency on anybody's release cadence, and six arrays of nouns produce
/// data that is quite good enough to tell a customer list from a product list at a glance.
/// </para>
/// <para>
/// Everything here is a pure function of its arguments. That is what makes a run reproducible: there is
/// no shared <see cref="Random" /> whose position depends on how many batches happened to be in flight,
/// so the millionth customer is the same customer whatever order the batches were built in.
/// </para>
/// </remarks>
public static class DemoDataWords
{
    /// <summary>Given names.</summary>
    public static readonly string[] FirstNames =
    [
        "Aino", "Bruno", "Cecilia", "Dmitri", "Elena", "Farah", "Gustav", "Helena", "Ivan", "Juhani",
        "Katri", "Lauri", "Marta", "Nils", "Olga", "Pekka", "Quentin", "Riikka", "Sanna", "Tuomas",
        "Ulla", "Viktor", "Wilma", "Xenia", "Yrjo", "Zara", "Anders", "Beatrix", "Casimir", "Dagny",
    ];

    public static readonly string[] LastNames =
    [
        "Aalto", "Berg", "Castillo", "Dahl", "Eriksson", "Fournier", "Grahn", "Halonen", "Ikonen",
        "Jarvinen", "Koskinen", "Lindqvist", "Makinen", "Niemi", "Ojala", "Peltonen", "Qvist",
        "Rantanen", "Salminen", "Tikkanen", "Uusitalo", "Virtanen", "Wahlberg", "Ylonen", "Zetterberg",
        "Andersson", "Bjork", "Carlsson", "Dubois", "Eklund",
    ];

    public static readonly string[] Cities =
    [
        "Helsinki", "Tampere", "Turku", "Oulu", "Jyvaskyla", "Kuopio", "Lahti", "Vaasa", "Pori",
        "Rovaniemi", "Stockholm", "Gothenburg", "Malmo", "Oslo", "Bergen", "Copenhagen", "Aarhus",
        "Tallinn", "Riga", "Vilnius",
    ];

    public static readonly string[] Streets =
    [
        "Harbour Road", "Mill Lane", "Birch Street", "Foundry Way", "Cathedral Square", "Pine Avenue",
        "Granite Row", "Lakeside Drive", "Station Street", "Copper Alley", "Windmill Road",
        "Spruce Terrace", "Market Street", "Anchor Quay", "Ironworks Road",
    ];

    public static readonly string[] Countries = ["FI", "SE", "NO", "DK", "EE", "LV", "LT", "DE", "NL", "PL"];

    public static readonly string[] Categories =
    [
        "tools", "hardware", "consumables", "fasteners", "abrasives", "safety", "lubricants",
        "electrical", "measuring", "packaging",
    ];

    public static readonly string[] Makes = ["Volvo", "Scania", "Toyota", "Ford", "MAN", "Iveco", "Renault", "DAF"];

    public static readonly string[] ProductNouns =
    [
        "Hammer", "Wrench", "Bearing", "Gasket", "Coupling", "Bracket", "Clamp", "Spindle", "Flange",
        "Bushing", "Valve", "Rivet", "Washer", "Pulley", "Sprocket", "Gauge", "Chisel", "Mandrel",
    ];

    public static readonly string[] ProductAdjectives =
    [
        "Heavy-duty", "Compact", "Stainless", "Galvanised", "Insulated", "Reinforced", "Precision",
        "Industrial", "Portable", "Modular",
    ];

    public static readonly string[] Severities = ["info", "info", "info", "warning", "error"];

    public static readonly string[] Tags =
    [
        "eu", "preferred", "trial", "legacy", "wholesale", "retail", "vip", "dormant",
    ];

    /// <summary>A stable pseudo-random 32-bit value for <paramref name="index" /> under <paramref name="seed" />.</summary>
    /// <remarks>
    /// A hash rather than a sequence, so value <c>n</c> never depends on values <c>0..n-1</c> having been
    /// produced first - which is what lets the generator build batch 137 without building batches 0..136,
    /// and what makes two runs of one plan agree regardless of cancellation, retries or batch size.
    /// </remarks>
    public static uint Noise(int seed, int index, int salt)
    {
        unchecked
        {
            uint x = (uint) seed * 2654435761u;
            x ^= (uint) index * 2246822519u;
            x = (x ^ (x >> 15)) * 2654435761u;
            x ^= (uint) salt * 3266489917u;
            x = (x ^ (x >> 13)) * 3266489917u;
            return x ^ (x >> 16);
        }
    }

    /// <summary>An element of <paramref name="values" />, chosen stably for <paramref name="index" />.</summary>
    public static string Pick(string[] values, int seed, int index, int salt) =>
        values[(int) (Noise(seed, index, salt) % (uint) values.Length)];

    /// <summary>A stable value in <c>[min, max)</c>.</summary>
    public static int Range(int seed, int index, int salt, int min, int max) =>
        min + (int) (Noise(seed, index, salt) % (uint) Math.Max(1, max - min));

    /// <summary>A stable money amount with two decimals.</summary>
    public static decimal Money(int seed, int index, int salt, int minCents, int maxCents) =>
        Range(seed, index, salt, minCents, maxCents) / 100m;

    /// <summary>The instant document or event <paramref name="index" /> is dated at.</summary>
    /// <remarks>
    /// Spread backwards from a fixed instant rather than from <c>UtcNow</c>: a generated set whose
    /// timestamps move every time it is regenerated makes a "last modified" column impossible to reason
    /// about, and the sample's seeded data is dated in 2026 too.
    /// </remarks>
    public static DateTimeOffset Timestamp(int seed, int index, int salt, int spreadMinutes = 525_600) =>
        Epoch.AddMinutes(-Range(seed, index, salt, 0, spreadMinutes)).AddSeconds(index % 60);

    /// <summary>The instant every generated timestamp is measured back from.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A person's name for <paramref name="index" />.</summary>
    public static string PersonName(int seed, int index) =>
        Pick(FirstNames, seed, index, 1) + " " + Pick(LastNames, seed, index, 2);

    /// <summary>
    /// A mailbox that is unique across runs, because <c>customer.email</c> carries a unique index and a
    /// second generation run into the same database must append rather than explode.
    /// </summary>
    public static string Email(string runId, int index)
    {
        var builder = new StringBuilder(48);
        builder.Append('c').Append(index.ToString(CultureInfo.InvariantCulture))
            .Append('.').Append(runId).Append("@demo.example");
        return builder.ToString();
    }
}
