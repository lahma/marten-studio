using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Links;
using MartenStudio.Tests.Services;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Links;

/// <summary>A document type at namespace level, so that its alias is its plain lower-cased name.</summary>
public sealed class Order
{
    public Guid Id { get; set; }
}

/// <summary>A document type a test hides.</summary>
public sealed class Secret
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>A document type with a string id.</summary>
public sealed class Invoice
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>An event, so that a store keeps an event store.</summary>
public sealed class OrderPlaced;

/// <summary>
/// D-CORR 7.2: a link from a module's page to a document or a stream, by type name and id, built from <c>StoreOptions</c> alone -
/// no connection, no policy - with a store and a tenant and never a database.
/// </summary>
public class MartenStudioRouteLinksTests
{
    /// <summary>Never connected to: the links read configuration only.</summary>
    private const string Connection = "Host=route-links.invalid;Database=none;Username=none;Password=none";

    private const string SecondConnection = "Host=route-links-b.invalid;Database=none;Username=none;Password=none";

    public interface IBillingStore : IDocumentStore;

    public interface IShippingStore : IDocumentStore;

    public static class One
    {
        public sealed class Twin
        {
            public Guid Id { get; set; }
        }
    }

    public static class Other
    {
        public sealed class Twin
        {
            public Guid Id { get; set; }
        }
    }

    [Fact]
    public void A_document_is_found_by_its_alias_its_full_name_and_its_name()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>();
        }));

        foreach (string name in (string[])["order", "ORDER", typeof(Order).FullName!, typeof(Order).FullName!.ToUpperInvariant(), "Order"])
        {
            string? url = links.ToDocument(new StudioLinkScope(null, null), name, "abc");

            url.Should().NotBeNull("'{0}' names the collection", name);
            Query(url!).Should().Contain(new KeyValuePair<string, string?>("id", "abc"));
            url.Should().Contain("/order/", "it is the alias that goes in the route");
        }
    }

    [Fact]
    public void An_unknown_name_is_null()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>();
        }));

        links.ToDocument(new StudioLinkScope(null, null), "nothing", "abc").Should().BeNull();
        links.ToDocument(new StudioLinkScope(null, null), "", "abc").Should().BeNull();
        links.ToDocument(new StudioLinkScope(null, null), "order", "").Should().BeNull();
    }

    [Fact]
    public void A_name_two_types_of_one_store_share_is_ambiguous()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<One.Twin>();
            x.Schema.For<Other.Twin>();
        }));

        links.ToDocument(new StudioLinkScope(null, null), "Twin", "1").Should().BeNull("which of the two it is would be a guess");
        links.ToDocument(new StudioLinkScope(null, null), typeof(One.Twin).FullName!, "1").Should().NotBeNull("the full name tells them apart");
    }

    [Fact]
    public void A_hidden_document_type_is_null_exactly_as_an_unknown_one_is()
    {
        MartenStudioRouteLinks links = Links(
            static services => services.AddMarten(x =>
            {
                x.Connection(Connection);
                x.Schema.For<Order>();
                x.Schema.For<Secret>();
            }),
            static options => options.IsDocumentTypeVisible = static type => type != typeof(Secret));

        links.ToDocument(new StudioLinkScope(null, null), "secret", "1").Should().BeNull();
        links.ToDocument(new StudioLinkScope(null, null), typeof(Secret).FullName!, "1").Should().BeNull();
        links.ToDocument(new StudioLinkScope(null, null), "order", "1").Should().NotBeNull();
    }

    [Fact]
    public void Two_stores_one_of_which_the_scope_names_is_that_one()
    {
        MartenStudioRouteLinks links = TwoStoresDeclaringOrderOnly(declaredByDefault: false);

        string? url = links.ToDocument(new StudioLinkScope(nameof(IShippingStore), null), "order", "7");

        Query(url!).Should().Contain(new KeyValuePair<string, string?>("store", nameof(IShippingStore)));
    }

    [Fact]
    public void Two_stores_with_the_scope_naming_neither_the_default_store_wins()
    {
        MartenStudioRouteLinks links = TwoStoresDeclaringOrderOnly(declaredByDefault: true);

        string? url = links.ToDocument(new StudioLinkScope("elsewhere", null), "order", "7");

        Query(url!).Should().Contain(new KeyValuePair<string, string?>("store", "default"));
    }

    [Fact]
    public void Two_stores_neither_of_which_the_scope_or_the_default_names_is_ambiguous()
    {
        MartenStudioRouteLinks links = TwoStoresDeclaringOrderOnly(declaredByDefault: false);

        links.ToDocument(new StudioLinkScope(null, null), "order", "7").Should().BeNull();
        links.ToDocument(new StudioLinkScope("elsewhere", null), "order", "7").Should().BeNull();
    }

    [Fact]
    public void One_store_that_declares_the_type_is_the_store_even_when_the_scope_names_another()
    {
        MartenStudioRouteLinks links = Links(static services =>
        {
            services.AddMarten(x => x.Connection(Connection));
            services.AddMartenStore<IBillingStore>(x =>
            {
                x.Connection(SecondConnection);
                x.Schema.For<Invoice>();
            });
        });

        string? url = links.ToDocument(new StudioLinkScope("default", null), "invoice", "9");

        Query(url!).Should().Contain(new KeyValuePair<string, string?>("store", nameof(IBillingStore)));
    }

    [Fact]
    public void A_store_with_tenants_and_no_tenant_given_is_null()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>().MultiTenanted();
            x.Events.AddEventType<OrderPlaced>();
            x.Events.TenancyStyle = TenancyStyle.Conjoined;
        }));

        links.ToDocument(new StudioLinkScope(null, null), "order", "7").Should().BeNull("the page would open the visitor's tenant and say 'not found'");
        links.ToDocument(new StudioLinkScope(null, " "), "order", "7").Should().BeNull();
        links.ToStream(new StudioLinkScope(null, null), Guid.NewGuid().ToString()).Should().BeNull();
    }

    [Fact]
    public void The_tenant_is_carried_and_a_database_never_is()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>().MultiTenanted();
            x.Events.AddEventType<OrderPlaced>();
            x.Events.TenancyStyle = TenancyStyle.Conjoined;
        }));

        string document = links.ToDocument(new StudioLinkScope("default", "acme"), "order", "7")!;
        string stream = links.ToStream(new StudioLinkScope("default", "acme"), "6f1f6f0e-0b8c-4f44-8ec5-0c7a5e9d9d0e")!;

        Query(document).Should().Contain(new KeyValuePair<string, string?>("tenant", "acme"));
        Query(stream).Should().Contain(new KeyValuePair<string, string?>("tenant", "acme"));
        Query(document).Keys.Should().NotContain("db");
        Query(stream).Keys.Should().NotContain("db");
    }

    [Fact]
    public void A_store_without_tenants_ignores_the_tenant_given()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>();
            x.Events.AddEventType<OrderPlaced>();
        }));

        Query(links.ToDocument(new StudioLinkScope(null, "acme"), "order", "7")!).Keys.Should().NotContain("tenant");
        Query(links.ToStream(new StudioLinkScope(null, "acme"), Guid.NewGuid().ToString())!).Keys.Should().NotContain("tenant");
    }

    public static TheoryData<string> DatabasePerTenantKinds => new()
    {
        "master table",
        "sharded",
        "one server",
        "static",
        "custom",
        "custom that throws",
    };

    /// <summary>
    /// The page a link opens takes the database from the address or the visitor's current one and cannot find a tenant's database
    /// without enumerating the tenancy, so a store that keeps each tenant in a database of its own gets no link: with a tenant, without
    /// one, for a document and for a stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(DatabasePerTenantKinds))]
    public void A_store_with_a_database_per_tenant_gets_no_link(string kind)
    {
        MartenStudioRouteLinks links = Links(services => services.AddMarten(x =>
        {
            x.Schema.For<Order>();
            x.Events.AddEventType<OrderPlaced>();
            DatabasePerTenant(x, kind);
        }));

        string stream = Guid.NewGuid().ToString();

        links.ToDocument(new StudioLinkScope(null, "globex"), "order", "7").Should().BeNull(kind);
        links.ToDocument(new StudioLinkScope(null, null), "order", "7").Should().BeNull(kind);
        links.ToStream(new StudioLinkScope(null, "globex"), stream).Should().BeNull(kind);
        links.ToStream(new StudioLinkScope(null, null), stream).Should().BeNull(kind);
    }

    [Fact]
    public void A_database_per_tenant_store_does_not_hide_another_stores_link()
    {
        MartenStudioRouteLinks links = Links(static services =>
        {
            services.AddMarten(x =>
            {
                x.Schema.For<Order>();
                x.MultiTenantedDatabases(t => t.AddMultipleTenantDatabase(Connection, "a").ForTenants("acme"));
            });
            services.AddMartenStore<IBillingStore>(x =>
            {
                x.Connection(SecondConnection);
                x.Schema.For<Invoice>();
            });
        });

        links.ToDocument(new StudioLinkScope(null, "acme"), "order", "7").Should().BeNull();
        links.ToDocument(new StudioLinkScope(null, null), "invoice", "7").Should().NotBeNull();
    }

    [Fact]
    public void A_conjoined_store_still_gets_a_link_with_its_tenant_and_without_a_database()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>().MultiTenanted();
            x.Events.AddEventType<OrderPlaced>();
            x.Events.TenancyStyle = TenancyStyle.Conjoined;
        }));

        string document = links.ToDocument(new StudioLinkScope(null, "globex"), "order", "7")!;
        string stream = links.ToStream(new StudioLinkScope(null, "globex"), Guid.NewGuid().ToString())!;

        foreach (string url in (string[])[document, stream])
        {
            Query(url).Should().Contain(new KeyValuePair<string, string?>("tenant", "globex"));
            Query(url).Keys.Should().NotContain("db");
        }
    }

    private static void DatabasePerTenant(StoreOptions options, string kind)
    {
        switch (kind)
        {
            case "master table":
                options.MultiTenantedDatabasesWithMasterDatabaseTable(Connection, "tenants");
                break;
            case "sharded":
                options.MultiTenantedWithShardedDatabases(t => t.ConnectionString = Connection);
                break;
            case "one server":
                options.MultiTenantedWithSingleServer(Connection);
                break;
            case "static":
                options.MultiTenantedDatabases(t =>
                {
                    t.AddMultipleTenantDatabase(Connection, "a").ForTenants("acme");
                    t.AddMultipleTenantDatabase(SecondConnection, "b").ForTenants("globex");
                });
                break;
            default:
                options.MultiTenantedDatabases(t => t.AddMultipleTenantDatabase(Connection, "a").ForTenants("acme"));
                options.Tenancy = (ITenancy)(object)StoreTenancyTests.CustomTenancy.Over(options.Tenancy, throwsCardinality: kind == "custom that throws");
                break;
        }
    }

    [Fact]
    public void A_stream_needs_events_and_a_guid_identity_reads_only_a_guid()
    {
        MartenStudioRouteLinks noEvents = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Order>();
        }));

        noEvents.ToStream(new StudioLinkScope(null, null), Guid.NewGuid().ToString()).Should().BeNull("the store keeps no events");

        MartenStudioRouteLinks guids = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Events.AddEventType<OrderPlaced>();
        }));

        guids.ToStream(new StudioLinkScope(null, null), "order-1").Should().BeNull("a string is not a Guid stream's id");
        guids.ToStream(new StudioLinkScope(null, null), "").Should().BeNull();

        Guid id = Guid.NewGuid();
        Query(guids.ToStream(new StudioLinkScope(null, null), id.ToString("N"))!)
            .Should().Contain(new KeyValuePair<string, string?>("id", id.ToString("D")), "the id is written in the canonical form");
    }

    [Fact]
    public void A_string_identity_reads_one_to_two_hundred_and_fifty_characters()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Events.AddEventType<OrderPlaced>();
            x.Events.StreamIdentity = StreamIdentity.AsString;
        }));

        links.ToStream(new StudioLinkScope(null, null), "order-1").Should().NotBeNull();
        links.ToStream(new StudioLinkScope(null, null), new string('x', 250)).Should().NotBeNull();
        links.ToStream(new StudioLinkScope(null, null), new string('x', 251)).Should().BeNull();
    }

    [Fact]
    public void Reserved_characters_in_an_id_are_escaped_and_read_back_whole()
    {
        MartenStudioRouteLinks links = Links(static services => services.AddMarten(x =>
        {
            x.Connection(Connection);
            x.Schema.For<Invoice>();
            x.Events.AddEventType<OrderPlaced>();
            x.Events.StreamIdentity = StreamIdentity.AsString;
        }));

        const string id = "a/b#c?d%e&f=g h";

        string document = links.ToDocument(new StudioLinkScope(null, null), "invoice", id)!;
        string stream = links.ToStream(new StudioLinkScope(null, null), id)!;

        foreach (string url in (string[])[document, stream])
        {
            url.Should().NotContain("#", "a fragment would cut the id");
            url.Split('?')[0].Should().NotContain("%", "the path holds no id");
            url.Should().Contain("%2F").And.Contain("%23").And.Contain("%3F").And.Contain("%25").And.Contain("%26");
            Query(url).Should().Contain(new KeyValuePair<string, string?>("id", id));
        }
    }

    [Fact]
    public void A_store_that_will_not_build_is_skipped()
    {
        MartenStudioRouteLinks links = Links(static services =>
        {
            services.AddMarten(x =>
            {
                x.Connection(Connection);
                x.Schema.For<Order>();
            });
            services.AddSingleton<IShippingStore>(static _ => throw new InvalidOperationException("will not build"));
        });

        Query(links.ToDocument(new StudioLinkScope(null, null), "order", "7")!).Should().Contain(new KeyValuePair<string, string?>("store", "default"));
    }

    private static MartenStudioRouteLinks TwoStoresDeclaringOrderOnly(bool declaredByDefault) =>
        Links(services =>
        {
            services.AddMarten(x =>
            {
                x.Connection(Connection);
                if (declaredByDefault)
                {
                    x.Schema.For<Order>();
                }
            });
            services.AddMartenStore<IBillingStore>(x =>
            {
                x.Connection(SecondConnection);
                x.Schema.For<Order>();
            });
            services.AddMartenStore<IShippingStore>(x =>
            {
                x.Connection("Host=route-links-c.invalid;Database=none;Username=none;Password=none");
                x.Schema.For<Order>();
            });
        });

    private static MartenStudioRouteLinks Links(Action<IServiceCollection> register, Action<MartenStudioOptions>? configure = null)
    {
        ServiceCollection services = new();
        register(services);

        MartenStudioOptions options = new();
        configure?.Invoke(options);

        ServiceProvider provider = services.BuildServiceProvider();
        return new MartenStudioRouteLinks(Microsoft.Extensions.Options.Options.Create(options), new MartenStoreRegistry(services), provider);
    }

    /// <summary>The query string of a studio-relative link, decoded.</summary>
    private static Dictionary<string, string?> Query(string url)
    {
        int mark = url.IndexOf('?', StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(mark < 0 ? string.Empty : url[mark..])
            .ToDictionary(static x => x.Key, static x => (string?)x.Value.ToString(), StringComparer.Ordinal);
    }
}
