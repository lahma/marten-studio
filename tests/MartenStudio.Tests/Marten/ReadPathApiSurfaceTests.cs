using System.Data.Common;

using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Schema;

using MartenStudio.Tests.Events;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// The Marten members DL (0.4 packet: no read path reaches <c>EnsureStorageExists</c>) depends on, pinned the way
/// <c>MartenApiSurfaceTest</c> pins the rest - a compile-only probe for the signatures, reflection for the facts - in a
/// file of its own.
/// </summary>
/// <remarks>
/// <para>
/// Read on 2026-10-17 against Marten <c>V9.39.0</c> (JasperFx 2.74.0, Weasel 9.33.0) and <c>V9.50.2</c> (JasperFx
/// 2.87.1, Weasel 9.45.0). <c>QueryEventStore.cs</c>, <c>EventStatement.cs</c> and <c>EventDocumentStorage.cs</c> are
/// byte for byte the same at both, and so is <c>DeadLetterEvent</c> between the two JasperFx releases.
/// </para>
/// <para>
/// <b>What the replay relies on.</b> <c>IMartenSession</c> is public, in <c>Marten.Internal</c>, and its
/// <c>EventStorage()</c> is the storage <c>FetchStreamAsync</c> resolves rows through - without the ensure in front of
/// it. <c>IEventStorage</c> is an <c>ISelector&lt;IEvent&gt;</c> - Weasel's, in <c>Weasel.Storage</c> since #4821 - whose
/// <c>SelectFields()</c> is the column list its <c>ResolveAsync</c> reads by ordinal. The aggregator is JasperFx's
/// <c>ProjectionGraph.AggregatorFor&lt;T&gt;()</c>, reached as Marten's own <c>AggregateToExtensions</c> reaches it, through
/// <c>IMartenSession.Options</c>; the identity setter is Weasel's <c>IDocumentStorage&lt;T&gt;</c>, from
/// <c>IStorageSession.StorageFor&lt;T&gt;()</c>, which refuses an aggregate with no id member with
/// <c>InvalidDocumentException</c> - the exception <c>QuerySession.TryGetStorageForLiveAggregation</c> catches.
/// </para>
/// <para>
/// <b>What the dead-letter list relies on.</b> <c>StoreOptions.ApplyConfiguration</c> registers
/// <c>Schema.For&lt;DeadLetterEvent&gt;().Identity(x =&gt; x.Id).DatabaseSchemaName(Events.DatabaseSchemaName).SingleTenanted()</c>
/// at both tags, and <c>StorageFeatures.BuildAllMappings</c> materialises it only for an active event store (#4303).
/// </para>
/// </remarks>
public class ReadPathApiSurfaceTests
{
    [Fact]
    public void A_query_sessions_event_storage_is_a_public_selector_of_events()
    {
        using DocumentStore store = Store(static options => options.Events.AddEventType<TimeTravelOrderPlaced>());
        using IQuerySession session = store.QuerySession();

        typeof(IMartenSession).IsPublic.Should().BeTrue();
        session.Should().BeAssignableTo<IMartenSession>();

        IEventStorage storage = ((IMartenSession) session).EventStorage();

        storage.Should().BeAssignableTo<Weasel.Storage.ISelector<IEvent>>();
        storage.SelectFields().Should().NotBeEmpty();
    }

    [Fact]
    public void An_aggregate_with_no_identity_is_refused_with_the_exception_Marten_catches()
    {
        using DocumentStore store = Store(static _ => { });
        using IQuerySession session = store.QuerySession();

        Action storage = () => ((IMartenSession) session).StorageFor<NoIdentity>();

        storage.Should().Throw<InvalidDocumentException>();
    }

    [Fact]
    public void Dead_letters_are_registered_single_tenanted_in_the_event_schema_once_the_event_store_is_active()
    {
        using DocumentStore active = Store(static options => options.Events.AddEventType<TimeTravelOrderPlaced>());
        using DocumentStore inactive = Store(static _ => { });

        IDocumentType mapping = ((IReadOnlyStoreOptions) active.Options).AllKnownDocumentTypes()
            .Single(static x => x.DocumentType == typeof(DeadLetterEvent));

        mapping.TableName.Schema.Should().Be("studio_rp_events");
        mapping.TableName.Name.Should().Be("mt_doc_deadletterevent");
        mapping.TenancyStyle.Should().Be(TenancyStyle.Single);
        mapping.IdType.Should().Be<Guid>();

        ((IReadOnlyStoreOptions) inactive.Options).AllKnownDocumentTypes()
            .Should().NotContain(static x => x.DocumentType == typeof(DeadLetterEvent));
    }

    [Fact]
    public void Skipped_events_are_a_read_only_event_option()
    {
        typeof(IReadOnlyEventStoreOptions).GetProperty(nameof(IReadOnlyEventStoreOptions.EnableEventSkippingInProjectionsOrSubscriptions))
            .Should().NotBeNull();
    }

    /// <summary>Never executed: the compiler checks every signature the read paths call.</summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c>, so that nothing has to call it for the analyzers to accept it - a test
    /// whose only job was to reference it could never fail, and would read as a test of something.
    /// </remarks>
    internal static async Task CompileOnlyAsync(IQuerySession session, DbDataReader reader, CancellationToken token)
    {
        IMartenSession marten = (IMartenSession) session;

        IEventStorage storage = marten.EventStorage();
        string[] fields = storage.SelectFields();
        Weasel.Storage.ISelector<IEvent> selector = storage;
        IEvent resolved = await selector.ResolveAsync(reader, token);

        TimeTravelOrder? aggregate = await marten.Options.Projections
            .AggregatorFor<TimeTravelOrder>()
            .BuildAsync([resolved], session, null, token);

        Weasel.Storage.IDocumentStorage<TimeTravelOrder> identity = marten.StorageFor<TimeTravelOrder>();
        identity.SetIdentityFromGuid(aggregate!, Guid.NewGuid());
        identity.SetIdentityFromString(aggregate!, fields[0]);

        DeadLetterEvent letter = await session.DocumentStore.Options.Serializer().FromJsonAsync<DeadLetterEvent>(reader, 1, token);

        string tenant = session.TenantId;
        bool skipping = session.DocumentStore.Options.Events.EnableEventSkippingInProjectionsOrSubscriptions;

        _ = (letter.TenantId, letter.ProjectionName, letter.ShardName, letter.ExceptionType, letter.Timestamp, tenant, skipping);
    }

    private static DocumentStore Store(Action<StoreOptions> configure) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_rp";
            options.Events.DatabaseSchemaName = "studio_rp_events";
            configure(options);
        });

    /// <summary>A type Marten cannot find an id on.</summary>
    public sealed class NoIdentity
    {
        public string Name { get; set; } = string.Empty;
    }
}
