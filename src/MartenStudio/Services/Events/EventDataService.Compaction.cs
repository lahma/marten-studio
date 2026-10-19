using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Services.Events;

// F3: stream compaction. In a file of its own so the event service's 1,700 lines are not one more feature
// longer; the class's documentation is EventDataService.cs's.
internal sealed partial class EventDataService
{
    /// <summary>The action name the audit ring records a compaction under.</summary>
    internal const string CompactStreamAction = "Compact stream";

    /// <inheritdoc />
    public async Task<CompactionPlan> DescribeCompactionAsync(
        StudioScope scope,
        StreamState stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            // The scope is still resolved - the store policy is asked, and the options are this store's - but
            // the stream is the one the page has just read through GetStreamAsync, the same tenant-scoped
            // mt_streams read, rather than a second read of the same row. Never Marten's
            // FetchStreamStateAsync, which opens with EnsureStorageExistsAsync and resolves the type through
            // the cache CompactionAggregates explains the studio must not write to. Nothing here is trusted
            // by the write: CompactStreamAsync reads the stream again.
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            return Assess(resolved, stream).Plan;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return CompactionPlan.Failed(stream.Id, Describe(scope, exception, "describe the stream's compaction"));
        }
    }

    /// <inheritdoc />
    public async Task<string> CompactStreamAsync(
        StudioScope scope,
        string streamId,
        string confirmation,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string target = streamId ?? string.Empty;

        // Capability, then the scope with the write policy asked about (store, database, tenant,
        // CompactStreams), then the operation, then the audit - on failure as well as on success (hard
        // rule 5). The scope's own tenant is the resource asked about first: compaction reaches one stream,
        // never the database as a whole - but where that stream also holds an event another tenant's session
        // appended, step 2b asks about the database as a whole as well. On a conjoined
        // event store Marten keeps it to that tenant itself - every statement on the session path carries the
        // session's tenant. On any other store it does not: Marten fetches the stream by id alone and deletes and
        // replaces the sequences that fetch returned with no tenant predicate (ConjoinedEventFilter, read at
        // V9.39.0 and V9.50.2), while every append records its session's tenant in mt_streams.tenant_id. There it is step 1's
        // read - narrowed to the scope's tenant whenever mt_streams has the column - that keeps a compaction to
        // one tenant's stream: another tenant's stream of a typed id is not found, and is refused as missing.
        return await MutateAsync(
            scope,
            StudioCapability.CompactStreams,
            CompactStreamAction,
            target,
            async (resolved, token) =>
            {
                IdParseResult parsed = ParseStreamIdFor(resolved, target);
                if (!parsed.Success)
                {
                    throw new InvalidOperationException(parsed.Error ?? "That stream id is not usable.");
                }

                // 0. The id Marten's stream fetch reads as "no filter" is refused before anything is read: the
                //    plan refuses it too, but this is the service and the page's plan is not evidence.
                if (StreamCompactionRules.IsWholeLogId(parsed.Value))
                {
                    throw new InvalidOperationException(StreamCompactionRules.Explain(CompactionUnavailableReason.EmptyStreamId));
                }

                // 1. The stream, read again here rather than trusted from the page: what is refused is decided
                //    where the operation is (hard rule 5), and the page's plan may be a minute old. On the
                //    primary, as the read-back in step 5 is: a standby's row can be behind, and both the
                //    cutoff check and the watermark the verdict is measured from have to be the primary's. And
                //    in the scope: this read, not Marten, is what keeps a compaction on a store with
                //    single-tenanted events to the scope's tenant's stream (see above) - a stream it does not find
                //    is StreamMissing, whose refusal names no tenant and is the one a stream that does not exist gets.
                StreamState stream = await ReadStreamOnPrimaryAsync(resolved.Scope, target, token).ConfigureAwait(false);
                (CompactionPlan plan, CompactionAggregate? aggregate) = Assess(resolved, stream);

                if (plan.Error is { } error)
                {
                    throw new InvalidOperationException(error.Display);
                }

                if (!plan.IsOffered || aggregate is null)
                {
                    throw new InvalidOperationException(plan.Explanation ?? "This stream cannot be compacted.");
                }

                // 2. One tenant or none. A conjoined store's session always has exactly one tenant, and the
                //    "all tenants" scope's would be *DEFAULT* - the default tenant's stream of the same id,
                //    which is the one thing this must never compact in place of the one on screen.
                if (plan.NeedsTenant)
                {
                    throw new InvalidOperationException(StreamCompactionRules.TenantRequired(plan.StreamTenantId));
                }

                // 2b. Reach, not scope: on a store whose events are not conjoined Marten compacts every event of the
                //     stream, whichever tenant's session appended it, so one with another tenant's events asks for the
                //     database as a whole as well (SEC1-fix F1).
                await RequireReachOverOtherTenantsEventsAsync(resolved, parsed.Value!, StudioCapability.CompactStreams, token)
                    .ConfigureAwait(false);

                // 3. What the visitor typed, against the stream's id as the database holds it. The dialog
                //    checks this too; this is the check that counts, because a circuit is a long-lived object
                //    a client can drive.
                if (!StreamCompactionRules.ConfirmationMatches(confirmation, stream.Id))
                {
                    throw new InvalidOperationException(StreamCompactionRules.ConfirmationMismatch);
                }

                // 3b. What runs is bounded by what the dialog described: Marten folds through the dialog's
                //     version and no further, so an event appended since stays after the snapshot. A version
                //     past the stream's end, or one an earlier compaction already folded, is refused; a stream
                //     that has grown since is compacted through the dialog's version, not refused.
                if (StreamCompactionRules.CutoffProblem(expectedVersion, stream) is { } cutoff)
                {
                    throw new InvalidOperationException(cutoff);
                }

                // 4. Past the confirmation, on a token of this operation's own, as the schema apply does. It is
                //    one transaction, so a cancelled compaction would roll back cleanly - but a closed tab or a
                //    dropped circuit is not a decision to abandon something somebody typed a stream id to
                //    start, and an audit entry that reads "cancelled" leaves the operator asking whether it
                //    committed. The session's own Timeout (QueryTimeout) still bounds every statement.
                using var compacting = new CancellationTokenSource();

                await using (IDocumentSession session = OpenWriteSession(resolved))
                {
                    try
                    {
                        await StreamCompactionInvoker
                            .CompactAsync(session, aggregate.Type, parsed.Value!, expectedVersion, compacting.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (TagTableRefusal(exception) is { } refusal)
                    {
                        throw new InvalidOperationException(
                            StreamCompactionRules.TaggedStreamRefused(refusal.TableName), refusal);
                    }
                }

                // 5. The truth, read back. Whether this save moved the watermark - against the read in step 1,
                //    not against the dialog's version - is what says whether it compacted, and the watermark
                //    is what says through which version: Marten folds through the last event its fetch
                //    returned, which on a skipping store is below the dialog's version when that event was
                //    skipped. A save that committed nothing is the failure it is. One whose read-back failed is
                //    neither: the save returned, so it is "saved, not confirmed", never a plain failure. On the
                //    primary: an asynchronous standby can still hold the old compacted_version, which would
                //    read as "nothing was compacted" with the events already deleted.
                StreamState after;
                try
                {
                    after = await ReadStreamOnPrimaryAsync(resolved.Scope, target, compacting.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new StreamCompactionUnconfirmedException(
                        StreamCompactionRules.Unconfirmed("the stream could not be read back - " + exception.Message), exception);
                }

                (CompactionVerdict verdict, string outcome) = StreamCompactionRules.Verify(
                    expectedVersion, stream.CompactedVersion, after, aggregate.Name, plan.StreamTenantId, plan.SkippedEventsStay);

                return verdict switch
                {
                    CompactionVerdict.Compacted => outcome,
                    CompactionVerdict.Unconfirmed => throw new StreamCompactionUnconfirmedException(outcome),
                    _ => throw new InvalidOperationException(outcome),
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The plan for one stream, and the aggregate it compacts into.</summary>
    private (CompactionPlan Plan, CompactionAggregate? Aggregate) Assess(ResolvedScope resolved, StreamState stream)
    {
        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;

        (CompactionAggregate? aggregate, CompactionUnavailableReason reason) = CompactionAggregates.Resolve(
            CompactionAggregates.Registered(storeOptions), stream.AggregateType, options.Value.IsDocumentTypeVisible);

        CompactionPlan plan = StreamCompactionRules.Plan(
            stream,
            storeOptions.Events.StreamIdentity,
            aggregate,
            reason,
            storeOptions.Events.TenancyStyle == TenancyStyle.Conjoined,
            resolved.TenantId,
            EventTagModel.From(storeOptions).Storage,
            storeOptions.Events.EnableEventSkippingInProjectionsOrSubscriptions);

        return (plan, plan.IsOffered ? aggregate : null);
    }

    /// <summary>
    /// The foreign-key refusal a tagged stream's delete gets from a tag table, wherever Marten wrapped it.
    /// </summary>
    /// <remarks>
    /// <c>23503</c> on a delete is reported against the referencing table, which for this delete can only be
    /// a tag table (<c>mt_event_tag_&lt;suffix&gt;</c>) - matched by table or constraint name so that any other
    /// foreign key violation keeps Postgres' own words.
    /// </remarks>
    private static PostgresException? TagTableRefusal(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } postgres
                && (postgres.TableName?.StartsWith(EventTagModel.TablePrefix, StringComparison.Ordinal) == true
                    || postgres.ConstraintName?.Contains(EventTagModel.TablePrefix, StringComparison.Ordinal) == true))
            {
                return postgres;
            }
        }

        return null;
    }
}
