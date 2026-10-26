using PostgresStudio.Services.Replication;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// R02's backlog is the distance between two WAL file names, parsed as Postgres builds them (<c>XLogFileName</c>): a
/// timeline, then the segment number split at every 4 GiB - on every release, <c>pg_split_walfile_name</c> being 16's alone.
/// </summary>
public class WalFileNameTests
{
    [Theory]
    [InlineData("000000010000000000000001", 1u, 1ul)]
    [InlineData("0000000100000000000000FF", 1u, 255ul)]
    [InlineData("000000010000000100000000", 1u, 256ul)]
    [InlineData("00000002000000030000000A", 2u, 3ul * 256 + 10)]
    public void A_16_MiB_segment_name_is_its_timeline_and_number(string name, uint timeline, ulong number)
    {
        WalFileName.TryParse(name, Segment16, out WalSegment segment).Should().BeTrue();
        segment.Should().Be(new WalSegment(timeline, number));
    }

    [Fact]
    public void With_64_MiB_segments_a_log_holds_64_of_them()
    {
        const long Segment64 = 64L * 1024 * 1024;

        WalFileName.TryParse("00000001000000000000003F", Segment64, out WalSegment last).Should().BeTrue();
        last.Number.Should().Be(63);

        WalFileName.TryParse("000000010000000100000000", Segment64, out WalSegment next).Should().BeTrue();
        next.Number.Should().Be(64, "the next 4 GiB starts at segment 64 when a segment is 64 MiB");

        WalFileName.TryParse("000000010000000000000040", Segment64, out _).Should().BeFalse(
            "a 64 MiB server never names segment 0x40 within a log - the name belongs to a 16 MiB server");
    }

    [Theory]
    [InlineData("00000002.history")]
    [InlineData("000000010000000000000003.partial")]
    [InlineData("000000010000000000000002.00000028.backup")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00000001000000000000000G")]
    public void A_history_partial_or_backup_file_is_not_a_segment(string? name)
    {
        WalFileName.TryParse(name, Segment16, out _).Should().BeFalse();
    }

    [Fact]
    public void The_distance_counts_segments_across_a_timeline_change()
    {
        // A promotion moves to timeline 2 and the numbering continues: segment 3 on 1, segment 5 on 2.
        ArchiveBacklog backlog = ArchiveBacklog.Measure("000000010000000000000003", "000000020000000000000005", Segment16)!;

        backlog.Segments.Should().Be(2);
        backlog.NothingArchived.Should().BeFalse();
        backlog.Bytes.Should().Be(2 * Segment16);
    }

    [Fact]
    public void Across_a_4_GiB_boundary_the_distance_is_continuous()
    {
        ArchiveBacklog.Measure("0000000100000000000000FE", "000000010000000100000002", Segment16)!.Segments.Should().Be(4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000002.history")]
    [InlineData("000000010000000000000003.partial")]
    [InlineData("000000010000000000000002.00000028.backup")]
    public void Nothing_archived_yet_measures_from_the_start_and_says_so(string? lastArchived)
    {
        ArchiveBacklog backlog = ArchiveBacklog.Measure(lastArchived, Wal(70), Segment16)!;

        backlog.NothingArchived.Should().BeTrue();
        backlog.Segments.Should().Be(70);
    }

    [Fact]
    public void No_current_file_or_no_segment_size_is_no_backlog()
    {
        ArchiveBacklog.Measure(Wal(1), null, Segment16).Should().BeNull("a standby names no current file");
        ArchiveBacklog.Measure(Wal(1), Wal(5), null).Should().BeNull();
    }
}
