using Microsoft.AspNetCore.Components;

using PublicApiGenerator;

namespace MartenStudio.Tests;

/// <summary>
/// Snapshots the whole public surface of the shipped package.
/// </summary>
/// <remarks>
/// <para>
/// The surface is meant to be two static classes, two classes and one record (AGENTS.md hard rule 7).
/// Anything else appearing in the baseline is a mistake rather than a feature: everything a consumer can
/// bind to is everything this project has to keep working, and an internal data seam is what keeps that
/// small (D12).
/// </para>
/// <para>
/// A failure here is not automatically a bug. Run the test, open the <c>.received.txt</c> beside the
/// <c>.verified.txt</c>, read the diff line by line, say in the pull request what moved and why, and only
/// then accept the new baseline. Never hand-edit the baseline.
/// </para>
/// </remarks>
public class PublicApiTest
{
    [Fact]
    public async Task The_public_api_has_not_changed_unintentionally()
    {
        var assembly = typeof(MartenStudioOptions).Assembly;

        var options = new ApiGeneratorOptions
        {
            // These say how the compiler encoded something, not what the contract is, and they churn the
            // baseline whenever an unrelated file is touched.
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

            // A record is not a class: it brings value equality, a copy constructor and `with`, and
            // turning one into the other breaks callers without changing a single signature.
            TreatRecordsAsClasses = false,

            // D20. Blazor components are the studio's UI, not API anyone calls. The .razor compiler emits
            // the class - so it cannot be made internal - along with a BuildRenderTree override whose body
            // is the markup, which means every markup edit would otherwise land in this baseline as if it
            // were a contract change. _Imports is the same story one step further from being API: the
            // compiler emits a public class whose entire content is the @using list.
            ExcludeTypes = assembly.GetExportedTypes()
                .Where(static type => typeof(IComponent).IsAssignableFrom(type) || type.Name == "_Imports")
                .ToArray(),
        };

        await Verify(assembly.GeneratePublicApi(options), extension: "txt")
            .UseDirectory("Verify")
            .UseFileName("PublicApiTest_MartenStudio");
    }
}
