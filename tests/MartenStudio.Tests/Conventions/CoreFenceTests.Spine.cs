namespace MartenStudio.Tests.Conventions;

public partial class CoreFenceTests
{
    /// <summary>
    /// PS-1a's files - the settings, the descriptor, the sources, the scope, authorization and audit services, the
    /// shell, the routes and the layout - name no Marten-family type and no type MartenStudio keeps.
    /// </summary>
    [Fact]
    public void Spine() => AssertFenced(CoreManifest.Packet.PS1a);
}
