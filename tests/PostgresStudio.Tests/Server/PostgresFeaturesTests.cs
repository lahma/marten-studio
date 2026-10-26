using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Server;

/// <summary>
/// The version gates every W5 packet gates on, pinned row by row: a row that moves is a check that lights up on the wrong
/// server. <c>VersionMatrixLiveTests</c> holds the same table to each server's catalog on 14 to 19.
/// </summary>
public class PostgresFeaturesTests
{
    public static TheoryData<string, int> Rows
    {
        get
        {
            TheoryData<string, int> rows = [];

            foreach ((PostgresFeature feature, int since) in Table)
            {
                rows.Add(feature.ToString(), since);
            }

            return rows;
        }
    }

    private static readonly (PostgresFeature Feature, int Since)[] Table =
    [
        (PostgresFeature.StatementsInfo, 140000),
        (PostgresFeature.StatementsToplevel, 140000),
        (PostgresFeature.ActivityQueryId, 140000),
        (PostgresFeature.PgStatWal, 140000),
        (PostgresFeature.ReplicationSlotStats, 140000),
        (PostgresFeature.WalReplayPauseState, 140000),
        (PostgresFeature.LocksWaitStart, 140000),
        (PostgresFeature.DatabaseSessionCounters, 140000),
        (PostgresFeature.VacuumFailsafeAge, 140000),
        (PostgresFeature.DatabaseOwnerRole, 140000),
        (PostgresFeature.ReadAllDataRole, 140000),
        (PostgresFeature.ReltuplesUnknownIsMinusOne, 140000),
        (PostgresFeature.StatementsJit, 150000),
        (PostgresFeature.ForceNextFlush, 150000),
        (PostgresFeature.DatabaseStatsResetNullAfterCrash, 150000),
        (PostgresFeature.PublicSchemaOwnedByDatabaseOwner, 150000),
        (PostgresFeature.PgStatIo, 160000),
        (PostgresFeature.LastScanColumns, 160000),
        (PostgresFeature.NewPageUpdates, 160000),
        (PostgresFeature.ExplainGenericPlan, 160000),
        (PostgresFeature.SlotConflicting, 160000),
        (PostgresFeature.ReservedConnections, 160000),
        (PostgresFeature.PgStatCheckpointer, 170000),
        (PostgresFeature.SlotInactiveSince, 170000),
        (PostgresFeature.ProgressIndexesTotal, 170000),
        (PostgresFeature.StatementsSharedBlkTimeNames, 170000),
        (PostgresFeature.WaitEventsView, 170000),
        (PostgresFeature.MaintainRole, 170000),
        (PostgresFeature.ExplainGenerationObject, 170000),
        (PostgresFeature.TransactionTimeout, 170000),
        (PostgresFeature.RelAllFrozen, 180000),
        (PostgresFeature.VacuumMaxThreshold, 180000),
        (PostgresFeature.PgStatIoBytes, 180000),
        (PostgresFeature.PgStatIoWal, 180000),
        (PostgresFeature.CheckpointerNumDone, 180000),
        (PostgresFeature.NotNullConstraints, 180000),
        (PostgresFeature.SequencesViewLocksAll, 180000),
        (PostgresFeature.ExplainFractionalRows, 180000),
        (PostgresFeature.ExplainBuffersByDefault, 180000),
        (PostgresFeature.IdleSlotTimeout, 180000),
        (PostgresFeature.ChecksumsDefaultOn, 180000),
        (PostgresFeature.TotalVacuumTime, 180000),
        (PostgresFeature.StatsResetPerTable, 190000),
        (PostgresFeature.AutovacuumScores, 190000),
        (PostgresFeature.MultixactStats, 190000),
        (PostgresFeature.PgStatLock, 190000),
        (PostgresFeature.JitOffByDefault, 190000),
        (PostgresFeature.MaxLocksDefault128, 190000),
        (PostgresFeature.WaitTypeBufferRenamed, 190000),
        (PostgresFeature.SubscriptionSyncTableErrorCount, 190000),
    ];

    [Theory]
    [MemberData(nameof(Rows))]
    public void Each_feature_arrives_in_the_release_B0_and_the_release_notes_say(string name, int since)
    {
        PostgresFeature feature = Enum.Parse<PostgresFeature>(name);

        PostgresFeatures.Since(feature).Should().Be(since);
        PostgresFeatures.Has(feature, since).Should().BeTrue("a server of that release has it");
        PostgresFeatures.Has(feature, since + 6).Should().BeTrue("so does any minor of it");
        PostgresFeatures.Has(feature, since - 10_000 + 99).Should().BeFalse("the release before does not, at any minor");
        PostgresFeatures.Needs(feature).Should().Be("needs PostgreSQL " + (since / 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Every_feature_has_exactly_one_row_and_the_table_is_oldest_first()
    {
        PostgresFeatures.Gates.Select(static x => x.Feature).Should().BeEquivalentTo(Enum.GetValues<PostgresFeature>(),
            "a feature without a gate is one Has() cannot answer");
        PostgresFeatures.Gates.Select(static x => x.Feature).Should().OnlyHaveUniqueItems();
        PostgresFeatures.Gates.Select(static x => x.Since).Should().BeInAscendingOrder();
        PostgresFeatures.Gates.Select(static x => (x.Feature, x.Since)).Should().Equal(Table, "every row is pinned above, in order");
    }

    [Fact]
    public void The_floor_is_14()
    {
        PostgresFeatures.MinimumSupported.Should().Be(140000);
        PostgresFeatures.MinimumSupportedMajor.Should().Be(14);
        PostgresFeatures.BelowFloor.Should().Be("below the supported floor (14)");

        PostgresFeatures.IsBelowFloor(130023).Should().BeTrue();
        PostgresFeatures.IsBelowFloor(140000).Should().BeFalse();
        PostgresFeatures.IsBelowFloor(new Version(13, 23)).Should().BeTrue();
        PostgresFeatures.IsBelowFloor(new Version(14, 24)).Should().BeFalse();
        PostgresFeatures.IsBelowFloor((Version?) null).Should().BeFalse("a version that could not be read says nothing about the floor");
    }

    [Fact]
    public void The_gates_majors_are_the_releases_they_name()
    {
        PostgresFeatures.Gates.Select(static x => x.Major).Distinct().Should().Equal(14, 15, 16, 17, 18, 19);
    }
}
