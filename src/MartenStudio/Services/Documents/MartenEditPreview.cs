using System.Reflection;
using System.Runtime.ExceptionServices;

using Marten;

using MartenStudio.Services.Configuration;

using PostgresStudio.Services;
using PostgresStudio.Services.Json;

namespace MartenStudio.Services.Documents;

/// <summary>What Marten's own preview of a save said about an edit, beside the studio's round trip.</summary>
internal enum MartenPreviewVerdict
{
    /// <summary>This Marten has no preview of its own (below 9.53), or the store does not offer one.</summary>
    Unavailable,

    /// <summary>Marten would keep exactly what the studio's round trip keeps.</summary>
    Agrees,

    /// <summary>Marten would keep something else; <see cref="MartenPreviewCheck.Differences" /> says what.</summary>
    Disagrees,

    /// <summary>Marten's preview refused the edit, as its own save would; <see cref="MartenPreviewCheck.Reason" /> says why.</summary>
    Refused,
}

/// <summary>
/// Marten's own answer to "what would saving this edit keep", laid beside the studio's round trip (D7).
/// </summary>
/// <remarks>
/// Advisory, never the gate: the save is still the studio's (<c>DocumentWriteService.SaveAsync</c>), and its own
/// refusals still decide. What this adds is a second opinion from the code that would run a save through Marten's
/// diagnostics writer - so a disagreement is something to read before pressing Save, not a refusal.
/// </remarks>
internal sealed record MartenPreviewCheck
{
    /// <summary>What Marten's preview said.</summary>
    public required MartenPreviewVerdict Verdict { get; init; }

    /// <summary>The JSON Marten would keep, when it answered.</summary>
    public string? MartenJson { get; init; }

    /// <summary>The studio's round trip against Marten's answer: empty when they agree.</summary>
    public JsonDiffResult Differences { get; init; } = JsonDiffResult.Empty;

    /// <summary>Why Marten refused, or why there is no answer, for the dialog.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the dialog has to warn: Marten keeps something else, or would refuse the save.</summary>
    public bool IsDisagreement => Verdict is MartenPreviewVerdict.Disagrees or MartenPreviewVerdict.Refused;
}

/// <summary>
/// Calls Marten's <c>IDocumentStoreDiagnosticsWriter.PreviewDocumentJsonAsync</c> (9.53+), by reflection because the
/// floor's JasperFx has no such interface, and compares its answer with the studio's round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is read or written.</b> Marten's implementation (v9.53.0, unchanged at v9.54.2) resolves the type name in
/// memory, deserializes the edit with the store's own serializer, refuses an id in the JSON that differs from the
/// requested one, and serializes the document back: no session, no connection, no <c>EnsureStorageExistsAsync</c>
/// (hard rule 14), and no tenant or database (hard rule 15). <see cref="RuntimeFeature.DocumentEditPreview" /> is the
/// probe; below it, the answer is <see cref="MartenPreviewVerdict.Unavailable" /> with the Marten it needs.
/// </para>
/// <para>
/// <b>The type is named by its full CLR name</b> - the concrete type the row's <c>mt_doc_type</c> names, for a hierarchy,
/// which Marten's resolver finds among the root's subclasses and deserializes as itself, exactly as the studio's round
/// trip does.
/// </para>
/// </remarks>
internal static class MartenEditPreview
{
    /// <summary>The longest refusal the dialog prints; a serializer's message can carry a fragment of the document.</summary>
    internal const int MaxReasonLength = 600;

    private static readonly Lazy<Members?> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's Marten has its own preview, implemented, and the studio can call it.</summary>
    public static bool IsAvailable => Resolved.Value is not null;

    /// <summary>What the dialog says where Marten has no preview of its own.</summary>
    public static string RequirementSentence =>
        "Marten's own preview of a save (PreviewDocumentJsonAsync) needs "
        + RuntimeFeature.DocumentEditPreview.Requirement
        + "; this host runs "
        + MartenRuntime.Current.Describe(RuntimePackage.Marten)
        + ".";

    /// <summary>
    /// Asks Marten what saving <paramref name="editedJson" /> as <paramref name="documentType" /> would keep, and
    /// compares it with <paramref name="studioRoundTrip" />.
    /// </summary>
    /// <param name="store">The store the document belongs to.</param>
    /// <param name="documentType">The concrete CLR type the studio's round trip used.</param>
    /// <param name="id">The document's id, as the studio addresses it.</param>
    /// <param name="editedJson">The edit.</param>
    /// <param name="studioRoundTrip">What the studio's round trip produced from the same edit.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    public static async Task<MartenPreviewCheck> CheckAsync(
        IDocumentStore store,
        Type documentType,
        string id,
        string editedJson,
        string studioRoundTrip,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(editedJson);
        ArgumentNullException.ThrowIfNull(studioRoundTrip);

        if (Resolved.Value is not { } members)
        {
            return new MartenPreviewCheck { Verdict = MartenPreviewVerdict.Unavailable, Reason = RequirementSentence };
        }

        if (!members.Writer.IsInstanceOfType(store))
        {
            return new MartenPreviewCheck
            {
                Verdict = MartenPreviewVerdict.Unavailable,
                Reason = "This store does not offer Marten's own preview of a save.",
            };
        }

        string martenJson;

        try
        {
            object request = members.Request.Invoke([documentType.FullName ?? documentType.Name, id, editedJson]);
            var task = (Task<string>) members.Preview.Invoke(store, [request, cancellationToken])!;

            martenJson = await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner)
        {
            if (inner is OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
            }

            return Refused(inner);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Refused(exception);
        }

        JsonDiffResult differences = RoundTripDiffer.Diff(studioRoundTrip, martenJson);

        return new MartenPreviewCheck
        {
            Verdict = differences.IsEmpty ? MartenPreviewVerdict.Agrees : MartenPreviewVerdict.Disagrees,
            MartenJson = martenJson,
            Differences = differences,
        };
    }

    private static MartenPreviewCheck Refused(Exception exception)
    {
        // Marten's words can carry what the visitor typed - the edited JSON, its id - so they reach the dialog escaped and
        // bounded, as error words reach every refusal (H1; INT-W1), then cut to the dialog's own length.
        string message = StudioLogValue.CapText(exception.Message) ?? string.Empty;

        if (message.Length > MaxReasonLength)
        {
            message = message[..MaxReasonLength] + "…";
        }

        return new MartenPreviewCheck
        {
            Verdict = MartenPreviewVerdict.Refused,
            Reason = exception.GetType().Name + ": " + message,
        };
    }

    private static Members? Resolve()
    {
        try
        {
            if (!RuntimeFeature.DocumentEditPreview.IsAvailable(MartenRuntime.Current)
                || RuntimeFeature.DiagnosticsWriterType() is not { } writer
                || RuntimeFeature.EditPreviewMethod() is not { } preview
                || preview.ReturnType != typeof(Task<string>))
            {
                return null;
            }

            ConstructorInfo? request = preview.GetParameters()[0].ParameterType.GetConstructor(
                [typeof(string), typeof(string), typeof(string)]);

            return request is null ? null : new Members(writer, preview, request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private sealed record Members(Type Writer, MethodInfo Preview, ConstructorInfo Request);
}
