using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using DataStudio.Postgres;

using Npgsql;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// 43bf: no assembly of ours references a member Npgsql 11.0.0 removed from <c>NpgsqlConnectionStringBuilder</c>. A compiled
/// reference to one throws <see cref="MissingMethodException" /> on a host that takes Npgsql 11, and the package is built
/// against Npgsql 9.0.4, where it compiles. <c>Multiplexing</c> is read by keyword (<c>NpgsqlMultiplexing</c>).
/// </summary>
/// <remarks>
/// The scan reads each assembly's metadata, not its IL: every member reference names its declaring type and the member, whether
/// the loaded Npgsql has the member or not, so the scan holds at Npgsql 9, 10 and 11 alike and sees a reference the way the
/// JIT would, a call, a method group or a lambda's body included.
/// </remarks>
public class NpgsqlRemovedMemberTests
{
    /// <summary>What Npgsql 11.0.0 removed from the connection string builder: <c>Multiplexing</c> and <c>Write Coalescing Buffer Threshold Bytes</c>.</summary>
    private static readonly string[] Removed =
    [
        "get_Multiplexing", "set_Multiplexing", "get_WriteCoalescingBufferThresholdBytes", "set_WriteCoalescingBufferThresholdBytes",
    ];

    [Theory]
    [MemberData(nameof(Shipped))]
    public void No_shipped_assembly_references_a_member_Npgsql_11_removed(string name, Assembly assembly)
    {
        References(assembly, Removed).Should().BeEmpty(
            "Npgsql 11.0.0 removed these members of NpgsqlConnectionStringBuilder, so {0} would throw MissingMethodException on a host "
            + "that takes it; read the keyword instead (NpgsqlMultiplexing.Multiplexes)",
            name);
    }

    /// <summary>The scan's anti-vacuity: it finds a reference to a member of the builder that every Npgsql has, written in a method of this assembly.</summary>
    [Fact]
    public void The_scan_finds_a_member_reference_to_the_builder_in_the_assembly_it_reads()
    {
        References(typeof(NpgsqlRemovedMemberTests).Assembly, ["get_KeepAlive"]).Should().ContainSingle()
            .Which.Should().Contain("NpgsqlConnectionStringBuilder.get_KeepAlive");

        References(typeof(NpgsqlRemovedMemberTests).Assembly, ["get_NoSuchMember"]).Should().BeEmpty();

        KeepAliveOf(new NpgsqlConnectionStringBuilder("Host=probe")).Should().Be(0, "the probe method is called, so it is not dead code");
    }

    public static TheoryData<string, Assembly> Shipped() => new()
    {
        { "DataStudio.Postgres.Diagnostics", typeof(PostgresDiagnosticsOptions).Assembly },
        { "DataStudio.Postgres", typeof(PostgresOptions).Assembly },
        { "DataStudio.Marten", typeof(MartenOptions).Assembly },
    };

    /// <summary>Every reference <paramref name="assembly" /> makes to a member of <c>Npgsql.NpgsqlConnectionStringBuilder</c> named in <paramref name="members" />.</summary>
    private static string[] References(Assembly assembly, string[] members)
    {
        using PEReader pe = new(File.OpenRead(assembly.Location));
        MetadataReader metadata = pe.GetMetadataReader();

        return
        [
            .. metadata.MemberReferences
                .Select(metadata.GetMemberReference)
                .Where(reference => reference.Parent.Kind == HandleKind.TypeReference
                    && metadata.GetTypeReference((TypeReferenceHandle) reference.Parent) is var type
                    && metadata.GetString(type.Namespace) == "Npgsql"
                    && metadata.GetString(type.Name) == nameof(NpgsqlConnectionStringBuilder)
                    && members.Contains(metadata.GetString(reference.Name), StringComparer.Ordinal))
                .Select(reference => "Npgsql.NpgsqlConnectionStringBuilder." + metadata.GetString(reference.Name))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>A compiled reference to <c>get_KeepAlive</c>, which every Npgsql has, for the anti-vacuity above.</summary>
    private static int KeepAliveOf(NpgsqlConnectionStringBuilder builder) => builder.KeepAlive;
}
