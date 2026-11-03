namespace PostgresStudio.Services.Connection;

/// <summary>
/// The process environment as the connection reader sees it: one variable by name, never an enumeration.
/// </summary>
/// <remarks>
/// A seam so the reader's provenance rules are testable with a stated environment. The reader asks only for the
/// variables <see cref="NpgsqlSettingsCatalog" /> lists (<c>PGUSER</c>, <c>PGPASSWORD</c>, …), and a value it is asked
/// for is shown only on the terms of the keyword the variable stands in for (NP-DESIGN §2.3): <c>PGPASSWORD</c> is
/// reported as set, never read out.
/// </remarks>
internal interface IProcessEnvironment
{
    /// <summary>The variable's value, or <see langword="null" /> when it is not set.</summary>
    /// <param name="name">The variable's name.</param>
    string? Get(string name);
}

/// <summary>The real environment: <see cref="Environment.GetEnvironmentVariable(string)" />.</summary>
internal sealed class ProcessEnvironment : IProcessEnvironment
{
    /// <summary>The one instance.</summary>
    public static ProcessEnvironment Instance { get; } = new();

    /// <inheritdoc />
    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Environment.GetEnvironmentVariable(name);
    }
}
