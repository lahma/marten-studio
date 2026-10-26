namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The shared studio is an assembly of its own, and the reference runs one way: MartenStudio references it, never the
/// reverse. Everything that reads "both assemblies" stands on that. The other direction - that PostgresStudio and its
/// suite reach no Marten at all - is <c>PostgresStudioFenceTests</c>, in PostgresStudio.Tests, the one project that can
/// check its own output folder for it.
/// </summary>
public class SharedStudioReferenceTests
{
    [Fact]
    public void The_shared_studio_is_an_assembly_of_its_own_that_MartenStudio_references()
    {
        StudioProjects.SharedAssembly.Should().NotBeSameAs(StudioProjects.MartenAssembly);
        StudioProjects.SharedAssembly.GetName().Name.Should().Be(StudioProjects.Shared);
        StudioProjects.MartenAssembly.GetName().Name.Should().Be(StudioProjects.Marten);

        StudioProjects.MartenAssembly.GetReferencedAssemblies().Select(static x => x.Name)
            .Should().Contain(StudioProjects.Shared, "MartenStudio is built on the shared studio");

        StudioProjects.SharedAssembly.GetReferencedAssemblies().Select(static x => x.Name)
            .Should().NotContain(StudioProjects.Marten, "the reference runs one way");
    }
}
