namespace PostgresStudio.Services.Database;

/// <summary>
/// The visitor's half of <see cref="StatementScreen" />: the screen read off the database browser's gate, which is the
/// UI's (<see cref="DatabaseGate" />), where the engine's own half knows no visitor.
/// </summary>
internal static class StatementScreenGate
{
    extension(StatementScreen)
    {
        /// <summary>One visitor's screen, from the database browser's gate as <c>DatabaseAccess.GateAsync</c> read it.</summary>
        /// <param name="read">The gate, or <see langword="null" /> when it could not be asked.</param>
        /// <param name="hidesDocumentTypes">Whether <c>MartenStudioOptions.IsDocumentTypeVisible</c> is set at all.</param>
        public static StatementScreen Of(DatabaseGateRead? read, bool hidesDocumentTypes) =>
            read is not null && read.Succeeded
                ? new StatementScreen(read.Gate.WithheldSchemas, read.Gate.Classifier, hidesDocumentTypes, read.Gate.RefusedStoreDocumentTables())
                : StatementScreen.Unchecked(hidesDocumentTypes);
    }
}
