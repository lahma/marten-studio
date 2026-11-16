namespace DataStudio.Services.Settings;

/// <summary>
/// The words the shell speaks in, given by the studio that registered it (<see cref="StudioModuleComposition.Compose" />): in phase B,
/// from the shell's own options; until then, from the database descriptor, which builds it.
/// </summary>
/// <param name="ProductName">The product's name in a sentence: the sidebar's accessible name and the rail's credit.</param>
/// <param name="ExposedContent">What a page served to anyone exposes, in the anonymous banner's clause, or <see langword="null" /> for none.</param>
/// <param name="Surface">The startup guard's name for the endpoints this studio maps.</param>
/// <param name="Exposure">The startup guard's lead sentence: what an unauthorized mapping would serve, and that the studio refuses to start.</param>
/// <param name="Remedies">The startup guard's list of the three ways to say which authorization was meant, one per line.</param>
internal sealed record StudioShellIdentity(string ProductName, string? ExposedContent, string Surface, string Exposure, string Remedies);
