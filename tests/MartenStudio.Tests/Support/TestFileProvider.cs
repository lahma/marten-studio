using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace MartenStudio.Tests.Support;

/// <summary>
/// A minimal in-memory <see cref="IFileProvider"/> for the endpoints that serve files out of the web
/// root, so those tests do not depend on a build having laid the static web assets out on disk.
/// </summary>
public sealed class TestFileProvider(Dictionary<string, byte[]> files) : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IFileInfo GetFileInfo(string subpath)
    {
        var normalized = subpath.Replace('\\', '/').TrimStart('/');
        return files.TryGetValue(normalized, out var content)
            ? new TestFileInfo(normalized, content)
            : new NotFoundFileInfo(subpath);
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class TestFileInfo(string name, byte[] content) : IFileInfo
    {
        public bool Exists => true;

        public long Length => content.Length;

        public string? PhysicalPath => null;

        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(content);
    }
}
