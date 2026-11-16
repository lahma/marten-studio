using Microsoft.Extensions.DependencyInjection;

namespace DataStudio;

/// <summary>
/// What <c>AddDataStudio()</c> returns: the shell's registration, to which every installed module adds itself with its own
/// <c>Add…()</c> call (<c>AddPostgres()</c>, <c>AddMarten()</c>, <c>AddKafka()</c>) - each an extension of this type in the <c>DataStudio</c>
/// namespace, so one <c>using DataStudio;</c> lights up every module the application has installed.
/// </summary>
public sealed class DataStudioBuilder
{
    internal DataStudioBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The application's service collection, for a registration of the host's own that belongs beside the studio's.</summary>
    public IServiceCollection Services { get; }
}
