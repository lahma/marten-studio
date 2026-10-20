namespace MartenStudio.Services.Query;

/// <summary>Something a filter parser could not make sense of, and where it is.</summary>
/// <remarks>
/// One shape for both grammars that report positions - the documents list's search box and the database browser's
/// row filter - so the strip that underlines a mistake draws either.
/// </remarks>
/// <param name="Message">What is wrong, phrased for the person who typed it.</param>
/// <param name="Position">Zero-based offset into the filter text.</param>
/// <param name="Length">How much of the text the message is about; at least one character.</param>
internal sealed record SearchGrammarError(string Message, int Position, int Length);
