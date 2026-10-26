using System.Reflection;

using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using IJasperFxEventStore = JasperFx.Events.IEventStore;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// The Marten 9.52 light-ups (JasperFx.Events 2.92-2.93): projection start floors, systemic database failures in
/// projection code, and the high-water advance that ensures the event storage and moves per-tenant marks.
/// </summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// Projection start floors, both halves: JasperFx.Events 2.93.0's <c>AsyncOptions.SubscribeFromFirstRun(string?)</c>
    /// (jasperfx#809/#1068, <c>src/JasperFx.Events/Projections/AsyncOptions.cs</c>; absent at v2.92.0), whose daemon
    /// replays a rebuild from <c>AsyncOptions.DetermineRebuildFloorAsync</c> - a stored floor, else a configured
    /// <c>SubscribeFromSequence</c> / <c>SubscribeFromTime</c> floor, else 0 - instead of from 0; <em>and</em> Marten
    /// 9.52.0's own implementation of <c>IEventDatabase.FetchProjectionFloorAsync</c> (#5740,
    /// <c>src/Marten/Storage/MartenDatabase.ProjectionFloors.cs</c>), which stores the floors in
    /// <c>mt_event_projection_floors</c>. The interface's default body throws <c>NotSupportedException</c>, which the
    /// daemon reads as "no stored floor".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it changes.</b> A rebuild of a projection with a floor deletes every document the projection wrote - its
    /// teardown is still the whole database's - and replays only the events above the floor, so a document only the
    /// events at or below it built is gone for good. The rebuild dialog reads the stored floors with the studio's own SQL
    /// (<c>ProjectionProgressQueries.ReadProjectionFloorsAsync</c>), never through Marten's seam, which opens with
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c> (hard rule 14). A floor configured in code is invisible to the
    /// studio - <c>AsyncOptions</c> keeps its strategies private - so the dialog says that it may apply.
    /// </para>
    /// <para>
    /// <b>Both halves, because each ships in its own package.</b> A host that pinned JasperFx.Events 2.93 under Marten
    /// 9.51 replays from a configured floor but stores none; the probe answers "absent" there, and the dialog keeps its
    /// historical words - the edge this table accepts for a combination no Marten release ships.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature ProjectionStartFloors = new(
        nameof(ProjectionStartFloors),
        RuntimePackage.Marten,
        "9.52.0",
        "9.52.0",
        "A rebuild replays from the projection's stored or configured start floor rather than from the first event, and Marten stores floors in mt_event_projection_floors.",
        static () => typeof(AsyncOptions).GetMethod(
                "SubscribeFromFirstRun", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]) is not null
            && ImplementedBy(
                typeof(MartenDatabase),
                typeof(IEventDatabase),
                typeof(IEventDatabase).GetMethod(
                    "FetchProjectionFloorAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    [typeof(string), typeof(string), typeof(CancellationToken)])));

    /// <summary>
    /// <c>ProjectionExceptions.IsSystemicDatabaseFailure(Exception)</c> - JasperFx.Events 2.93.0 (jasperfx#1029/#1064,
    /// <c>src/JasperFx.Events/Projections/ProjectionExceptions.cs</c>; absent at v2.92.0), first in Marten 9.52.0: a
    /// <c>DbException</c> anywhere in a projection failure's graph that <c>TransientFailures</c> does not call transient - a
    /// missing column or table, a permission - is never dead-lettered, even under <c>SkipApplyErrors</c>
    /// (<c>ProjectionExecution</c> and <c>GroupedProjectionExecution</c> catch only what it says no to). The shard pauses
    /// as <c>ApplyEvent</c> with <c>ShardFailure.IsTransient</c> false.
    /// </summary>
    /// <remarks>
    /// Marten's half ships in the same release (#5741): <c>DocumentStore</c>'s static constructor stops registering
    /// <c>NpgsqlException</c> and <c>MartenCommandException</c> as transient, which until then let such an error escape
    /// unwrapped and pause the shard as <c>Other</c> (v9.51.0 <c>DocumentStore.EventStore.cs</c>). The probe asks for
    /// JasperFx's half alone: on the combination that has it without Marten's, a database error never reaches
    /// <c>ApplyEvent</c>, so what D05 says beside that category stays true.
    /// </remarks>
    public static readonly RuntimeFeature SystemicDatabaseFailure = new(
        nameof(SystemicDatabaseFailure),
        RuntimePackage.JasperFxEvents,
        "2.93.0",
        "9.52.0",
        "A database error in projection code (a missing column or table, a permission) pauses the shard at any setting, and is never dead-lettered under SkipApplyErrors.",
        static () => SystemicDatabaseFailureMethod() is not null);

    /// <summary>
    /// <c>IEventStore.AdvanceHighWaterMarkToLatestAsync(IEventDatabase, string?, long?, CancellationToken)</c>, implemented
    /// by Marten - JasperFx.Events 2.92.0 declares it as a default interface method that throws (jasperfx#1031), and Marten
    /// 9.52.0 implements it (#5738, <c>src/Marten/DocumentStore.HighWaterAdvance.cs</c>; absent at v9.51.0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the same release changes about the call the studio makes.</b> <c>AdvancedOperations.AdvanceHighWaterMarkToLatestAsync</c>
    /// now runs through <c>DocumentStore.AdvanceHighWaterMarksAsync</c>, which opens with
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c> - DDL under the database's own <c>AutoCreate</c> - and locks the mark
    /// with <c>FOR UPDATE</c>. Under <c>UseTenantPartitionedEvents</c> the tenant overload advances only
    /// <c>HighWaterMark:{tenant}</c>, and the tenant-less one every managed tenant's own mark in each database; below
    /// 9.52 both wrote the store-global <c>HighWaterMark</c> row, which on such a store the daemon never reads. The
    /// Advance high water mark dialog words both from this probe.
    /// </para>
    /// <para>
    /// The per-database, compare-and-set overload itself is 0.6's: the studio still calls <c>AdvancedOperations</c>, and
    /// still authorizes what that call reaches.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature PerDatabaseHighWaterAdvance = new(
        nameof(PerDatabaseHighWaterAdvance),
        RuntimePackage.Marten,
        "9.52.0",
        "9.52.0",
        "The high-water advance ensures the event store's storage first, moves each tenant's own mark under per-tenant partitioning, and has a per-database compare-and-set form on IEventStore.",
        static () => ImplementedBy(typeof(DocumentStore), typeof(IJasperFxEventStore), PerDatabaseAdvanceMethod()));

    /// <summary>JasperFx's <c>ProjectionExceptions.IsSystemicDatabaseFailure(Exception)</c>, or <see langword="null" /> below 2.93.</summary>
    internal static MethodInfo? SystemicDatabaseFailureMethod() =>
        typeof(AsyncOptions).Assembly
            .GetType("JasperFx.Events.Projections.ProjectionExceptions", throwOnError: false)?
            .GetMethod("IsSystemicDatabaseFailure", BindingFlags.Public | BindingFlags.Static, [typeof(Exception)]);

    /// <summary>The declaration of the per-database high-water advance on JasperFx's <c>IEventStore</c>, or <see langword="null" />.</summary>
    internal static MethodInfo? PerDatabaseAdvanceMethod() =>
        typeof(IJasperFxEventStore).GetMethod(
            "AdvanceHighWaterMarkToLatestAsync",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(IEventDatabase), typeof(string), typeof(long?), typeof(CancellationToken)]);
}
