namespace MartenStudio.Tests.Conventions;

public partial class CoreFenceTests
{
    /// <summary>
    /// PS-1c's files - the health engine (registry, sweeper, cache, history, gates, mutes, the export), the 23 core-bound
    /// checks with their facts files and SQL builders, and the Health page - name no Marten-family type and no type
    /// MartenStudio keeps.
    /// </summary>
    [Fact]
    public void Health() => AssertFenced(CoreManifest.Packet.PS1c);
}
