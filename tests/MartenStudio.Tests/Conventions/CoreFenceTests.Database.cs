namespace MartenStudio.Tests.Conventions;

public partial class CoreFenceTests
{
    /// <summary>
    /// PS-1b's files - the database browser's services and screens, Sessions, the catalog and row SQL, the schema
    /// declaration models and the shared SQL text helpers - name no Marten-family type and no type MartenStudio keeps.
    /// </summary>
    [Fact]
    public void Database() => AssertFenced(CoreManifest.Packet.PS1b);
}
