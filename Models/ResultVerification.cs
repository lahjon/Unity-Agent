namespace HappyEngine
{
    /// <summary>
    /// Verification result from the LLM-based result quality analysis.
    /// </summary>
    public class ResultVerification
    {
        public bool Passed { get; set; }
        public string Summary { get; set; } = "";
    }
}
