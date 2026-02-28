namespace AgenticEngine
{
    /// <summary>
    /// Verification result from the LLM-based continue analysis.
    /// </summary>
    public class ContinueVerification
    {
        public bool ShouldContinue { get; set; }
        public string Reason { get; set; } = "";
    }
}
