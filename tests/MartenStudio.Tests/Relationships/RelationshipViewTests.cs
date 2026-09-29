using MartenStudio.Services;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Support;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The Relationships screen's three views, its schema chips and its node cap, and the object detail
/// page's neighbourhood - all pure functions of one graph.
/// </summary>
public class RelationshipViewTests
{
    private static readonly string CreditKey = FakeRelationshipDataService.TableKey("legacy", "customer_credit");
    private static readonly string TriggersKey = FakeRelationshipDataService.TableKey("quartz", "qrtz_triggers");

    [Fact]
    public void A_graph_with_no_table_in_any_key_is_drawn_exactly_as_before()
    {
        GraphModel graph = FakeRelationshipDataService.Sample();

        RelationshipProjection projection = RelationshipViews.Project(graph, RelationshipViewMode.Tables, "quartz");

        projection.Drawn.Nodes.Should().Equal(graph.Nodes, "every document type is drawn whatever the URL says");
        projection.Drawn.Edges.Should().Equal(graph.Edges);
        projection.Schemas.Should().BeEmpty();
        projection.OverCap.Should().BeFalse("a picture of document types alone is never capped, as it never was");
    }

    [Fact]
    public void Both_draws_every_document_type_and_every_table_that_takes_part_in_a_key()
    {
        GraphModel graph = FakeRelationshipDataService.WithTables();

        RelationshipProjection projection = RelationshipViews.Project(graph, RelationshipViewMode.Both, null);

        projection.Drawn.Nodes.Select(static x => x.Alias).Should().Equal(["customer", "order"]);
        projection.Drawn.Tables.Select(static x => x.Name).Should().BeEquivalentTo(
            ["customer_credit", "purchase_order_lines", "purchase_orders", "qrtz_job_details", "qrtz_simple_triggers", "qrtz_triggers"],
            "audit_log takes part in no key and is only listed");
        projection.Drawn.Edges.Should().HaveCount(5);

        projection.DocumentEdges.Should().Be(1);
        projection.TableEdges.Should().Be(4);
        projection.AllEdges.Should().Be(5);
    }

    [Fact]
    public void Documents_draws_the_document_types_and_the_keys_between_them_alone()
    {
        RelationshipProjection projection = RelationshipViews.Project(
            FakeRelationshipDataService.WithTables(), RelationshipViewMode.Documents, null);

        projection.Drawn.Tables.Should().BeEmpty();
        projection.Drawn.Edges.Should().ContainSingle().Which.ToAlias.Should().Be("customer");
        projection.Isolated.Should().BeEmpty();
    }

    [Fact]
    public void Tables_draws_every_key_with_a_table_end_and_the_document_types_those_keys_reach()
    {
        RelationshipProjection projection = RelationshipViews.Project(
            FakeRelationshipDataService.WithTables(), RelationshipViewMode.Tables, null);

        projection.Drawn.Edges.Should().HaveCount(4).And.OnlyContain(static x => x.IsTableEdge);
        projection.Drawn.Nodes.Select(static x => x.Alias).Should().Equal(["customer"],
            "customer_credit points at customer; order is reached by no key with a table end");
        projection.Isolated.Select(static x => x.Name).Should().Equal(["audit_log"]);
    }

    [Fact]
    public void A_schema_chip_filters_the_table_nodes_and_leaves_the_document_types_alone()
    {
        GraphModel graph = FakeRelationshipDataService.WithTables();

        RelationshipProjection quartz = RelationshipViews.Project(graph, RelationshipViewMode.Both, "quartz");

        quartz.Drawn.Tables.Should().OnlyContain(static x => x.Schema == "quartz");
        quartz.Drawn.Nodes.Select(static x => x.Alias).Should().Equal(["customer", "order"]);
        quartz.Drawn.Edges.Should().HaveCount(3, "order → customer, and the two Quartz keys");
        quartz.TableEdges.Should().Be(2);

        quartz.Schemas.Select(static x => (x.Schema, x.Tables)).Should().Equal(
            [("legacy", 3), ("quartz", 3)], "the chips count every schema, whichever one is picked");
    }

    [Fact]
    public void Past_the_cap_a_picture_with_tables_is_not_drawn()
    {
        GraphModel graph = Chain(RelationshipViews.NodeCap + 5);

        RelationshipProjection projection = RelationshipViews.Project(graph, RelationshipViewMode.Both, null);

        projection.OverCap.Should().BeTrue();
        projection.DrawnNodes.Should().Be(RelationshipViews.NodeCap + 5);
        projection.Drawn.Edges.Should().HaveCount(RelationshipViews.NodeCap + 4, "the table still lists every edge");

        RelationshipViews.Project(graph, RelationshipViewMode.Documents, null).OverCap.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "Both")]
    [InlineData("", "Both")]
    [InlineData("both", "Both")]
    [InlineData("Tables", "Tables")]
    [InlineData("documents", "Documents")]
    [InlineData("nonsense", "Both")]
    public void A_view_token_parses_to_its_view_and_anything_else_is_the_default(string? token, string expected)
    {
        RelationshipViews.Parse(token).ToString().Should().Be(expected);
    }

    [Fact]
    public void The_default_view_is_left_out_of_the_url()
    {
        RelationshipViews.Token(RelationshipViewMode.Both).Should().BeNull();
        MartenStudio.Services.Database.DatabaseLinks.ToRelationships(new MartenStudioOptions(), new StudioScope("default", "db1", null), RelationshipViewMode.Tables, "quartz")
            .Should().Be("relationships?store=default&db=db1&view=tables&schema=quartz");
    }

    [Fact]
    public void The_legend_names_only_the_styles_drawn_in_a_fixed_order()
    {
        GraphModel graph = FakeRelationshipDataService.WithTables();

        RelationshipViews.Styles(graph.Edges).Should().Equal(["both", "table", "notvalid"]);
        RelationshipViews.Styles(FakeRelationshipDataService.Sample().Edges).Should().Equal(["both", "declared", "physical"]);
        RelationshipViews.Styles([]).Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------------------
    // The neighbourhood
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_tables_neighbourhood_is_the_table_every_node_one_key_away_and_those_keys()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(
            FakeRelationshipDataService.WithTables(), "quartz", "qrtz_triggers");

        neighbourhood.State.Should().Be(NeighbourhoodState.Loaded);
        neighbourhood.Centre.Should().Be(TriggersKey);
        neighbourhood.Graph.Edges.Should().HaveCount(2);
        neighbourhood.Graph.Tables.Select(static x => x.Name).Should().BeEquivalentTo(
            ["qrtz_job_details", "qrtz_simple_triggers", "qrtz_triggers"]);
        neighbourhood.Graph.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void A_document_tables_neighbourhood_is_found_by_its_table_name_and_counts_its_withheld_keys()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(
            FakeRelationshipDataService.WithTables(), "studio_sample", "mt_doc_customer");

        neighbourhood.State.Should().Be(NeighbourhoodState.Loaded);
        neighbourhood.Centre.Should().Be("customer");
        neighbourhood.Graph.Edges.Select(static x => x.FromAlias).Should().BeEquivalentTo(["order", CreditKey]);
        neighbourhood.Withheld.Should().Be(1, "one key reaches customer from a schema the visitor is not shown");
    }

    [Fact]
    public void A_table_with_no_key_has_an_empty_neighbourhood()
    {
        RelationshipViews.Neighbourhood(FakeRelationshipDataService.WithTables(), "legacy", "audit_log")
            .State.Should().Be(NeighbourhoodState.Empty);

        RelationshipViews.Neighbourhood(FakeRelationshipDataService.WithTables(), "legacy", "no_such_table")
            .State.Should().Be(NeighbourhoodState.Empty);
    }

    /// <summary>
    /// The integration review of 74c9bb0: a table whose only key points at an <c>mt_doc_</c> table no store
    /// registers said "No foreign keys" on its Relationships tab while its Keys tab listed the key.
    /// </summary>
    [Fact]
    public void A_table_whose_only_key_cannot_be_drawn_keeps_that_key_in_its_neighbourhood()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(WithUnmatched(), "legacy", "audit_log");

        neighbourhood.State.Should().Be(NeighbourhoodState.Loaded);
        neighbourhood.Graph.Edges.Should().BeEmpty();
        neighbourhood.Graph.Nodes.Should().BeEmpty();
        neighbourhood.Graph.Tables.Should().BeEmpty("there is nothing to draw, so nothing is drawn");
        neighbourhood.Graph.Unmatched.Should().ContainSingle().Which.Name.Should().Be("audit_log_ref_fkey");
    }

    [Fact]
    public void The_far_end_of_an_unmatched_key_finds_it_too_though_it_is_no_node()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(WithUnmatched(), "studio_sample", "mt_doc_discovered");

        neighbourhood.State.Should().Be(NeighbourhoodState.Loaded);
        neighbourhood.Centre.Should().BeNull("an unregistered document table is no node of the picture");
        neighbourhood.Graph.Unmatched.Select(static x => x.Name).Should().Equal("audit_log_ref_fkey");
    }

    [Fact]
    public void A_neighbourhood_with_edges_keeps_its_own_unmatched_rows_and_nobody_elses()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(WithUnmatched(), "quartz", "qrtz_triggers");

        neighbourhood.State.Should().Be(NeighbourhoodState.Loaded);
        neighbourhood.Graph.Edges.Should().HaveCount(2);
        neighbourhood.Graph.Unmatched.Select(static x => x.Name).Should().Equal("qrtz_triggers_owner_fkey");
    }

    [Fact]
    public void A_name_that_differs_only_by_case_is_another_table_with_no_keys()
    {
        RelationshipViews.Neighbourhood(WithUnmatched(), "legacy", "Audit_Log").State.Should().Be(NeighbourhoodState.Empty);
    }

    [Fact]
    public void An_object_in_a_schema_the_visitor_is_not_shown_is_refused_without_saying_whether_it_exists()
    {
        RelationshipNeighbourhood neighbourhood = RelationshipViews.Neighbourhood(
            FakeRelationshipDataService.WithTables(), "secrets", "vault");

        neighbourhood.State.Should().Be(NeighbourhoodState.Refused);
        neighbourhood.Reason.Should().Contain("not one of the schemas this studio shows you");
        neighbourhood.Graph.Edges.Should().BeEmpty();
    }

    /// <summary>
    /// <see cref="FakeRelationshipDataService.WithTables" /> with two keys that cannot be drawn: the only key
    /// of <c>legacy.audit_log</c>, into a document table no store registers, and one of
    /// <c>quartz.qrtz_triggers</c>' besides its two drawn ones, into another store's collection.
    /// </summary>
    internal static GraphModel WithUnmatched() => FakeRelationshipDataService.WithTables() with
    {
        Unmatched =
        [
            new UnmatchedForeignKey(
                "audit_log_ref_fkey", "legacy.audit_log", "ref", "studio_sample.mt_doc_discovered", false, true,
                "It points at a table no document type of this store maps.")
            {
                FromSchema = "legacy",
                FromTable = "audit_log",
                ToSchema = "studio_sample",
                ToTable = "mt_doc_discovered",
            },
            new UnmatchedForeignKey(
                "qrtz_triggers_owner_fkey", "quartz.qrtz_triggers", "owner_id", "studio_sample.mt_doc_billing_owner", false, true,
                "It points at a table no document type of this store maps.")
            {
                FromSchema = "quartz",
                FromTable = "qrtz_triggers",
                ToSchema = "studio_sample",
                ToTable = "mt_doc_billing_owner",
            },
        ],
    };

    /// <summary>
    /// A chain of document → table → table → … keys, long enough to pass the cap.
    /// </summary>
    private static GraphModel Chain(int nodes)
    {
        List<RelationshipTableNode> tables = [];
        List<RelationshipEdge> edges = [];

        for (int i = 0; i < nodes - 1; i++)
        {
            tables.Add(FakeRelationshipDataService.Table("wide", "t" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)));
        }

        edges.Add(new RelationshipEdge(tables[0].Key, "customer", "customer_id", null, false, true, "NoAction", "fk_root")
        {
            FromIsTable = true,
        });

        for (int i = 1; i < tables.Count; i++)
        {
            edges.Add(new RelationshipEdge(tables[i].Key, tables[i - 1].Key, "parent_id", null, false, true, "NoAction", "fk_" + i)
            {
                FromIsTable = true,
                ToIsTable = true,
            });
        }

        return new GraphModel(
            [FakeRelationshipDataService.WithTables().Nodes[0]],
            edges,
            [],
            DateTimeOffset.UnixEpoch)
        {
            Tables = tables,
        };
    }
}
