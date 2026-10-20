namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The move manifest: every file under <c>src/MartenStudio/</c> that moves to the shared assembly (PostgresStudio) in
/// 0.5, where it goes and the namespace it then declares - final as of PS-1e, and the table PS-2's move script is written
/// from.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a row means.</b> The file, relative to <c>src/MartenStudio/</c>, moves to <c>src/PostgresStudio/</c> - at the
/// same relative path unless the row names another (<see cref="Entry.NewPath" />: a folder change, or a type renamed with
/// its file, <see cref="TypeRenames" />). It is a core row of the design's Appendix A, or the core half of a mixed one as
/// the packet that split it left it. Its packet is the one that made it Marten-free; <see cref="CoreFenceTests" /> fences
/// every row's source file alike.
/// </para>
/// <para>
/// <b>Namespaces follow folders, on both sides.</b> Every C# file on the manifest declares the namespace its folder gives
/// under <c>MartenStudio</c> (<see cref="Entry.OldNamespace" />), and every Razor file takes it from its folder, so the
/// namespace it declares after the move is its new folder's under <c>PostgresStudio</c> (<see cref="Entry.NewNamespace" />)
/// - which is what makes the rename a table rather than a judgement. <see cref="CoreFenceTests" /> holds both halves.
/// </para>
/// <para>
/// <b>What is not on it is MartenStudio's</b>, and stays in <c>src/MartenStudio/</c> under its own namespace.
/// <see cref="CoreFenceTests" /> collects every type and global alias declared in a <c>src/</c> file that is not on this
/// list, and a manifest file may name none of them.
/// </para>
/// </remarks>
internal static class CoreManifest
{
    /// <summary>The packet that made a row Marten-free.</summary>
    public enum Packet
    {
        /// <summary>The spine: settings, descriptor, sources, scope, authorization, audit, shell, routes, layout.</summary>
        PS1a,

        /// <summary>The database browser and Sessions.</summary>
        PS1b,

        /// <summary>The health advisor.</summary>
        PS1c,

        /// <summary>The SQL console, Relationships, Overview and Query shells, live badges.</summary>
        PS1d,

        /// <summary>Core files no 1b-1d packet owned - the JSON toolkit, the shared building blocks, the assets - fenced by PS-1e.</summary>
        PS1e,
    }

    /// <summary>One file bound for the shared assembly.</summary>
    /// <param name="Path">Relative to <c>src/MartenStudio/</c>, with forward slashes.</param>
    /// <param name="Owner">The packet that made it Marten-free.</param>
    /// <param name="NewPath">
    /// Its path relative to <c>src/PostgresStudio/</c> when that is not <paramref name="Path" />: a folder change, a type
    /// renamed with its file, or an asset renamed.
    /// </param>
    public sealed record Entry(string Path, Packet Owner, string? NewPath = null)
    {
        /// <summary>The path relative to the repository root, as the scanners report it.</summary>
        public string RepositoryPath => "src/MartenStudio/" + Path;

        /// <summary>Where it goes, relative to <c>src/PostgresStudio/</c>.</summary>
        public string TargetPath => NewPath ?? Path;

        /// <summary>Where it goes, relative to the repository root.</summary>
        public string NewRepositoryPath => "src/PostgresStudio/" + TargetPath;

        /// <summary>Whether it is C# or Razor: code the fence reads and a namespace moves with.</summary>
        public bool IsSource => Path.EndsWith(".cs", StringComparison.Ordinal) || Path.EndsWith(".razor", StringComparison.Ordinal);

        /// <summary>The namespace it declares today, or <see langword="null" /> for an asset.</summary>
        public string? OldNamespace => IsSource ? NamespaceOf("MartenStudio", Path) : null;

        /// <summary>The namespace it declares after the move, or <see langword="null" /> for an asset.</summary>
        public string? NewNamespace => IsSource ? NamespaceOf("PostgresStudio", TargetPath) : null;

        private static string NamespaceOf(string root, string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? root : root + "." + path[..slash].Replace('/', '.');
        }
    }

    /// <summary>
    /// The types renamed with the move (design Appendix D): a shared type named <c>MartenStudio*</c> takes the neutral
    /// <c>Studio*</c> name, and its file is renamed with it (<see cref="Entry.NewPath" />).
    /// </summary>
    public static IReadOnlyDictionary<string, string> TypeRenames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MartenStudioEndpointAuthorizationGuard"] = "StudioEndpointAuthorizationGuard",
        ["MartenStudioEndpointMarker"] = "StudioEndpointMarker",
        ["MartenStudioMappedEndpoints"] = "StudioMappedEndpoints",
        ["MartenStudioConventionBuilder"] = "StudioConventionBuilder",
        ["MartenStudioHealthCheck"] = "StudioHealthCheck",
    };

    /// <summary>Every row, grouped by packet.</summary>
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        // ---- PS1a --------------------------------------------------------
        new("Components/Layout/CapabilityBanner.razor", Packet.PS1a),
        new("Components/Layout/NavMenu.razor", Packet.PS1a),
        new("Components/Layout/PreferencesMenu.razor", Packet.PS1a),
        new("Components/Layout/ScopeSelector.razor", Packet.PS1a),
        new("Components/Layout/StudioLayout.razor", Packet.PS1a),
        new("Components/Shared/CapabilityDisabled.razor", Packet.PS1a),
        new("Components/Shared/ComponentModel.cs", Packet.PS1a),
        new("Components/Shared/ScopeNotAuthorized.razor", Packet.PS1a),
        new("Components/Shared/WriteRefusal.razor", Packet.PS1a),
        new("Components/StudioDocument.razor", Packet.PS1a),
        new("Components/StudioLink.cs", Packet.PS1a),
        new("Components/StudioRoutes.razor", Packet.PS1a),
        new("Components/StudioRouteTable.cs", Packet.PS1a),
        new("Components/StudioScopeQuery.cs", Packet.PS1a),
        new("Internal/MartenStudioEndpointAuthorizationGuard.cs", Packet.PS1a, "Internal/StudioEndpointAuthorizationGuard.cs"),
        new("Internal/MartenStudioEndpointMarker.cs", Packet.PS1a, "Internal/StudioEndpointMarker.cs"),
        new("Internal/MartenStudioMappedEndpoints.cs", Packet.PS1a, "Internal/StudioMappedEndpoints.cs"),
        new("Internal/StudioEndpointMapper.cs", Packet.PS1a),
        new("MartenStudioConventionBuilder.cs", Packet.PS1a, "StudioConventionBuilder.cs"),
        new("Services/Settings/IStudioLinks.cs", Packet.PS1a),
        new("Services/Settings/IStudioSettings.cs", Packet.PS1a),
        new("Services/Settings/StudioCoreRegistration.cs", Packet.PS1a),
        new("Services/Settings/StudioDescriptor.cs", Packet.PS1a),
        new("Services/Settings/StudioPath.cs", Packet.PS1a),
        new("Services/Settings/StudioRegistrationMarker.cs", Packet.PS1a),
        new("Services/Sources/IDatabaseDeclarationsSource.cs", Packet.PS1a),
        new("Services/Sources/IHealthTableSource.cs", Packet.PS1a),
        new("Services/Sources/ISessionLockLabeler.cs", Packet.PS1a),
        new("Services/Sources/ISqlConsoleService.cs", Packet.PS1a),
        new("Services/Sources/IStudioDatabase.cs", Packet.PS1a),
        new("Services/Sources/IStudioSourceProvider.cs", Packet.PS1a),
        new("Services/Sources/ResolvedSource.cs", Packet.PS1a),
        new("Services/Sources/StudioSourceResolver.cs", Packet.PS1a),
        new("Services/StudioActionLog.cs", Packet.PS1a),
        new("Services/StudioActionLogService.cs", Packet.PS1a),
        new("Services/StudioAuthorization.cs", Packet.PS1a),
        new("Services/StudioCapabilityGuard.cs", Packet.PS1a),
        new("Services/StudioLog.cs", Packet.PS1a),
        new("Services/StudioLog.Export.cs", Packet.PS1a),
        new("Services/StudioLog.Server.cs", Packet.PS1a),
        new("Services/StudioLog.Sessions.cs", Packet.PS1a),
        new("Services/StudioLog.Statements.cs", Packet.PS1a),
        new("Services/StudioLogThrottle.cs", Packet.PS1a),
        new("Services/StudioScope.cs", Packet.PS1a),
        new("Services/StudioScopeCatalog.cs", Packet.PS1a),
        new("Services/StudioScopeModels.cs", Packet.PS1a),
        new("Services/StudioState.cs", Packet.PS1a),

        // ---- PS1b --------------------------------------------------------
        new("Components/Pages/Database/DatabaseAccessNotice.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseDefinitionBody.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseDefinitionLoader.cs", Packet.PS1b),
        new("Components/Pages/Database/DatabaseDefinitionWithheld.cs", Packet.PS1b),
        new("Components/Pages/Database/DatabaseFunctionsGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseNameFilter.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseObjects.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseOwnerBadge.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabasePresentation.cs", Packet.PS1b),
        new("Components/Pages/Database/DatabaseRail.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseRelationBadges.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseRelationsGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseRelationshipsSlot.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseRowsSlot.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseSequencesGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseTriggersGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseTypeDescription.razor", Packet.PS1b),
        new("Components/Pages/Database/DatabaseTypesGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/ObjectColumnsTab.razor", Packet.PS1b),
        new("Components/Pages/Database/ObjectDetail.razor", Packet.PS1b),
        new("Components/Pages/Database/ObjectKeysTab.razor", Packet.PS1b),
        new("Components/Pages/Database/RowColumnChooser.razor", Packet.PS1b),
        new("Components/Pages/Database/RowDetail.razor", Packet.PS1b),
        new("Components/Pages/Database/RowFilterStrip.razor", Packet.PS1b),
        new("Components/Pages/Database/RowGrid.razor", Packet.PS1b),
        new("Components/Pages/Database/RowPresentation.cs", Packet.PS1b),
        new("Components/Pages/Database/RowReferencesPanel.razor", Packet.PS1b),
        new("Components/Pages/Database/RowToolbar.razor", Packet.PS1b),
        new("Components/Pages/Database/RowValue.razor", Packet.PS1b),
        new("Components/Pages/Database/Sessions.razor", Packet.PS1b),
        new("Components/Pages/Database/TableRowsTab.razor", Packet.PS1b),
        new("Components/Pages/Documents/CollectionChip.razor", Packet.PS1b, "Components/Shared/CollectionChip.razor"),
        new("Components/Pages/Documents/SafeReturnLink.cs", Packet.PS1b, "Components/Shared/SafeReturnLink.cs"),
        new("Components/Pages/Schema/SqlActions.razor", Packet.PS1b, "Components/Shared/SqlActions.razor"),
        new("Components/Pages/Schema/SqlText.razor", Packet.PS1b, "Components/Shared/SqlText.razor"),
        new("Internal/Sql/ColumnCatalog.cs", Packet.PS1b),
        new("Internal/Sql/CountEstimator.cs", Packet.PS1b),
        new("Internal/Sql/DatabaseCatalogQueries.cs", Packet.PS1b),
        new("Internal/Sql/ReadOnlySqlGuard.cs", Packet.PS1b),
        new("Internal/Sql/ReadOnlySqlSession.cs", Packet.PS1b),
        new("Internal/Sql/SqlIdentifier.cs", Packet.PS1b),
        new("Internal/Sql/SqlLexer.cs", Packet.PS1b),
        new("Internal/Sql/SqlLiteralText.cs", Packet.PS1b),
        new("Internal/Sql/SqlValueFormatter.cs", Packet.PS1b),
        new("Internal/Sql/StudioDatabaseTime.cs", Packet.PS1b),
        new("Internal/Sql/TableRowQueryBuilder.cs", Packet.PS1b),
        new("Services/CollectionColorizer.cs", Packet.PS1b),
        new("Services/Database/BrowsableSchemaMatcher.cs", Packet.PS1b),
        new("Services/Database/DatabaseAccess.cs", Packet.PS1b),
        new("Services/Database/DatabaseBrowserConfigurationNotice.cs", Packet.PS1b),
        new("Services/Database/DatabaseBrowserState.cs", Packet.PS1b),
        new("Services/Database/DatabaseCatalog.cs", Packet.PS1b),
        new("Services/Database/DatabaseGate.cs", Packet.PS1b),
        new("Services/Database/DatabaseLinks.cs", Packet.PS1b),
        new("Services/Database/DatabaseModels.cs", Packet.PS1b),
        new("Services/Database/DatabaseObjectAssembler.cs", Packet.PS1b),
        new("Services/Database/DatabaseObjectClassifier.cs", Packet.PS1b),
        new("Services/Database/DatabaseObjectService.cs", Packet.PS1b),
        new("Services/Database/IDatabaseObjectService.cs", Packet.PS1b),
        new("Services/Database/ISessionActivityService.cs", Packet.PS1b),
        new("Services/Database/ITableRowService.cs", Packet.PS1b),
        new("Services/Database/PgVector.cs", Packet.PS1b),
        new("Services/Database/PostgresErrorText.cs", Packet.PS1b),
        new("Services/Database/RowFilterGrammar.cs", Packet.PS1b),
        new("Services/Database/SessionActivityModels.cs", Packet.PS1b),
        new("Services/Database/SessionActivityReader.cs", Packet.PS1b),
        new("Services/Database/SessionActivityService.cs", Packet.PS1b),
        new("Services/Database/SessionLockLabels.cs", Packet.PS1b),
        new("Services/Database/SessionPoolObserver.cs", Packet.PS1b),
        new("Services/Database/SessionPoolSettings.cs", Packet.PS1b),
        new("Services/Database/SessionQueryText.cs", Packet.PS1b),
        new("Services/Database/TableRowModels.cs", Packet.PS1b),
        new("Services/Database/TableRowService.cs", Packet.PS1b),
        new("Services/Database/WithheldNames.cs", Packet.PS1b),
        new("Services/Documents/SearchChip.cs", Packet.PS1b, "Services/Query/SearchChip.cs"),
        new("Services/Query/IndexVerdict.cs", Packet.PS1b),
        new("Services/Query/SearchGrammarError.cs", Packet.PS1b),
        new("Services/Query/SortDirection.cs", Packet.PS1b),
        new("Services/Schema/SchemaDeclarations.cs", Packet.PS1b),
        new("Services/Schema/SchemaFormat.cs", Packet.PS1b),
        new("Services/Schema/SqlTokenizer.cs", Packet.PS1b),

        // ---- PS1c --------------------------------------------------------
        new("Components/Pages/Health/HealthPage.razor", Packet.PS1c),
        new("Components/Pages/Health/HealthPresentation.cs", Packet.PS1c),
        new("Internal/Sql/HealthConnectionQueries.cs", Packet.PS1c),
        new("Internal/Sql/HealthHorizonQueries.cs", Packet.PS1c),
        new("Internal/Sql/HealthQuery.cs", Packet.PS1c),
        new("Internal/Sql/HealthServerQueries.cs", Packet.PS1c),
        new("Internal/Sql/HealthStatementQueries.cs", Packet.PS1c),
        new("Internal/Sql/HealthTableQueries.cs", Packet.PS1c),
        new("Services/Health/Checks/ChecksumFailuresCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/ConnectionThresholds.cs", Packet.PS1c),
        new("Services/Health/Checks/DangerousSettingsCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/DatabaseCounterCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/ExpensiveStatementsCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/HealthCheckSeams.cs", Packet.PS1c),
        new("Services/Health/Checks/HorizonFacts.cs", Packet.PS1c),
        new("Services/Health/Checks/HorizonHeldCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/HotUpdateCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/IdleInTransactionCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/InvalidIndexCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/JitShareCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/LockWaitCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/LongRunningStatementCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/MultixactAgeCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/PoolSaturationCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/PostgresEndOfLifeCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/PreparedTransactionCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/RedundantIndexCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/ReplicationSlotCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/ReplicationSlots.cs", Packet.PS1c),
        new("Services/Health/Checks/SequentialScansCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/ServerConnectionHeadroomCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/StaleStatisticsCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/StatementReads.cs", Packet.PS1c),
        new("Services/Health/Checks/StatementTextCache.cs", Packet.PS1c),
        new("Services/Health/Checks/TableFacts.cs", Packet.PS1c),
        new("Services/Health/Checks/TransactionIdAgeCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/UnusedIndexCheck.cs", Packet.PS1c),
        new("Services/Health/Checks/VacuumBehindCheck.cs", Packet.PS1c),
        new("Services/Health/Export/HealthExportRunner.cs", Packet.PS1c),
        new("Services/Health/Export/HealthExportSummary.cs", Packet.PS1c),
        new("Services/Health/Export/MartenStudioHealthCheck.cs", Packet.PS1c, "Services/Health/Export/StudioHealthCheck.cs"),
        new("Services/Health/HealthCheck.cs", Packet.PS1c),
        new("Services/Health/HealthCheckContext.cs", Packet.PS1c),
        new("Services/Health/HealthCheckRegistry.cs", Packet.PS1c),
        new("Services/Health/HealthFinding.cs", Packet.PS1c),
        new("Services/Health/HealthHistory.cs", Packet.PS1c),
        new("Services/Health/HealthLinks.cs", Packet.PS1c),
        new("Services/Health/HealthModel.cs", Packet.PS1c),
        new("Services/Health/HealthMutes.cs", Packet.PS1c),
        new("Services/Health/HealthReport.cs", Packet.PS1c),
        new("Services/Health/HealthService.cs", Packet.PS1c),
        new("Services/Health/HealthSweepCache.cs", Packet.PS1c),
        new("Services/Health/HealthSweeper.cs", Packet.PS1c),
        new("Services/Health/PostgresPlatform.cs", Packet.PS1c),
        new("Services/Health/StudioRuntime.cs", Packet.PS1c),
        new("Services/Query/ExplainPlanScans.cs", Packet.PS1c),
        new("Services/Schema/IndexRedundancy.cs", Packet.PS1c),
        new("Services/Schema/StorageParameters.cs", Packet.PS1c),

        // ---- PS1d --------------------------------------------------------
        new("Components/Pages/Activity.razor", Packet.PS1d),
        new("Components/Pages/Query/ExamplesPanel.razor", Packet.PS1d),
        new("Components/Pages/Query/QueryEditor.razor", Packet.PS1d),
        new("Components/Pages/Query/QueryStats.razor", Packet.PS1d),
        new("Components/Pages/Query/ResultGrid.razor", Packet.PS1d),
        new("Components/Pages/Query/SavedQueryDrawer.razor", Packet.PS1d),
        new("Components/Pages/Query/SqlConsolePanel.razor", Packet.PS1d),
        new("Components/Pages/QueryPage.razor", Packet.PS1d),
        new("Components/Pages/Relationships/GraphDiagram.razor", Packet.PS1d),
        new("Components/Pages/Relationships/NeighbourhoodGraph.razor", Packet.PS1d),
        new("Components/Pages/Relationships/RelationshipGraph.razor", Packet.PS1d),
        new("Components/Pages/Relationships/RelationshipLegend.razor", Packet.PS1d),
        new("Components/Pages/Relationships/Relationships.razor", Packet.PS1d),
        new("Components/Pages/Relationships/RelationshipTable.razor", Packet.PS1d),
        new("Components/Pages/Relationships/TableChip.razor", Packet.PS1d),
        new("Components/Pages/StudioHome.razor", Packet.PS1d),
        new("Internal/Sql/RelationshipQueries.cs", Packet.PS1d),
        new("Services/Live/NavIndicatorService.cs", Packet.PS1d),
        new("Services/Live/StudioLiveUpdates.cs", Packet.PS1d),
        new("Services/Live/StudioLiveUpdatesFactory.cs", Packet.PS1d),
        new("Services/Live/StudioSnapshotCache.cs", Packet.PS1d),
        new("Services/Query/QueryExport.cs", Packet.PS1d),
        new("Services/Query/QuerySavedQueries.cs", Packet.PS1d),
        new("Services/Query/QuerySqlErrors.cs", Packet.PS1d),
        new("Services/Query/SqlConsoleModels.cs", Packet.PS1d),
        new("Services/Query/SqlConsoleService.cs", Packet.PS1d),
        new("Services/Relationships/GraphDiagramModel.cs", Packet.PS1d),
        new("Services/Relationships/GraphLayout.cs", Packet.PS1d),
        new("Services/Relationships/IRelationshipDataService.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipDataService.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipDiagram.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipGraphBuilder.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipModels.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipReads.cs", Packet.PS1d),
        new("Services/Relationships/RelationshipView.cs", Packet.PS1d),
        new("Services/Sources/INavIndicatorContributor.cs", Packet.PS1d),
        new("Services/Sources/IRelationshipNodeSource.cs", Packet.PS1d),

        // ---- PS1e --------------------------------------------------------
        new("Components/Json/JsonCopyMenu.razor", Packet.PS1e),
        new("Components/Json/JsonDiff.razor", Packet.PS1e),
        new("Components/Json/JsonEditor.razor", Packet.PS1e),
        new("Components/Json/JsonRawView.razor", Packet.PS1e),
        new("Components/Json/JsonSearchBar.razor", Packet.PS1e),
        new("Components/Json/JsonView.razor", Packet.PS1e),
        new("Components/Json/JsonViewMoreRow.razor", Packet.PS1e),
        new("Components/Json/JsonViewRow.razor", Packet.PS1e),
        new("Components/Json/JsonViewToolbar.razor", Packet.PS1e),
        new("Components/Json/RoundTripDiff.razor", Packet.PS1e),
        new("Components/Shared/ConfirmDialog.razor", Packet.PS1e),
        new("Components/Shared/CopyBadge.razor", Packet.PS1e),
        new("Components/Shared/CopyButton.razor", Packet.PS1e),
        new("Components/Shared/DisplayValueHelper.cs", Packet.PS1e),
        new("Components/Shared/ErrorAlert.razor", Packet.PS1e),
        new("Components/Shared/KeysetPager.razor", Packet.PS1e),
        new("Components/Shared/LoadingSpinner.razor", Packet.PS1e),
        new("Components/Shared/MenuButton.razor", Packet.PS1e),
        new("Components/Shared/Pagination.razor", Packet.PS1e),
        new("Components/Shared/SearchFilter.razor", Packet.PS1e),
        new("Components/Shared/SqlCellFormat.cs", Packet.PS1e),
        new("Components/Shared/SqlCellView.razor", Packet.PS1e),
        new("Components/Shared/SqlDisclosure.razor", Packet.PS1e),
        new("Components/Shared/StatCard.razor", Packet.PS1e),
        new("Components/Shared/StateIndicator.razor", Packet.PS1e),
        new("Components/Shared/TableScroll.razor", Packet.PS1e),
        new("Components/Shared/TimeAgo.razor", Packet.PS1e),
        new("Components/Shared/ToastHost.razor", Packet.PS1e),
        new("Components/Shared/VectorIndexSummary.razor", Packet.PS1e),
        new("Services/Json/CSharpRecordGenerator.cs", Packet.PS1e),
        new("Services/Json/JsonCanonicalizer.cs", Packet.PS1e),
        new("Services/Json/JsonModel.cs", Packet.PS1e),
        new("Services/Json/JsonModelBuilder.cs", Packet.PS1e),
        new("Services/Json/JsonPathExpressions.cs", Packet.PS1e),
        new("Services/Json/JsonPrettyPrinter.cs", Packet.PS1e),
        new("Services/Json/JsonSearch.cs", Packet.PS1e),
        new("Services/Json/RoundTripDiffer.cs", Packet.PS1e),
        new("Services/PostgresFailure.cs", Packet.PS1e),
        new("Services/ToastService.cs", Packet.PS1e),
        new("Services/WritePolicyRefusal.cs", Packet.PS1e),
        new("wwwroot/css/marten-studio.css", Packet.PS1e, "wwwroot/css/studio.css"),
        new("wwwroot/js/marten-studio.js", Packet.PS1e, "wwwroot/js/studio.js"),
    ];
}
