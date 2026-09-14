using System.Globalization;

using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

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

            if (!DocumentQueryBuilder.TryBuildSingle(table, id, resolved.TenantId, out NpgsqlCommand? command, out var error))
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

                detail = await WithSizesAsync(connection, table, detail, resolved.TenantId, cancellationToken)
                    .ConfigureAwait(false);

                return DocumentDetailResult.Ok(await WithUpsertFunctionAsync(resolved, table, detail).ConfigureAwait(false));
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

            DocumentDetailResult document = await GetDocumentAsync(scope, alias, id, cancellationToken).ConfigureAwait(false);

            if (document.Detail is not { } detail)
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

                IDocumentType? target = foreignKey.LinkedTable is { } linked
                    ? FindByTable(resolved.Store, linked)
                    : null;

                if (target is null)
                {
                    continue;
                }

                var exists = value is not null && await ExistsAsync(
                        connection, DocumentTableInfo.FromDocumentType(target), value, resolved.TenantId, cancellationToken)
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
            await using IQuerySession session = resolved.TenantId is { } tenant
                ? resolved.Store.QuerySession(tenant)
                : resolved.Store.QuerySession();

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

    private async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        string id,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!DocumentQueryBuilder.ParseId(table.IdColumnType, id).Success)
        {
            return false;
        }

        var query = new DocumentListQuery
        {
            Predicates = [new DocumentPredicate.IdEquals(id)],
            Sort = DocumentColumn.ById,
            PageSize = 1,
            IncludeDeleted = table.SoftDeleteEnabled ? DeletedFilter.Include : DeletedFilter.Exclude,
            TenantId = tenantId,
            Columns = [],
            MaxInlineDocumentBytes = 0,
        };

        try
        {
            await using NpgsqlCommand command = DocumentQueryBuilder.BuildList(table, query);

            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

    private static IDocumentType? FindByTable(IDocumentStore store, DbObjectName table)
    {
        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
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
        var expectedType = context.ClrType?.AssemblyQualifiedName is { } qualified
            ? qualified
            : context.ClrType?.FullName;

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
            ExpectedDotNetType = expectedType,
            DotNetTypeMismatch = IsDotNetTypeMismatch(storedType, context.ClrType),
            ClrType = context.ClrType,
            NamingPolicy = context.NamingPolicy,
            IsRegistered = context.IsRegistered,
            IsDeleted = string.Equals(metadata.GetValueOrDefault(DocumentMetadataColumn.IsSoftDeleted), "true", StringComparison.Ordinal),
            TenantId = metadata.GetValueOrDefault(DocumentMetadataColumn.TenantId),
            DocumentTypeAlias = metadata.GetValueOrDefault(DocumentMetadataColumn.DocumentType),
            IdColumnType = table.IdColumnType,
        };
    }

    /// <summary>
    /// Whether <c>mt_dotnet_type</c> names a different type from the one the mapping expects.
    /// </summary>
    /// <remarks>
    /// Compared on the type name only, not on the assembly-qualified string: Marten writes the fully
    /// qualified name including the assembly version, so a mismatch on the whole string would fire on
    /// every store whose assembly was rebuilt, which is every store. What matters — and what makes
    /// deserialization produce something other than what the mapping expects — is the type moving or
    /// being renamed.
    /// </remarks>
    internal static bool IsDotNetTypeMismatch(string? storedType, Type? expected)
    {
        if (string.IsNullOrWhiteSpace(storedType) || expected is null)
        {
            return false;
        }

        var storedName = storedType.Split(',', 2)[0].Trim();
        var expectedName = expected.FullName;

        if (expectedName is null)
        {
            return false;
        }

        if (string.Equals(storedName, expectedName, StringComparison.Ordinal))
        {
            return false;
        }

        // A hierarchy's rows carry the *subclass* type, which is a different name from the root and is
        // exactly right rather than a mismatch. Nested type names use '+' where the CLR does.
        return !storedName.EndsWith("." + expected.Name, StringComparison.Ordinal) &&
               !storedName.EndsWith("+" + expected.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Names the <c>mt_upsert_&lt;alias&gt;</c> function when the database actually has one.
    /// </summary>
    /// <remarks>
    /// Looked up rather than composed. Marten 9 writes documents with inline SQL and creates no
    /// per-document upsert function, so a pane that printed the conventional name would be naming an
    /// object that is not there - which is precisely the kind of thing somebody would then go looking for
    /// in psql.
    /// </remarks>
    private async Task<DocumentDetail> WithUpsertFunctionAsync(
        ResolvedScope resolved,
        DocumentTableInfo table,
        DocumentDetail detail)
    {
        var wanted = "mt_upsert_" + table.Alias;

        try
        {
            foreach (DbObjectName function in await resolved.Database.Functions().ConfigureAwait(false))
            {
                if (string.Equals(function.Name, wanted, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(function.Schema, table.Schema, StringComparison.OrdinalIgnoreCase))
                {
                    return detail with { UpsertFunction = SqlIdentifier.Qualify(function.Schema, function.Name) };
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not list the functions of the database");
        }

        return detail;
    }

    private async Task<DocumentDetail> WithSizesAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        DocumentDetail detail,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!DocumentBrowseQueries.TryBuildSizes(table, detail.Id, tenantId, out NpgsqlCommand? command, out _))
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
