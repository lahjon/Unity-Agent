namespace HappyEngine.Constants
{
    /// <summary>
    /// Centralised magic-number constants used across the application.
    /// Keeping them here makes behaviour tunable and self-documenting.
    /// </summary>
    public static class AppConstants
    {
        // ── Task Services ────────────────────────────────────────────

        /// <summary>Number of lines from the end of output to scan for status markers.</summary>
        public const int MaxOutputTailLines = 50;

        /// <summary>Maximum character length of the output tail used for token-limit detection.</summary>
        public const int MaxOutputCharLength = 3000;

        /// <summary>Maximum character length for the short project description.</summary>
        public const int MaxShortDescriptionLength = 200;

        /// <summary>Maximum character length for the long project description (token optimization).</summary>
        public const int MaxLongDescriptionLength = 800;

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

        // ── Message Bus ──────────────────────────────────────────────

        /// <summary>Time-to-live for message bus messages in minutes before expiration.</summary>
        public const int MessageBusTTLMinutes = 60;

        /// <summary>Maximum number of messages to keep in the bus history.</summary>
        public const int MessageBusMaxMessages = 500;

        /// <summary>Interval in seconds for cleaning up expired messages from the bus.</summary>
        public const int MessageBusCleanupIntervalSeconds = 300;

        /// <summary>Poll interval in seconds when message bus is active (recent activity).</summary>
        public const int MessageBusActivePollSeconds = 5;

        /// <summary>Poll interval in seconds when message bus is idle (no recent activity).</summary>
        public const int MessageBusIdlePollSeconds = 30;

        // ── Token Optimization ──────────────────────────────────────────────

        /// <summary>Maximum retry attempts for token limit errors before giving up.</summary>
        public const int MaxTokenLimitRetries = 4;

        /// <summary>Context reduction factors for each retry attempt (0.8 = 80% of original).</summary>
        public static readonly double[] TokenRetryReductionFactors = { 0.8, 0.6, 0.4, 0.3 };

        /// <summary>Maximum tokens for child result truncation in feature mode.</summary>
        public const int MaxChildResultTokens = 2000;

        /// <summary>Threshold for using Sonnet vs Opus in feature mode consolidation (0-1 scale).</summary>
        public const double ModelComplexityThreshold = 0.5;
    }
}
