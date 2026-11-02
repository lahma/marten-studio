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
        await Verify(PublicApi(typeof(PostgresStudioOptions).Assembly), extension: "txt")
            .UseDirectory("Verify")
            .UseFileName("PublicApiTest_PostgresStudio");
    }

    /// <summary>
    /// The diagnostics engine's own baseline (0.11-C): the types that moved out of <c>PostgresStudio.dll</c> with the engine,
    /// and the two the engine adds - <c>PostgresDiagnosticsOptions</c>, the base of <c>PostgresStudioOptions</c>, and
    /// <c>AddPostgresDiagnostics</c>. Read like the other: a diff is deliberate, never accepted by hand.
    /// </summary>
    [Fact]
    public async Task The_diagnostics_public_api_has_not_changed_unintentionally()
    {
        await Verify(PublicApi(typeof(PostgresDiagnosticsOptions).Assembly), extension: "txt")
            .UseDirectory("Verify")
            .UseFileName("PublicApiTest_PostgresStudioDiagnostics");
    }

    private static string PublicApi(System.Reflection.Assembly assembly)
    {
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

        return assembly.GeneratePublicApi(options);
    }

    /// <summary>
    /// The thirty-six public types of the two assemblies, by name, so an extra one fails with its name rather than a diff:
    /// the studio's five (its options, capabilities, resource, registration and mapping) and the engine's thirty-one - the
    /// registration, the options it shares as a base, the six inbound types, the twenty of the diagnostics read model (API
    /// design §5.1, and issue #31's <c>PostgresWalSection</c>), the two that link into the studio (issue #24) and the
    /// health-check export (0.11-C).
    /// </summary>
    [Fact]
    public void The_public_types_are_the_registration_types_the_inbound_types_and_the_diagnostics_read_model()
    {
        Exported(typeof(PostgresStudioOptions).Assembly).Should().Equal(
            "PostgresStudio.PostgresStudioCapabilities",
            "PostgresStudio.PostgresStudioEndpointRouteBuilderExtensions",
            "PostgresStudio.PostgresStudioOptions",
            "PostgresStudio.PostgresStudioResource",
            "PostgresStudio.PostgresStudioServiceCollectionExtensions");

        Exported(typeof(PostgresDiagnosticsOptions).Assembly).Should().Equal(
            "PostgresStudio.IPostgresCapacitySource",
            "PostgresStudio.IPostgresDatabaseSource",
            "PostgresStudio.IPostgresDiagnostics",
            "PostgresStudio.PostgresActionKind",
            "PostgresStudio.PostgresCheck",
            "PostgresStudio.PostgresComputeCapacity",
            "PostgresStudio.PostgresConnectionCounts",
            "PostgresStudio.PostgresConnectionsSection",
            "PostgresStudio.PostgresDatabaseRef",
            "PostgresStudio.PostgresDiagnosticsOptions",
            "PostgresStudio.PostgresDiagnosticsRequest",
            "PostgresStudio.PostgresDiagnosticsServiceCollectionExtensions",
            "PostgresStudio.PostgresDiagnosticsSnapshot",
            "PostgresStudio.PostgresExtension",
            "PostgresStudio.PostgresFinding",
            "PostgresStudio.PostgresFindingCategory",
            "PostgresStudio.PostgresFindingGroup",
            "PostgresStudio.PostgresPlatformKind",
            "PostgresStudio.PostgresReadOutcome",
            "PostgresStudio.PostgresServerInfo",
            "PostgresStudio.PostgresSetting",
            "PostgresStudio.PostgresSettingsSection",
            "PostgresStudio.PostgresSeverity",
            "PostgresStudio.PostgresStatement",
            "PostgresStudio.PostgresStatementsSection",
            "PostgresStudio.PostgresStorageSection",
            "PostgresStudio.PostgresStudioHealthChecksBuilderExtensions",
            "PostgresStudio.PostgresStudioLink",
            "PostgresStudio.PostgresStudioPage",
            "PostgresStudio.PostgresTableOwnership",
            "PostgresStudio.PostgresTableStorage",
            "PostgresStudio.PostgresWalSection");
    }

    private static string[] Exported(System.Reflection.Assembly assembly) =>
    [
        .. assembly.GetExportedTypes()
            .Where(static type => !typeof(IComponent).IsAssignableFrom(type) && type.Name != "_Imports")
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal),
    ];
}
