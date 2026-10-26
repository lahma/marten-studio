using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Schema;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// The one rule T15 and the database browser's Keys badge share (<see cref="ForeignKeyCoverage.IsCovered" />), over B0 §9's
/// shapes - measured on 14, 17 and 18 - and the refinements around them, unit-tested once.
/// </summary>
public class ForeignKeyCoverageTests
{
    private static CoveringIndexCandidate BTree(params short[] keys) => new(keys, IsValid: true, IsPartial: false, "btree");

    [Fact]
    public void A_conjoined_Marten_key_on_its_member_and_tenant_id_is_covered_by_the_members_index_where_tenant_id_trails()
    {
        // ForeignKey<TRef> between two conjoined types: key (customer_id = 3, tenant_id = 4), index (customer_id) only - the
        // review's 13-buffer bitmap index scan, not a read of the whole table (W5-B1 review F1).
        ForeignKeyCoverage.Judge([3, 4], [], [BTree(1), BTree(3)], trailing: [4]).Should().Be(ForeignKeyCover.Covered);

        ForeignKeyCoverage.Judge([3, 4], [], [BTree(1), BTree(3)]).Should().Be(ForeignKeyCover.Partly, "without the source's word, an index on some of the key narrows it");
        ForeignKeyCoverage.Judge([3, 4], [], [BTree(1)], trailing: [4]).Should().Be(ForeignKeyCover.None, "nothing leads with customer_id or tenant_id");
        ForeignKeyCoverage.Judge([4], [], [BTree(1)], trailing: [4]).Should().Be(ForeignKeyCover.None, "a key that is all trailing column is judged whole");
        ForeignKeyCoverage.Judge([3, 4], [], [BTree(3, 4)]).Should().Be(ForeignKeyCover.Covered, "TenancyScope.PerTenant's (customer_id, tenant_id)");
    }

    [Fact]
    public void An_index_leading_with_some_of_the_keys_columns_partly_covers_it_and_one_leading_with_none_does_not()
    {
        ForeignKeyCoverage.Judge([2, 3], [], [BTree(2)]).Should().Be(ForeignKeyCover.Partly);
        ForeignKeyCoverage.Judge([2, 3], [], [BTree(3, 7)]).Should().Be(ForeignKeyCover.Partly);
        ForeignKeyCoverage.Judge([2, 3], [], [BTree(7, 2)]).Should().Be(ForeignKeyCover.None, "an index that does not lead with a key column is no help");
        ForeignKeyCoverage.Judge([2, 3], [], [BTree(2) with { IsValid = false }]).Should().Be(ForeignKeyCover.None, "an invalid index is never used");
        ForeignKeyCoverage.Judge([2, 3], [], [BTree(2) with { IsPartial = true }]).Should().Be(ForeignKeyCover.None);
        ForeignKeyCoverage.Judge([2], [], []).Should().Be(ForeignKeyCover.None);
    }

    [Fact]
    public void A_key_with_no_index_on_its_table_is_not_covered()
    {
        ForeignKeyCoverage.IsCovered([2], [], []).Should().BeFalse("store.child (parent_id), with no index: B0 §9 flags it");
        ForeignKeyCoverage.IsCovered([2], [], [BTree(1)]).Should().BeFalse("the primary key on id does not lead with parent_id");
    }

    [Fact]
    public void An_index_leading_with_the_keys_columns_covers_it()
    {
        ForeignKeyCoverage.IsCovered([2], [], [BTree(2)]).Should().BeTrue("store.child2 has an index on parent_id");
        ForeignKeyCoverage.IsCovered([2], [], [BTree(2, 3)]).Should().BeTrue("a wider index that leads with it covers it too");
    }

    [Fact]
    public void The_DCB_tag_tables_primary_key_leads_with_value_and_does_not_cover_the_seq_id_key()
    {
        // mt_event_tag_x: pk (value, seq_id), fk (seq_id) -> mt_events - B0 §9 caught it on a fixture of the same shape.
        ForeignKeyCoverage.IsCovered([2], [], [BTree(1, 2)]).Should().BeFalse();

        // marten#5694 (Marten 9.51.0) declares idx_mt_event_tag_<suffix>_seq_id, which does.
        ForeignKeyCoverage.IsCovered([2], [], [BTree(1, 2), BTree(2)]).Should().BeTrue();
    }

    [Fact]
    public void A_partial_index_alone_does_not_cover_it()
    {
        ForeignKeyCoverage.IsCovered([2], [], [new CoveringIndexCandidate([2], true, IsPartial: true, "btree")])
            .Should().BeFalse("store.partial_ref: the delete's lookup may match rows the predicate leaves out");
    }

    [Fact]
    public void An_index_on_b_a_covers_a_key_on_a_b_in_any_order_and_one_on_a_alone_does_not()
    {
        ForeignKeyCoverage.IsCovered([2, 3], [], [BTree(3, 2)]).Should().BeTrue("store.pair_ref: (b, a) for fk (a, b) is not flagged");
        ForeignKeyCoverage.IsCovered([2, 3], [], [BTree(2)]).Should().BeFalse("an index on a alone reads every row of that a");
        ForeignKeyCoverage.IsCovered([2, 3], [], [BTree(2, 4, 3)]).Should().BeFalse("the key's columns must lead, not merely appear");
    }

    [Fact]
    public void A_partitioned_table_is_covered_by_its_partitioned_index()
    {
        // store.pref: partitioned, with a partitioned index on the key - B0 §9 does not flag it.
        ForeignKeyCoverage.IsCovered([2], [3], [BTree(2)]).Should().BeTrue();
    }

    [Fact]
    public void Partition_key_columns_of_the_key_are_left_to_pruning()
    {
        // Marten's tag table under archived partitioning: fk (seq_id, is_archived), partitioned on is_archived, index (seq_id).
        ForeignKeyCoverage.IsCovered([2, 3], [3], [BTree(2)]).Should().BeTrue("the lookup prunes to one partition on is_archived");
        ForeignKeyCoverage.IsCovered([2, 3], [], [BTree(2)]).Should().BeFalse("without the pruning the same index is a prefix only");
        ForeignKeyCoverage.IsCovered([3], [3], []).Should().BeFalse("a key that is all partition key still reads its partition whole");
        ForeignKeyCoverage.IsCovered([2, 3], [3, 4], [BTree(2)]).Should().BeFalse("a partition key column outside the key prunes nothing for it");
        ForeignKeyCoverage.IsCovered([2, 3], [0], [BTree(2)]).Should().BeFalse("an expression in the partition key is not one of the key's columns");
    }

    [Theory]
    [InlineData("gin", 1, false)]
    [InlineData("brin", 1, false)]
    [InlineData("hash", 1, true)]
    [InlineData("btree", 1, true)]
    public void Only_a_btree_or_a_one_column_hash_answers_the_lookup(string method, int columns, bool covers)
    {
        short[] keys = [.. Enumerable.Range(2, columns).Select(static x => (short) x)];
        ForeignKeyCoverage.IsCovered(keys, [], [new CoveringIndexCandidate(keys, true, false, method)]).Should().Be(covers);
    }

    [Fact]
    public void An_invalid_index_or_an_expression_among_the_leading_columns_does_not_cover_it()
    {
        ForeignKeyCoverage.IsCovered([2], [], [new CoveringIndexCandidate([2], IsValid: false, false, "btree")]).Should().BeFalse("a failed concurrent build is never used");
        ForeignKeyCoverage.IsCovered([2], [], [BTree(0)]).Should().BeFalse("an expression's attribute number is zero, never the key's");
        ForeignKeyCoverage.IsCovered([2], [], [BTree(2, 0)]).Should().BeTrue("an expression after the key's columns does not stop it");
    }

    [Fact]
    public void The_browsers_index_list_parses_as_the_rule_reads_it()
    {
        IReadOnlyList<CoveringIndexCandidate> parsed = CatalogForeignKey.ParseIndexes(
            "[{\"k\": [2], \"m\": \"btree\", \"p\": false, \"v\": true}, {\"k\": [1, 2], \"m\": \"btree\", \"p\": true, \"v\": true}, {\"k\": [3], \"m\": \"gin\", \"p\": false, \"v\": false}]");

        parsed.Should().HaveCount(3);
        parsed[0].Should().BeEquivalentTo(new CoveringIndexCandidate([2], true, false, "btree"));
        parsed[1].IsPartial.Should().BeTrue();
        parsed[2].IsValid.Should().BeFalse();
        parsed[2].AccessMethod.Should().Be("gin");

        new CatalogForeignKey("app", "child", ["parent_id"], "app", "parent", ["id"], "child_parent_fk", true, "a", "a")
        {
            Attributes = [2],
            Indexes = parsed,
        }.Covered.Should().BeTrue();

        new CatalogForeignKey("app", "child", ["parent_id"], "app", "parent", ["id"], "child_parent_fk", true, "a", "a")
            .Covered.Should().BeNull("a key whose columns were not read says nothing either way");
    }
}
