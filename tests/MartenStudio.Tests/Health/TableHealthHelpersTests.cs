using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;

using Microsoft.Extensions.DependencyInjection;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>The pieces the tables-and-indexes checks share: history, storage parameters, redundancy, the table set.</summary>
public class TableHealthHelpersTests
{
    // -----------------------------------------------------------------------------------------------
    // TableHistory
    // -----------------------------------------------------------------------------------------------

    private static HealthTrend Trend(params (int MinutesAgo, double Value)[] points) =>
        new("s", [.. points.Select(static x => (Now.AddMinutes(-x.MinutesAgo), x.Value))]);

    [Fact]
    public void A_delta_is_the_change_since_the_last_reset_and_needs_two_points_of_the_same_run()
    {
        TableHistory.Delta(Trend()).Should().BeNull();
        TableHistory.Delta(Trend((0, 5))).Should().BeNull();
        TableHistory.Delta(Trend((10, 5), (0, 9)))!.Change.Should().Be(4);
        TableHistory.Delta(Trend((20, 100), (10, 2), (0, 9)))!.Should().BeEquivalentTo(new CounterDelta(7, Now.AddMinutes(-10), Now));
        TableHistory.Delta(Trend((10, 100), (0, 2))).Should().BeNull("a reset in the latest sample leaves one point of the new run");
    }

    [Fact]
    public void Held_since_is_the_start_of_the_latest_unbroken_run_over_the_sweeps_the_check_ran_in()
    {
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(40), [("ran", 1), ("bad", 1)]),
            (TimeSpan.FromMinutes(30), [("ran", 1)]),
            (TimeSpan.FromMinutes(20), [("ran", 1), ("bad", 1)]),
            (TimeSpan.FromMinutes(10), [("other", 1)]),
            (TimeSpan.Zero, [("ran", 1), ("bad", 1)]));

        TableHistory.HeldSince(history, "ran", "bad").Should().Be(Now.AddMinutes(-20), "a sweep that did not run the check neither breaks nor extends the run");
        TableHistory.HeldSince(History((TimeSpan.Zero, [("ran", 1)])), "ran", "bad").Should().BeNull();
    }

    // -----------------------------------------------------------------------------------------------
    // StorageParameters
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Reloptions_parse_into_lower_case_names_and_values_and_an_entry_without_equals_is_skipped()
    {
        StorageParameters.Parse(["fillfactor=80", "Autovacuum_Enabled=off", "nonsense"]).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["fillfactor"] = "80", ["autovacuum_enabled"] = "off" });
    }

    [Theory]
    [InlineData("80", "80", true)]
    [InlineData("0.02", "0.020", true)]
    [InlineData("true", "on", true)]
    [InlineData("1", "true", true)]
    [InlineData("false", "off", true)]
    [InlineData("80", "70", false)]
    [InlineData("true", "off", false)]
    public void Values_compare_the_way_Postgres_reads_them(string declared, string actual, bool same)
    {
        StorageParameters.SameValue(declared, actual).Should().Be(same);
    }

    [Fact]
    public void Declared_parameters_are_matched_drifted_or_set_outside_Marten()
    {
        IReadOnlyList<StorageParameterState> states = StorageParameters.Compare(
            [new("fillfactor", "80"), new("autovacuum_vacuum_scale_factor", "0.02")],
            ["fillfactor=80", "autovacuum_enabled=false"]);

        states.Should().Equal(
            new StorageParameterState("fillfactor", "80", "80", StorageParameterStatus.Matches),
            new StorageParameterState("autovacuum_vacuum_scale_factor", "0.02", null, StorageParameterStatus.Drift),
            new StorageParameterState("autovacuum_enabled", null, "false", StorageParameterStatus.SetOutsideMarten));
    }

    /// <summary>
    /// A partitioned table has no storage parameters of its own - Postgres keeps none on the parent - so what is
    /// compared is the leaves' rollup: a parameter every leaf has at one value is the table's, and one they disagree
    /// on is "partitions differ", never a partition's name.
    /// </summary>
    [Fact]
    public void A_partitioned_table_s_parameters_are_its_leaves_agreement_and_a_disagreement_is_said_without_a_name()
    {
        IReadOnlyList<StorageParameterState> states = StorageParameters.Compare(
            [new("fillfactor", "80"), new("autovacuum_vacuum_scale_factor", "0.02")],
            ["fillfactor=80"],
            ["autovacuum_vacuum_scale_factor", "autovacuum_enabled"]);

        states.Should().Equal(
            new StorageParameterState("fillfactor", "80", "80", StorageParameterStatus.Matches),
            new StorageParameterState("autovacuum_vacuum_scale_factor", "0.02", null, StorageParameterStatus.PartitionsDiffer),
            new StorageParameterState("autovacuum_enabled", null, null, StorageParameterStatus.PartitionsDiffer));
    }

    [Fact]
    public void A_disagreement_on_an_undeclared_parameter_is_listed_even_with_declarations_unknown()
    {
        StorageParameters.Compare(null, [], ["fillfactor"]).Should().Equal(
            new StorageParameterState("fillfactor", null, null, StorageParameterStatus.PartitionsDiffer));
    }

    [Fact]
    public void With_declarations_unknown_every_parameter_on_the_table_is_simply_listed()
    {
        StorageParameters.Compare(null, ["fillfactor=90"]).Should().Equal(
            new StorageParameterState("fillfactor", null, "90", StorageParameterStatus.SetOutsideMarten));
    }

    [Fact]
    public void A_declared_value_formats_invariantly()
    {
        StorageParameters.Format(0.02).Should().Be("0.02");
        StorageParameters.Format(80).Should().Be("80");
        StorageParameters.Format(true).Should().Be("true");
    }

    /// <summary>
    /// Light-up, pinned at both ends: the floor cannot declare storage parameters (unknown, never "none");
    /// from 9.47 a mapping's declarations are read.
    /// </summary>
    [Fact]
    public void Declared_parameters_are_read_only_where_this_Marten_can_declare_them()
    {
        var plain = SqlTestStore.DocumentType<HealthOrderDoc>();

        DeclaredStorageParameters.Of(plain, Floor).Should().BeNull("Marten 9.39 has no DocumentMapping.StorageParameters");

        if (RuntimeFeature.DeclaredStorageParameters.IsAvailable(MartenRuntime.Current))
        {
            DeclaredStorageParameters.Of(plain, MartenRuntime.Current).Should().BeEmpty("nothing declared is nothing, on a Marten that can declare");
        }
        else
        {
            DeclaredStorageParameters.Of(plain, MartenRuntime.Current).Should().BeNull();
        }
    }

    // -----------------------------------------------------------------------------------------------
    // IndexRedundancy
    // -----------------------------------------------------------------------------------------------

    private static readonly MartenTable Orders = MartenTables.Table("s", "t", MartenTableKind.Managed, outsideStoreSchemas: false);

    [Theory]
    [InlineData("CREATE INDEX i ON s.t USING btree (a) WHERE (b = 1)", "(b = 1)")]
    [InlineData("CREATE INDEX i ON s.t USING btree (a)", null)]
    [InlineData("CREATE INDEX i ON s.t USING btree (a) INCLUDE (b) WHERE ((data ->> 'x WHERE y'::text) IS NOT NULL)", "((data ->> 'x WHERE y'::text) IS NOT NULL)")]
    [InlineData("CREATE INDEX i ON s.t USING btree ((data ->> ' WHERE '::text))", null)]
    [InlineData("CREATE INDEX \" USING \" ON s.t USING btree (a) WHERE a", "a")]
    public void The_predicate_is_what_follows_the_top_level_WHERE_after_the_column_list(string definition, string? predicate)
    {
        IndexRedundancy.Predicate(definition).Should().Be(predicate);
    }

    [Fact]
    public void A_unique_index_is_never_redundant_to_a_non_unique_one_but_the_reverse_is()
    {
        HealthIndexRow unique = IndexRow(Orders, "u", "a") with { IsUnique = true };
        HealthIndexRow plain = IndexRow(Orders, "p", "a");

        IndexRedundancy.Covers(plain, unique).Should().BeNull();
        IndexRedundancy.Covers(unique, plain).Should().Be(RedundancyKind.Duplicate);
        IndexRedundancy.Find([unique, plain]).Should().ContainSingle().Which.Index.Should().BeSameAs(plain);
    }

    [Fact]
    public void A_unique_index_on_a_prefix_enforces_more_and_is_not_redundant()
    {
        HealthIndexRow narrow = IndexRow(Orders, "u_a", "a") with { IsUnique = true };
        HealthIndexRow wide = IndexRow(Orders, "u_ab", "a", "b") with { IsUnique = true };

        IndexRedundancy.Find([narrow, wide]).Should().BeEmpty();
    }

    [Fact]
    public void A_primary_key_is_never_redundant_and_covers_a_duplicate_of_itself()
    {
        HealthIndexRow pk = IndexRow(Orders, "pk", "id") with { IsUnique = true, IsPrimaryKey = true };
        HealthIndexRow copy = IndexRow(Orders, "copy", "id") with { IsUnique = true };

        IndexRedundancy.Find([pk, copy]).Should().ContainSingle().Which.Index.Should().BeSameAs(copy);
    }

    [Theory]
    [InlineData("opclass")]
    [InlineData("collation")]
    [InlineData("order")]
    [InlineData("predicate")]
    [InlineData("method")]
    [InlineData("invalid")]
    [InlineData("include")]
    [InlineData("other table")]
    public void Anything_that_changes_what_an_index_answers_keeps_it_from_being_redundant(string difference)
    {
        HealthIndexRow cover = IndexRow(Orders, "ab", "a", "b");
        HealthIndexRow index = IndexRow(Orders, "a", "a");

        HealthIndexRow changed = difference switch
        {
            "opclass" => index with { OperatorClasses = [10_043u] },
            "collation" => index with { Collations = [950u] },
            "order" => index with { Options = [3] },
            "predicate" => index with { Definition = index.Definition + " WHERE (a > 0)" },
            "method" => index with { AccessMethod = "hash" },
            "invalid" => index,
            "include" => index with { Columns = ["a", "c"] },
            _ => index with { Table = "elsewhere" },
        };

        HealthIndexRow covering = difference == "invalid" ? cover with { IsValid = false } : cover;

        IndexRedundancy.Find([changed, covering]).Should().BeEmpty();
    }

    [Fact]
    public void A_GIN_index_is_redundant_only_as_an_exact_duplicate_never_as_a_prefix()
    {
        HealthIndexRow ginWide = IndexRow(Orders, "g_ab", "a", "b") with { AccessMethod = "gin" };
        HealthIndexRow ginNarrow = IndexRow(Orders, "g_a", "a") with { AccessMethod = "gin" };
        HealthIndexRow ginCopy = IndexRow(Orders, "g_ab_copy", "a", "b") with { AccessMethod = "gin" };

        IndexRedundancy.Find([ginWide, ginNarrow]).Should().BeEmpty();
        IndexRedundancy.Find([ginWide, ginCopy]).Should().ContainSingle().Which.Index.Name.Should().Be("g_ab_copy", "between equals, the name that sorts last goes");
    }

    [Fact]
    public void An_exclusion_constraint_s_index_is_never_redundant_and_covers_no_unique_index()
    {
        HealthIndexRow exclusion = IndexRow(Orders, "x_a", "a") with { AccessMethod = "gist", IsExclusion = true };
        HealthIndexRow plain = IndexRow(Orders, "g_a", "a") with { AccessMethod = "gist" };
        HealthIndexRow unique = IndexRow(Orders, "u_a", "a") with { IsUnique = true };
        HealthIndexRow btreeExclusion = IndexRow(Orders, "x_a_btree", "a") with { IsExclusion = true };

        IndexRedundancy.Covers(plain, exclusion).Should().BeNull("an exclusion index enforces its constraint whatever else answers its queries");
        IndexRedundancy.Covers(exclusion, plain).Should().Be(RedundancyKind.Duplicate, "for an index that enforces nothing, it answers the same queries");
        IndexRedundancy.Find([exclusion, plain]).Should().ContainSingle().Which.Index.Should().BeSameAs(plain);
        IndexRedundancy.Covers(btreeExclusion, unique).Should().BeNull("an exclusion index is not unique, so it covers no uniqueness constraint");
    }

    [Fact]
    public void A_unique_NULLS_NOT_DISTINCT_index_is_covered_only_by_another()
    {
        HealthIndexRow nullsNotDistinct = IndexRow(Orders, "u_a_nnd", "a") with { IsUnique = true, NullsNotDistinct = true };
        HealthIndexRow nullsDistinct = IndexRow(Orders, "u_a", "a") with { IsUnique = true };
        HealthIndexRow anotherNnd = IndexRow(Orders, "u_a_nnd_copy", "a") with { IsUnique = true, NullsNotDistinct = true };

        IndexRedundancy.Covers(nullsDistinct, nullsNotDistinct).Should().BeNull("it lets two nulls in, which the other refuses");
        IndexRedundancy.Covers(nullsNotDistinct, nullsDistinct).Should().Be(RedundancyKind.Duplicate, "refusing duplicate nulls enforces more, not less");
        IndexRedundancy.Find([nullsNotDistinct, nullsDistinct]).Should().ContainSingle().Which.Index.Should().BeSameAs(nullsDistinct);
        IndexRedundancy.Covers(anotherNnd, nullsNotDistinct).Should().Be(RedundancyKind.Duplicate);
    }

    [Fact]
    public void An_immediate_unique_index_is_not_covered_by_a_deferrable_one()
    {
        HealthIndexRow immediate = IndexRow(Orders, "u_a", "a") with { IsUnique = true };
        HealthIndexRow deferrable = IndexRow(Orders, "u_a_deferrable", "a") with { IsUnique = true, IsImmediate = false };
        HealthIndexRow plain = IndexRow(Orders, "p_a", "a");

        IndexRedundancy.Covers(deferrable, immediate).Should().BeNull("a deferred check lets a duplicate stand until commit, and ON CONFLICT cannot use it");
        IndexRedundancy.Covers(immediate, deferrable).Should().Be(RedundancyKind.Duplicate);
        IndexRedundancy.Find([immediate, deferrable]).Should().ContainSingle().Which.Index.Should().BeSameAs(deferrable);
        IndexRedundancy.Covers(deferrable, plain).Should().Be(RedundancyKind.Duplicate, "a non-unique index enforces nothing for the timing to matter to");
    }

    // -----------------------------------------------------------------------------------------------
    // MartenTables: the set comes from the configuration, and a hidden type is never in it
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_table_set_holds_the_visible_document_tables_the_event_store_and_mt_hilo_and_never_a_hidden_type()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_set";
            options.Events.DatabaseSchemaName = "studio_set_events";
            options.Schema.For<HealthOrderDoc>().SoftDeleted();
            options.Schema.For<HealthIntDoc>();
        });

        HealthCheckContext context = new(
            new ResolvedScope(
                new StudioScope("default", string.Empty, null),
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                store,
                store.Storage.Database),
            MartenStudioSettings.Of(new MartenStudioOptions { IsDocumentTypeVisible = static type => type != typeof(HealthIntDoc) }),
            MartenStudioRuntime.Current,
            services,
            new HealthSqlRunner(new HealthConnection(MartenStudioDatabase.Of(store.Storage.Database)), new ReadOnlySqlOptions(), null),
            Now,
            new HealthSweepMemo(),
            CancellationToken.None);

        MartenTableSet set = MartenTables.Read(context);

        set.Tables.Should().Contain(static x => x.Table == "mt_doc_healthorderdoc" && x.Kind == MartenTableKind.Document);
        set.Tables.Should().NotContain(static x => x.Table.Contains("healthintdoc", StringComparison.Ordinal), "a hidden type's table is not even a parameter");
        set.Tables.Should().Contain(static x => x.Schema == "studio_set_events" && x.Table == "mt_events" && x.Kind == MartenTableKind.EventStore);
        set.Tables.Should().Contain(static x => x.Schema == "studio_set" && x.Table == "mt_hilo" && x.Kind == MartenTableKind.Infrastructure);
        set.Targets.Should().HaveCount(set.Tables.Count);
        set.StructuralIndexes.Should().Contain(static x => x.EndsWith("_idx_mt_deleted", StringComparison.Ordinal),
            "a soft-deleted type's mt_deleted index is Marten's own");

        set.Tables.Single(static x => x.Table == "mt_doc_healthorderdoc").SoftDeleted.Should().BeTrue();
        HiloHeadroomCheck.IntSequences(context).Should().BeEmpty("the only int-id type is hidden");
    }

    /// <summary>
    /// XSO: an <c>ExtendedSchemaObjects</c> table in a schema of its own is in the set, by the schema and name
    /// its declaration carries. The set used to split each key back against <c>SchemaDeclarations.Schemas</c>,
    /// which leaves that schema out on purpose (D27), so the table was skipped and every T-check ignored it.
    /// One whose name holds a dot, beside a schema whose name is a prefix of another, is taken as declared too.
    /// </summary>
    [Fact]
    public void An_extended_table_in_a_schema_of_its_own_is_in_the_set_by_the_pair_its_declaration_carries()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_set";
            options.Events.DatabaseSchemaName = "studio_set_events";
            options.Schema.For<HealthOrderDoc>();
            options.Storage.ExtendedSchemaObjects.Add(new Weasel.Postgresql.Tables.Table(
                new Weasel.Postgresql.PostgresqlObjectName("xso_own", "xso_things", Weasel.Postgresql.SchemaUtils.IdentifierUsage.General)));
            options.Storage.ExtendedSchemaObjects.Add(new Weasel.Postgresql.Tables.Table(
                new Weasel.Postgresql.PostgresqlObjectName("studio_set_events.x", "y", Weasel.Postgresql.SchemaUtils.IdentifierUsage.General)));
        });

        MartenTableSet set = MartenTables.Read(Context(store, services));

        MartenTable extended = set.Tables.Should().ContainSingle(static x => x.Table == "xso_things").Subject;
        extended.Schema.Should().Be("xso_own");
        extended.Kind.Should().Be(MartenTableKind.Managed);
        extended.DocumentTypes.Should().BeEmpty("a finding about it names no document type");
        extended.OutsideOwnSchemas.Should().BeTrue();
        extended.GatedSchemas.Should().Equal(["xso_own"], "a finding about it is shown only past the database browser's gate for that schema (D27)");
        extended.NoteName.Should().NotContain("xso", "a status row's note names neither the table nor its schema");
        set.Tables.Where(static x => x.Table != "xso_things" && x.Table != "y").Should().OnlyContain(static x => x.GatedSchemas.Count == 0,
            "every other table is in the store's own schemas, and its findings read no gate they did not read before");
        set.Targets.Should().Contain(new HealthTableTarget("xso_own", "xso_things"), "the statements bind its own schema, as a parameter");
        set.Find("xso_own", "xso_things").Should().BeSameAs(extended, "a row the catalog returns for it is matched back to it");

        set.Tables.Should().ContainSingle(static x => x.Schema == "studio_set_events.x" && x.Table == "y",
            "split against the store's schemas, this key would have read as studio_set_events's table x.y");

        set.Declarations.Schemas.Should().NotContain("xso_own").And.NotContain("studio_set_events.x",
            "the set reads the pair; the store's own schemas are unchanged (D27)");
    }

    private static HealthCheckContext Context(DocumentStore store, ServiceProvider services) =>
        new(
            new ResolvedScope(
                new StudioScope("default", string.Empty, null),
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                store,
                store.Storage.Database),
            MartenStudioSettings.Of(new MartenStudioOptions()),
            MartenStudioRuntime.Current,
            services,
            new HealthSqlRunner(new HealthConnection(MartenStudioDatabase.Of(store.Storage.Database)), new ReadOnlySqlOptions(), null),
            Now,
            new HealthSweepMemo(),
            CancellationToken.None);

    /// <summary>The registry finds all eleven, and every one is well formed.</summary>
    [Fact]
    public void The_studio_ships_T01_to_T12_and_the_statement_checks_T22_and_T23()
    {
        new HealthCheckRegistry().All.Select(static x => x.Info.Code)
            .Should().Contain(["T01", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10", "T11", "T12"]);

        new HealthCheckRegistry().Find("T09")!.Info.Tier.Should().Be(HealthCostTier.BoundedData);

        // H7's statement checks: tier 3, behind the activity gate, as the registry requires of a statement check.
        foreach (string code in (string[]) ["T02", "T22", "T23"])
        {
            HealthCheckInfo info = new HealthCheckRegistry().Find(code)!.Info;
            info.Tier.Should().Be(HealthCostTier.Statements, code);
            info.Gate.Should().Be(HealthGate.Activity, code);
        }
    }
}
