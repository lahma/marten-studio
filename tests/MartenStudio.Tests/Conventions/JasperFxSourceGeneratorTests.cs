using System.Reflection;

using JasperFx.Events;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The shipped assembly is compiled without the JasperFx aggregate source generator Marten bundles, and
/// the test assemblies are compiled with it.
/// </summary>
/// <remarks>
/// <para>
/// From JasperFx.Events 2.75 (Marten 9.40) the generator emits
/// <c>[assembly: JasperFx.Events.Aggregation.JasperFxSourceGeneratorApplied]</c> into every assembly it
/// processes, unconditionally. On <c>MartenStudio.dll</c> that would be a JasperFx attribute on the
/// shipped assembly whenever it was built at a newer Marten than the floor, and a line in the public API
/// baseline that is present on one CI leg and absent on the other. The
/// <c>MartenStudioWithoutTheJasperFxSourceGenerator</c> target in <c>src/MartenStudio/MartenStudio.csproj</c>
/// takes the generator out of that one compilation - <c>ExcludeAssets="analyzers"</c> was measured not to
/// (AGENTS.md, <em>Package budget changes</em>, 2026-10-15).
/// </para>
/// <para>
/// The second fact is the anti-vacuity half. The test projects need the generator (the 9.22.4 - 9.23.0
/// "no source-generated dispatcher" wall), and an assembly that carries the marker is proof the check in
/// the first fact can see one. At the floor, JasperFx.Events 2.74.0 has no marker type at all, so the
/// second fact asserts that instead - and fails if the marker exists while the test assembly lacks it,
/// which would mean the target reached further than this one project.
/// </para>
/// </remarks>
public class JasperFxSourceGeneratorTests
{
    private const string MarkerAttribute = "JasperFx.Events.Aggregation.JasperFxSourceGeneratorAppliedAttribute";

    [Fact]
    public void The_shipped_assembly_is_compiled_without_the_JasperFx_source_generator()
    {
        var studio = typeof(MartenStudioOptions).Assembly;

        CarriesMarker(studio).Should().BeFalse(
            "MartenStudio.dll declares no aggregate and no projection; the only thing the generator would put " +
            "in it is a JasperFx attribute on the shipped assembly. See MartenStudioWithoutTheJasperFxSourceGenerator " +
            "in src/MartenStudio/MartenStudio.csproj");
    }

    [Fact]
    public void The_test_assembly_keeps_the_generator()
    {
        var events = typeof(IEvent).Assembly;
        var marker = events.GetType(MarkerAttribute);

        if (marker is null)
        {
            events.GetName().Version.Should().BeLessThan(new Version(2, 75),
                "JasperFx.Events has stamped JasperFxSourceGeneratorAppliedAttribute since 2.75; a newer " +
                "JasperFx.Events without it means the marker moved, and the first fact would be checking for " +
                "a name that no longer exists");
            return;
        }

        CarriesMarker(typeof(JasperFxSourceGeneratorTests).Assembly).Should().BeTrue(
            "the test projects declare projections of their own (TimeTravelSummaryProjection) and must be " +
            "compiled the way a host's project is, generator included, so the removal target in " +
            "MartenStudio.csproj must not reach them");
    }

    private static bool CarriesMarker(Assembly assembly) =>
        assembly.GetCustomAttributesData().Any(x => x.AttributeType.FullName == MarkerAttribute);
}
