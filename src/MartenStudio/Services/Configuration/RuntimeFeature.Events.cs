using System.Reflection;

using Marten.Events;

namespace MartenStudio.Services.Configuration;

/// <summary>The event-store features the event health checks light up on.</summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// <c>opts.Events.DcbTagVersionFillFactor</c>, the heap fillfactor of <c>mt_dcb_tag_version</c> - Marten 9.47.0,
    /// <c>src/Marten/Events/EventGraph.cs</c> and <c>IEventStoreOptions.cs</c> (#5622; absent at V9.46.0). Weasel
    /// 9.42's <c>WithFillFactor</c> is what lets the schema delta manage it.
    /// </summary>
    public static readonly RuntimeFeature DcbTagVersionFillFactor = new(
        nameof(DcbTagVersionFillFactor),
        RuntimePackage.Marten,
        "9.47.0",
        "9.47.0",
        "The heap fillfactor of the DCB tag-version table, declared with Events.DcbTagVersionFillFactor and managed by the schema delta.",
        static () => typeof(EventGraph).GetProperty(
            "DcbTagVersionFillFactor", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var type } && type == typeof(int?));
}
