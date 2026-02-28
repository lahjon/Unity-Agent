namespace AgenticEngine.Constants
{
    /// <summary>
    /// Centralised magic-number constants used across the application.
    /// Keeping them here makes behaviour tunable and self-documenting.
    /// </summary>
    public static class AppConstants
    {
        // ── TaskLauncher ────────────────────────────────────────────

        /// <summary>Number of lines from the end of output to scan for status markers.</summary>
        public const int MaxOutputTailLines = 50;

        /// <summary>Maximum character length of the output tail used for token-limit detection.</summary>
        public const int MaxOutputCharLength = 3000;

        /// <summary>Maximum character length for the short project description.</summary>
        public const int MaxShortDescriptionLength = 200;

        /// <summary>Lines before a recommendation header to search for completion signals.</summary>
        public const int RecommendationContextBefore = 20;

        /// <summary>Lines after a recommendation header to search for completion signals.</summary>
        public const int RecommendationContextAfter = 15;

        // ── Terminal ────────────────────────────────────────────────

        /// <summary>Byte-buffer size for reading ConPTY output.</summary>
        public const int TerminalBufferSize = 4096;

        /// <summary>Maximum number of scrollback lines kept by the VT screen buffer.</summary>
        public const int ScrollbackLimit = 5000;

        // ── MainWindow ──────────────────────────────────────────────

        /// <summary>Interval (in seconds) for the status-refresh timer.</summary>
        public const int StatusTimerIntervalSeconds = 3;
    }
}
