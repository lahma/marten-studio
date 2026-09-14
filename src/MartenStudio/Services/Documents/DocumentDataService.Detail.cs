using System.Globalization;

using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Logging;

using Npgsql;

using Weasel.Core;

namespace MartenStudio.Services.Documents;

/// <summary>
/// The single-document half of the documents browser: the detail read, the "explain this document"
/// metadata pane, the outbound related documents and the two existence probes the detail page offers.
/// </summary>
internal sealed partial class DocumentDataService
{
    /// <inheritdoc />
    public async Task<DocumentDetailResult> GetDocumentAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            CollectionContext? context = await LoadContextAsync(resolved, connection, alias, cancellationToken)
                .ConfigureAwait(false);

            if (context is null)
            {
                return DocumentDetailResult.Missing($"This store has no collection called '{alias}'.");
            }

            DocumentTableInfo table = context.Table;

            if (!DocumentQueryBuilder.TryBuildSingle(
                    table,
                    id,
                    resolved.TenantId,
                    DocumentRowLock.None,
                    CommandTimeoutSeconds,
                    out NpgsqlCommand? command,
                    out var error))
            {
                // A malformed id is an ordinary thing - somebody pasted half a GUID into the address bar -
                // so the page says what shape the query would have had, and offers to look elsewhere.
                return DocumentDetailResult.Missing(error, ShapeOfSingleRead(table), malformed: true);
            }

            await using (command)
            {
                command.Connection = connection;
                command.CommandTimeout = CommandTimeoutSeconds;

                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return DocumentDetailResult.Missing(
                        $"No document with id '{id}' is in '{alias}'" +
                        (resolved.TenantId is { } tenant ? $" for tenant '{tenant}'." : "."),
                        command.CommandText);
                }

                DocumentDetail detail = ReadDetail(reader, context, alias, id);

                await reader.DisposeAsync().ConfigureAwait(false);

                return DocumentDetailResult.Ok(
                    await WithSizesAsync(connection, table, detail, resolved.TenantId, cancellationToken)
                        .ConfigureAwait(false));
            }
        }
        catch (PostgresException postgres)
        {
            return DocumentDetailResult.Missing($"{postgres.SqlState}: {postgres.MessageText}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not read document '{Id}' of '{Alias}'", id, alias);
            return DocumentDetailResult.Missing(Describe(exception));
        }
    }

    /// <inheritdoc />
    public async Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        DocumentDetailResult document = await GetDocumentAsync(scope, alias, id, cancellationToken).ConfigureAwait(false);

        return document.Detail is { } detail
            ? await GetRelatedAsync(scope, alias, detail, cancellationToken).ConfigureAwait(false)
            : RelatedDocuments.None;
    }

    /// <inheritdoc />
    public async Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        DocumentDetail detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(detail);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            CollectionContext? context = await LoadContextAsync(resolved, connection, alias, cancellationToken)
                .ConfigureAwait(false);

            if (context?.DocumentType is null || context.DocumentType.ForeignKeys.Count == 0)
            {
                return RelatedDocuments.None;
            }

            List<RelatedDocumentLink> links = [];

            foreach (Weasel.Postgresql.Tables.ForeignKey foreignKey in context.DocumentType.ForeignKeys)
            {
                if (foreignKey.ColumnNames.Length != 1)
                {
                    // A composite foreign key on a Marten document is a tenanted key: the tenant half is
                    // already the scope, and pointing at "the other tenant's row" would be wrong.
                    continue;
                }

                var column = foreignKey.ColumnNames[0];
                var value = detail.Columns.FirstOrDefault(x =>
                    string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase))?.Value;

                // Only among the types this visitor may see. A foreign key is a declared relationship, but
                // it is not a reason to name a collection the host hid, nor to link into Marten's own
                // dead-letter table (P2-fix H4).
                IDocumentType? target = foreignKey.LinkedTable is { } linked
                    ? FindVisibleByTable(resolved.Store, linked)
                    : null;

                if (target is null)
                {
                    continue;
                }

                // WithPhysicalColumns, because the target's id column is what the probe binds against: a
                // strong-typed id guesses Unknown from the CLR type and would be sent as an untyped
                // literal, which cannot use the primary key and fails outright against some column types.
                DocumentTableInfo targetTable = DocumentTableInfo.FromDocumentType(target)
                    .WithPhysicalColumns(await columnCatalog
                        .GetAsync(connection, target.TableName.Schema, target.TableName.Name, cancellationToken)
                        .ConfigureAwait(false));

                var exists = value is not null && await ExistsAsync(
                        connection, targetTable, value, resolved.TenantId, cancellationToken)
                    .ConfigureAwait(false);

                links.Add(new RelatedDocumentLink(
                    column,
                    target.Alias,
                    target.DocumentType.Name,
                    value,
                    exists,
                    CollectionColorizer.HueFor(target.Alias)));
            }

            return new RelatedDocuments(links);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not read the related documents of '{Alias}'", alias);
            return new RelatedDocuments([], Describe(exception));
        }
    }

    /// <inheritdoc />
    public async Task<bool> StreamExistsAsync(
        StudioScope scope,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            // Streams are read through a Marten session rather than raw SQL: FetchStreamStateAsync already
            // knows which identity style this store uses and which table that means.
            //
            // Pinned to the scope's database with SessionOptions.ForDatabase. QuerySession(tenant) routes
            // through the store's own tenancy, which on a multi-database store picks the database that
            // tenant maps to - not the one the visitor selected and the store policy authorized - and
            // QuerySession() with no argument picks the default database outright.
            await using IQuerySession session = resolved.Store.QuerySession(
                resolved.TenantId is { } tenant
                    ? SessionOptions.ForDatabase(tenant, resolved.Database)
                    : SessionOptions.ForDatabase(resolved.Database));

            if (resolved.Store.Options.Events.StreamIdentity == StreamIdentity.AsGuid)
            {
                return Guid.TryParse(id, CultureInfo.InvariantCulture, out Guid streamId) &&
                       await session.Events.FetchStreamStateAsync(streamId, cancellationToken).ConfigureAwait(false) is not null;
            }

            return await session.Events.FetchStreamStateAsync(id, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A store with no event tables yet answers with an error, and "there is no stream" is the
            // honest rendering of that: the link is simply not offered.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not check whether a stream '{Id}' exists", id);
            }

            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentIdProbe>> ProbeIdAsync(
        StudioScope scope,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        List<DocumentIdProbe> probes = [];

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (IDocumentType documentType in VisibleDocumentTypes(resolved.Store))
            {
                if (probes.Count >= MaxIdProbes)
                {
                    break;
                }

                DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType)
                    .WithPhysicalColumns(await columnCatalog
                        .GetAsync(connection, documentType.TableName.Schema, documentType.TableName.Name, cancellationToken)
                        .ConfigureAwait(false));

                // Only collections whose id column this string could actually be: probing a uuid column
                // with "ORD-2026-0001" is a query that can only ever answer no.
                if (!DocumentQueryBuilder.ParseId(table.IdColumnType, id).Success)
                {
                    continue;
                }

                var found = await ExistsAsync(connection, table, id, resolved.TenantId, cancellationToken)
                    .ConfigureAwait(false);

                probes.Add(new DocumentIdProbe(table.Alias, CollectionColorizer.HueFor(table.Alias), found));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not probe the collections for id '{Id}'", id);
        }

        return probes;
    }

    /// <summary>Whether one id is in one collection, in the cheapest read Postgres will accept.</summary>
    /// <remarks>
    /// <c>select 1 … limit 1</c> rather than a one-row list: the list's select list carries
    /// <c>octet_length(data::text)</c>, which detoasts and decompresses every document it touches, and this
    /// method is called once per collection by the id probe and once per foreign key by the related-
    /// documents strip.
    /// </remarks>
    private async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        string id,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!DocumentQueryBuilder.TryBuildExists(
                table, id, tenantId, CommandTimeoutSeconds, out NpgsqlCommand? command))
        {
            return false;
        }

        try
        {
            await using (command)
            {
                command.Connection = connection;

                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not probe '{Alias}' for an id", table.Alias);
            }

            return false;
        }
    }

    /// <summary>
    /// The visible mapping whose table a foreign key points at, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="VisibleDocumentTypes"/> rather than <c>AllKnownDocumentTypes()</c>: a related-documents
    /// strip that named a hidden collection, linked to it and said whether the row is there is the
    /// visibility gate leaking through a relationship the host never thought about.
    /// </remarks>
    private IDocumentType? FindVisibleByTable(IDocumentStore store, DbObjectName table)
    {
        foreach (IDocumentType documentType in VisibleDocumentTypes(store))
        {
            if (string.Equals(documentType.TableName.Name, table.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(documentType.TableName.Schema, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                return documentType;
            }
        }

        return null;
    }

    /// <summary>
    /// Turns the single-document read into the metadata pane: every physical column present, in table
    /// order, with the duplicated fields checked against the JSON they shadow.
    /// </summary>
    private static DocumentDetail ReadDetail(NpgsqlDataReader reader, CollectionContext context, string alias, string id)
    {
        DocumentTableInfo table = context.Table;

        var json = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
        var textBytes = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

        // The builder's single-document select is data, data_bytes, then the metadata columns and then
        // the duplicated columns, in the order the table info lists them.
        var ordinal = 2;

        List<PhysicalColumnValue> columns =
        [
            new(DocumentTableInfo.IdColumn, id, PhysicalColumnRole.Identity),
            new(DocumentTableInfo.DataColumn, null, PhysicalColumnRole.Data),
        ];

        Dictionary<DocumentMetadataColumn, string?> metadata = [];

        foreach (DocumentMetadataColumnInfo column in table.MetadataColumns)
        {
            var value = reader.IsDBNull(ordinal) ? null : DisplayValue(reader.GetValue(ordinal));

            columns.Add(new PhysicalColumnValue(column.ColumnName, value, PhysicalColumnRole.Metadata));
            metadata[column.Column] = value;
            ordinal++;
        }

        List<DuplicatedFieldAgreement> duplicated = [];

        foreach (DuplicatedColumnInfo column in table.DuplicatedColumns)
        {
            var value = reader.IsDBNull(ordinal) ? null : DisplayValue(reader.GetValue(ordinal));

            columns.Add(new PhysicalColumnValue(column.ColumnName, value, PhysicalColumnRole.Duplicated));

            IReadOnlyList<string>? path = DuplicatedFieldPaths.Resolve(
                context.ClrType, column.MemberPath, context.NamingPolicy);

            var fromJson = DuplicatedFieldPaths.ReadValue(json, path);

            duplicated.Add(new DuplicatedFieldAgreement(
                column.ColumnName,
                column.MemberPath,
                path,
                value,
                fromJson,
                DuplicatedFieldPaths.Compare(value, fromJson, path is not null)));

            ordinal++;
        }

        var storedType = metadata.GetValueOrDefault(DocumentMetadataColumn.DotNetType);
        var rowAlias = metadata.GetValueOrDefault(DocumentMetadataColumn.DocumentType);

        // The type this *row* is, not the type the collection's root is. On a hierarchy every row carries
        // its subclass in mt_doc_type, and comparing a Car row against Vehicle's name reported a mismatch
        // on every subclass row in the store (P2-fix H3).
        DotNetTypeCheck check = CheckDotNetType(context, rowAlias, storedType);

        return new DocumentDetail
        {
            Alias = alias,
            Id = id,
            Json = json,
            TextBytes = textBytes,
            Columns = columns,
            Duplicated = duplicated,
            TableName = table.QualifiedName,
            StoredDotNetType = storedType,
            ExpectedDotNetType = check.Expected,
            DotNetTypeMismatch = check.Mismatch,
            DotNetTypeWarning = check.Warning,
            ClrType = context.ClrType,
            NamingPolicy = context.NamingPolicy,
            IsRegistered = context.IsRegistered,
            IsDeleted = string.Equals(metadata.GetValueOrDefault(DocumentMetadataColumn.IsSoftDeleted), "true", StringComparison.Ordinal),
            TenantId = metadata.GetValueOrDefault(DocumentMetadataColumn.TenantId),
            DocumentTypeAlias = metadata.GetValueOrDefault(DocumentMetadataColumn.DocumentType),
            IdColumnType = table.IdColumnType,
        };
    }

    /// <summary>What the <c>mt_dotnet_type</c> comparison came to.</summary>
    /// <param name="Expected">The full name of the type this row should hold, when one could be resolved.</param>
    /// <param name="Mismatch">Whether the stored name names a different type.</param>
    /// <param name="Warning">Something worth saying that is not a mismatch, or <see langword="null"/>.</param>
    internal readonly record struct DotNetTypeCheck(string? Expected, bool Mismatch, string? Warning);

    /// <summary>
    /// Compares <c>mt_dotnet_type</c> with the type this row is supposed to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On a hierarchy the expected type is the row's own subclass</b>, resolved through
    /// <c>IDocumentType.TypeFor(mt_doc_type)</c> — which is Marten's own mapping from discriminator to CLR
    /// type, and the only correct answer. Comparing every row of a hierarchy against the root's name was
    /// reporting a mismatch on each of them, and the old defence against that — accepting any stored name
    /// whose last segment matched the expected simple name — forgave the very thing this check exists to
    /// find: a type that moved to another namespace.
    /// </para>
    /// <para>
    /// <c>TypeFor</c> throws <see cref="ArgumentOutOfRangeException"/> for an alias no subclass claims
    /// (verified against Marten 9.35). That is a real finding — a row whose discriminator names a subclass
    /// this store no longer registers deserializes as nothing at all — so it becomes a warning rather than
    /// an exception or a silent pass.
    /// </para>
    /// <para>
    /// The comparison is on the type name only, never on the assembly-qualified string: Marten writes the
    /// assembly version into it, so comparing the whole thing would fire on every store whose assembly was
    /// rebuilt, which is every store.
    /// </para>
    /// </remarks>
    /// <param name="context">The collection, with its mapping when there is one.</param>
    /// <param name="rowAlias">The row's <c>mt_doc_type</c>, when the column exists.</param>
    /// <param name="storedType">The row's <c>mt_dotnet_type</c>, when the column exists.</param>
    internal static DotNetTypeCheck CheckDotNetType(CollectionContext context, string? rowAlias, string? storedType)
    {
        ArgumentNullException.ThrowIfNull(context);

        Type? expected = context.ClrType;
        string? warning = null;

        if (!string.IsNullOrWhiteSpace(rowAlias) && context.DocumentType is { } documentType)
        {
            try
            {
                expected = documentType.TypeFor(rowAlias);
            }
            catch (ArgumentOutOfRangeException)
            {
                warning =
                    $"This row's mt_doc_type is '{rowAlias}', which is not a subclass '{documentType.Alias}' " +
                    "registers any more. Marten cannot deserialize it.";
            }
        }

        var expectedName = expected?.FullName;

        if (string.IsNullOrWhiteSpace(storedType) || expectedName is null)
        {
            return new DotNetTypeCheck(expectedName, false, warning);
        }

        var storedName = storedType.Split(',', 2)[0].Trim();

        return new DotNetTypeCheck(
            expectedName,
            !string.Equals(storedName, expectedName, StringComparison.Ordinal),
            warning);
    }

    private async Task<DocumentDetail> WithSizesAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        DocumentDetail detail,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!DocumentBrowseQueries.TryBuildSizes(
                table, detail.Id, tenantId, CommandTimeoutSeconds, out NpgsqlCommand? command, out _))
        {
            return detail;
        }

        await using (command)
        {
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return detail;
            }

            return detail with
            {
                TextBytes = reader.IsDBNull(0) ? detail.TextBytes : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                StoredBytes = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            };
        }
    }

    /// <summary>
    /// The shape of the read that would have run, for the not-found page. It names the column and its
    /// type, which is the fact that explains why the id did not fit.
    /// </summary>
    private static string ShapeOfSingleRead(DocumentTableInfo table)
    {
        var tenant = table.TenancyStyle == TenancyStyle.Conjoined ? " and \"tenant_id\" = @tenant" : string.Empty;

        return $"select \"data\"::text from {table.QualifiedName} where \"id\" = @id{tenant}" +
               $"  -- \"id\" is {table.IdColumnType.ToString().ToLowerInvariant()}";
    }
}
