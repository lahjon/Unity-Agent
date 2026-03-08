namespace Spritely
{
    /// <summary>
    /// Verification result from the LLM-based result quality analysis.
    /// </summary>
    public class ResultVerification
    {
        public bool Passed { get; set; }
        public string Summary { get; set; } = "";
        public string NextSteps { get; set; } = "";

        /// <summary>The formatted prompt that was sent to the verifier LLM.</summary>
        public string SentPrompt { get; set; } = "";
    }
}
