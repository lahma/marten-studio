using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// F6's half of the Tables tab assembly: the update counters, TOAST and last autovacuum pass through, the
/// storage parameters are compared with the declarations, and a tenancy registry table withholds its shares.
/// </summary>
public class SchemaTableAssemblerHealthTests
{
    private static readonly string CustomerKey = SchemaKey.For(DocumentSchema, "mt_doc_dbcustomer");

    private static readonly IReadOnlyDictionary<string, string> TypeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CustomerKey] = nameof(DbCustomer) };

    private static TableStatsRow Row(string table, IReadOnlyList<string>? options = null) =>
        new(DocumentSchema, table, 16384, 8192, 8192, 10, 10, 5, 1, 1, null, null,
            Updates: 100, HotUpdates: 40, LastAutovacuum: DateTimeOffset.UnixEpoch, ToastBytes: 4096, StorageOptions: options ?? []);

    private static TableStats Assemble(
        TableStatsRow row,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>?>? declared = null,
        bool partitionCountsShown = false) =>
        SchemaTableAssembler.Assemble([row], Classifier(), "default", TypeNames, partitionCountsShown, declared).Single();

    [Fact]
    public void The_update_counters_TOAST_and_last_autovacuum_pass_through_and_the_shares_follow_from_them()
    {
        TableStats table = Assemble(Row("mt_doc_dbcustomer"));

        table.Updates.Should().Be(100);
        table.HotUpdates.Should().Be(40);
        table.HotShare.Should().Be(0.4);
        table.DeadShare.Should().BeApproximately(5.0 / 15, 1e-9);
        table.ToastBytes.Should().Be(4096);
        table.LastAutovacuum.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void A_declared_parameter_is_compared_with_the_tables_reloptions()
    {
        TableStats table = Assemble(
            Row("mt_doc_dbcustomer", ["fillfactor=90", "autovacuum_enabled=false"]),
            new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>?>(StringComparer.OrdinalIgnoreCase)
            {
                [CustomerKey] = [new("fillfactor", "80")],
            });

        table.DeclaredParametersKnown.Should().BeTrue();
        table.StorageParameters.Should().Equal(
            new StorageParameterState("fillfactor", "80", "90", StorageParameterStatus.Drift),
            new StorageParameterState("autovacuum_enabled", null, "false", StorageParameterStatus.SetOutsideMarten));
    }

    [Fact]
    public void A_partitioned_table_s_leaves_rollup_is_compared_and_a_disagreement_is_partitions_differ()
    {
        TableStats table = Assemble(
            Row("mt_doc_dbcustomer", ["fillfactor=80"]) with { DifferingStorageOptions = ["autovacuum_enabled"] },
            new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>?>(StringComparer.OrdinalIgnoreCase)
            {
                [CustomerKey] = [new("fillfactor", "80")],
            });

        table.StorageParameters.Should().Equal(
            new StorageParameterState("fillfactor", "80", "80", StorageParameterStatus.Matches),
            new StorageParameterState("autovacuum_enabled", null, null, StorageParameterStatus.PartitionsDiffer));
    }

    [Fact]
    public void Without_declarations_the_parameters_are_listed_and_nothing_is_called_drift()
    {
        TableStats table = Assemble(Row("mt_doc_dbcustomer", ["fillfactor=90"]),
            new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>?>(StringComparer.OrdinalIgnoreCase) { [CustomerKey] = null });

        table.DeclaredParametersKnown.Should().BeFalse("a Marten below 9.47 cannot declare them: unknown, never none");
        table.StorageParameters.Should().OnlyContain(static x => x.Status == StorageParameterStatus.SetOutsideMarten);
    }

    [Fact]
    public void A_tenancy_registry_table_withholds_its_update_counters_with_its_other_figures()
    {
        TableStats table = Assemble(Row("mt_tenant_partitions"));

        table.FiguresWithheld.Should().BeTrue();
        table.Updates.Should().BeNull();
        table.HotUpdates.Should().BeNull();
        table.HotShare.Should().BeNull();
        table.DeadShare.Should().BeNull();
    }
}
