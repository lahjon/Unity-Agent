namespace Spritely.Constants
{
    /// <summary>
    /// Centralised magic-number constants used across the application.
    /// Keeping them here makes behaviour tunable and self-documenting.
    /// </summary>
    public static class AppConstants
    {
        // ── Claude Models ────────────────────────────────────────────
        // Single source of truth for all Claude model identifiers.
        // Change these when upgrading to newer model versions.

        /// <summary>Claude Opus — highest capability, used for planning and execution tasks.</summary>
        public const string ClaudeOpus = "claude-opus-4-6";

        /// <summary>Claude Sonnet — balanced, used for exploration tasks (codebase analysis, decomposition).</summary>
        public const string ClaudeSonnet = "claude-sonnet-4-6";

        /// <summary>Claude Haiku — fastest/cheapest, used for lightweight classification tasks.</summary>
        public const string ClaudeHaiku = "claude-haiku-4-5-20251001";

        /// <summary>Default model for the Claude chat panel (API-based conversations).</summary>
        public const string ClaudeDefaultChatModel = ClaudeSonnet;

        /// <summary>Default model for CLI prompt execution (task running).</summary>
        public const string ClaudeDefaultPromptModel = ClaudeOpus;

        /// <summary>Ordered list of Claude models shown in the UI and available for selection.</summary>
        public static readonly string[] ClaudeAvailableModels = { ClaudeOpus, ClaudeSonnet, ClaudeHaiku };

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

        // ── Task Timeout ──────────────────────────────────────────────

        /// <summary>Default timeout for tasks in minutes.</summary>
        public const int DefaultTaskTimeoutMinutes = 120;

        /// <summary>Percentage of timeout elapsed before warning (0.8 = 80%).</summary>
        public const double TaskTimeoutWarningPercent = 0.8;

        // ── Token Optimization ──────────────────────────────────────────────

        /// <summary>Maximum retry attempts for token limit errors before giving up.</summary>
        public const int MaxTokenLimitRetries = 4;

        /// <summary>Context reduction factors for each retry attempt (0.8 = 80% of original).</summary>
        public static readonly double[] TokenRetryReductionFactors = { 0.8, 0.6, 0.4, 0.3 };

        /// <summary>Maximum tokens for child result truncation in teams mode.</summary>
        public const int MaxChildResultTokens = 2000;

        /// <summary>Threshold for using Sonnet vs Opus in teams mode consolidation (0-1 scale).</summary>
        public const double ModelComplexityThreshold = 0.5;

        // ── Token Cost Rates (USD per million tokens) ──────────────────
        // Single source of truth for all cost estimation across the app.

        public const decimal OpusInputPerM = 15.00m;
        public const decimal OpusOutputPerM = 75.00m;
        public const decimal OpusCacheReadPerM = 1.50m;
        public const decimal OpusCacheCreationPerM = 18.75m;

        public const decimal SonnetInputPerM = 3.00m;
        public const decimal SonnetOutputPerM = 15.00m;
        public const decimal SonnetCacheReadPerM = 0.30m;
        public const decimal SonnetCacheCreationPerM = 3.75m;

        public const decimal HaikuInputPerM = 0.80m;
        public const decimal HaikuOutputPerM = 4.00m;
        public const decimal HaikuCacheReadPerM = 0.08m;
        public const decimal HaikuCacheCreationPerM = 1.00m;

        // ── Remote Server ──────────────────────────────────────────────

        /// <summary>Default port for the SpritelyRemote REST API server.</summary>
        public const int DefaultRemoteServerPort = 7923;

        // ── Feedback System ──────────────────────────────────────────────

        /// <summary>Number of completed tasks before triggering a feedback analysis cycle.</summary>
        public const int FeedbackAnalysisThreshold = 10;

        /// <summary>Number of recent entries to include in each analysis window.</summary>
        public const int FeedbackAnalysisWindow = 30;

        /// <summary>Minimum entries required before invoking the LLM for deep analysis.</summary>
        public const int FeedbackLlmAnalysisThreshold = 15;

        // ── Prompt Evolution ──────────────────────────────────────────────

        /// <summary>Minimum feedback entries before attempting prompt mutation.</summary>
        public const int PromptEvolutionThreshold = 20;

        /// <summary>Number of recent entries to analyze for failure patterns.</summary>
        public const int PromptEvolutionAnalysisWindow = 30;

        /// <summary>Total A/B test tasks (variant + control) before making a decision.</summary>
        public const int PromptEvolutionAbTestSize = 5;

        /// <summary>Maximum prompt variants to retain per project.</summary>
        public const int PromptEvolutionMaxVariants = 20;
    }
}
