using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MartenStudio.Tests.Support;

/// <summary>An <see cref="IHostEnvironment" /> that is only its name, for the header's environment badge.</summary>
internal sealed class NamedHostEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "MartenStudio.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
