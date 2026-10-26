using Microsoft.AspNetCore.Components;

using PublicApiGenerator;

namespace PostgresStudio.Tests;

/// <summary>
/// Snapshots the whole public surface of the PostgresStudio package.
/// </summary>
/// <remarks>
/// <para>
/// The surface is the six registration types - three static classes, two classes and one record, mirroring
/// MartenStudio's - and the six inbound types a host implements or builds: <c>IPostgresDatabaseSource</c>,
/// <c>IPostgresCapacitySource</c>, <c>PostgresComputeCapacity</c>, <c>PostgresDatabaseRef</c>,
/// <c>PostgresServerInfo</c> and <c>PostgresPlatformKind</c> - and the diagnostics read model (D31, W5-API): the
/// <c>IPostgresDiagnostics</c> service, its request, its snapshot, findings and checks, the four sections and their rows, and
/// five enums. Anything else appearing in the baseline is a mistake rather than a feature (AGENTS.md hard rule 7, D12).
/// </para>
/// <para>
/// A failure here is not automatically a bug. Run the test, open the <c>.received.txt</c> beside the
/// <c>.verified.txt</c>, read the diff line by line, say in the pull request what moved and why, and only then accept
/// the new baseline. Never hand-edit the baseline.
/// </para>
/// </remarks>
public class PublicApiTest
{
    [Fact]
    public async Task The_public_api_has_not_changed_unintentionally()
    {
        var assembly = typeof(PostgresStudioOptions).Assembly;

        var options = new ApiGeneratorOptions
        {
            // The same generator options as MartenStudio's baseline, for the same reasons.
            ExcludeAttributes =
            [
                "System.Diagnostics.DebuggerDisplayAttribute",
                "System.Reflection.AssemblyMetadataAttribute",
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                "System.Runtime.CompilerServices.IsReadOnlyAttribute",
                "System.Runtime.CompilerServices.NullableAttribute",
                "System.Runtime.CompilerServices.NullableContextAttribute",
                "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
                "System.Runtime.Versioning.TargetFrameworkAttribute",
            ],

            TreatRecordsAsClasses = false,

            // D20: Blazor components are the studio's UI, not API anyone calls; _Imports is the @using list.
            ExcludeTypes = assembly.GetExportedTypes()
                .Where(static type => typeof(IComponent).IsAssignableFrom(type) || type.Name == "_Imports")
                .ToArray(),
        };

        await Verify(assembly.GeneratePublicApi(options), extension: "txt")
            .UseDirectory("Verify")
            .UseFileName("PublicApiTest_PostgresStudio");
    }

    /// <summary>
    /// The thirty-one public types, by name, so an extra one fails with its name rather than a diff: the six registration
    /// types, the six inbound types and the nineteen of the diagnostics read model (API design §5.1).
    /// </summary>
    [Fact]
    public void The_public_types_are_the_registration_types_the_inbound_types_and_the_diagnostics_read_model()
    {
        string[] exported =
        [
            .. typeof(PostgresStudioOptions).Assembly.GetExportedTypes()
                .Where(static type => !typeof(IComponent).IsAssignableFrom(type) && type.Name != "_Imports")
                .Select(static type => type.FullName!)
                .Order(StringComparer.Ordinal),
        ];

        exported.Should().Equal(
            "PostgresStudio.IPostgresCapacitySource",
            "PostgresStudio.IPostgresDatabaseSource",
            "PostgresStudio.IPostgresDiagnostics",
            "PostgresStudio.PostgresActionKind",
            "PostgresStudio.PostgresCheck",
            "PostgresStudio.PostgresComputeCapacity",
            "PostgresStudio.PostgresConnectionCounts",
            "PostgresStudio.PostgresConnectionsSection",
            "PostgresStudio.PostgresDatabaseRef",
            "PostgresStudio.PostgresDiagnosticsRequest",
            "PostgresStudio.PostgresDiagnosticsSnapshot",
            "PostgresStudio.PostgresExtension",
            "PostgresStudio.PostgresFinding",
            "PostgresStudio.PostgresFindingCategory",
            "PostgresStudio.PostgresPlatformKind",
            "PostgresStudio.PostgresReadOutcome",
            "PostgresStudio.PostgresServerInfo",
            "PostgresStudio.PostgresSetting",
            "PostgresStudio.PostgresSettingsSection",
            "PostgresStudio.PostgresSeverity",
            "PostgresStudio.PostgresStatement",
            "PostgresStudio.PostgresStatementsSection",
            "PostgresStudio.PostgresStorageSection",
            "PostgresStudio.PostgresStudioCapabilities",
            "PostgresStudio.PostgresStudioEndpointRouteBuilderExtensions",
            "PostgresStudio.PostgresStudioHealthChecksBuilderExtensions",
            "PostgresStudio.PostgresStudioOptions",
            "PostgresStudio.PostgresStudioResource",
            "PostgresStudio.PostgresStudioServiceCollectionExtensions",
            "PostgresStudio.PostgresTableOwnership",
            "PostgresStudio.PostgresTableStorage");
    }
}
