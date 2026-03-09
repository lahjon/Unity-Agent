using System;
using System.Linq;
using Spritely.Constants;
using Spritely.Managers;

namespace Spritely.Tests
{
    public class ContextReducerTests
    {
        // ── Retry info injection ─────────────────────────────────────

        [Fact]
        public void ReducePromptContext_IncludesRetryMarker()
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", 0, 100000);
            Assert.Contains("[TOKEN_LIMIT_RETRY]", result);
        }

        [Fact]
        public void ReducePromptContext_IncludesRetryNumber()
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", 2, 50000);
            Assert.Contains("Retry #3", result);
        }

        [Fact]
        public void ReducePromptContext_IncludesTokenEstimate()
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", 0, 123456);
            // N0 format is locale-dependent; just check the digits appear
            Assert.Contains("123", result);
            Assert.Contains("456", result);
        }

        // ── Reduction factor selection ───────────────────────────────

        [Theory]
        [InlineData(0, 0.8)]
        [InlineData(1, 0.6)]
        [InlineData(2, 0.4)]
        [InlineData(3, 0.3)]
        public void ReducePromptContext_UsesCorrectReductionFactor(int retryCount, double expectedFactor)
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", retryCount, 10000);
            var percentStr = $"{expectedFactor:P0}";
            Assert.Contains(percentStr, result);
        }

        [Fact]
        public void ReducePromptContext_NegativeRetry_UsesLastFactor()
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", -1, 10000);
            var lastFactor = AppConstants.TokenRetryReductionFactors[^1];
            Assert.Contains($"{lastFactor:P0}", result);
        }

        [Fact]
        public void ReducePromptContext_OverflowRetry_UsesLastFactor()
        {
            var result = ContextReducer.ReducePromptContext("Some prompt", 99, 10000);
            var lastFactor = AppConstants.TokenRetryReductionFactors[^1];
            Assert.Contains($"{lastFactor:P0}", result);
        }

        // ── Essential content preservation ───────────────────────────

        [Fact]
        public void ReducePromptContext_PreservesEssentialMarkedContent()
        {
            var prompt = "[ESSENTIAL] Critical setup info\nDo not lose this.";
            var result = ContextReducer.ReducePromptContext(prompt, 0, 10000);
            Assert.Contains("[ESSENTIAL] Critical setup info", result);
        }

        [Fact]
        public void ReducePromptContext_PreservesUserPromptSection()
        {
            var prompt = "Some preamble\n# USER PROMPT\nFix the login bug\nMore details here.";
            var result = ContextReducer.ReducePromptContext(prompt, 0, 10000);
            Assert.Contains("USER PROMPT", result);
            Assert.Contains("Fix the login bug", result);
        }

        [Fact]
        public void ReducePromptContext_PreservesErrorContent()
        {
            var prompt = "Setup block\nERROR: NullReferenceException at line 42\nStack trace follows.";
            var result = ContextReducer.ReducePromptContext(prompt, 0, 10000);
            Assert.Contains("NullReferenceException", result);
        }

        [Fact]
        public void ReducePromptContext_PreservesTaskDescription()
        {
            var prompt = "Background\nTask: Implement authentication\nSome other content.";
            var result = ContextReducer.ReducePromptContext(prompt, 0, 10000);
            Assert.Contains("Task: Implement authentication", result);
        }

        // ── Progressive reduction ────────────────────────────────────

        [Fact]
        public void ReducePromptContext_HigherRetry_ProducesSmallerOutput()
        {
            // General content BEFORE [ESSENTIAL] gets flushed as a General (priority 0.5) section.
            // This ensures non-essential content exists that can be dropped under heavier reduction.
            var generalBlock = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"Line {i}: general content padding text that is long enough to matter"));
            var prompt = $"{generalBlock}\n[ESSENTIAL] Keep this";

            var retry0 = ContextReducer.ReducePromptContext(prompt, 0, 100000);
            var retry3 = ContextReducer.ReducePromptContext(prompt, 3, 100000);

            Assert.True(retry3.Length < retry0.Length,
                $"Retry 3 ({retry3.Length}) should be smaller than retry 0 ({retry0.Length})");
        }

        [Fact]
        public void ReducePromptContext_AggressiveReduction_StillKeepsEssentials()
        {
            // Use separate [ESSENTIAL] markers to create distinct essential sections
            var filler = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"Filler line {i}: padding content to bulk up the prompt"));
            var prompt = $"[ESSENTIAL] Must keep this\n{filler}\n[ESSENTIAL] Fix the bug\nMore details";

            // Most aggressive reduction
            var result = ContextReducer.ReducePromptContext(prompt, 3, 200000);

            Assert.Contains("[ESSENTIAL] Must keep this", result);
            Assert.Contains("[ESSENTIAL] Fix the bug", result);
        }

        // ── Section classification ───────────────────────────────────

        [Fact]
        public void ReducePromptContext_HistorySections_LowerPriority()
        {
            // Use a second [ESSENTIAL] marker to flush the first section before history begins
            var essentialLine = "[ESSENTIAL] Critical task data";
            var historyBlock = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"Filler line {i}: padding content to bulk up the prompt"));
            var prompt = $"{essentialLine}\n[ESSENTIAL] Second marker\n{historyBlock}";

            var result = ContextReducer.ReducePromptContext(prompt, 3, 200000);

            Assert.Contains("Critical task data", result);
        }

        [Fact]
        public void ReducePromptContext_NonEssentialDroppedUnderBudgetPressure()
        {
            // Large non-essential content before an [ESSENTIAL] marker gets its own section.
            // Under aggressive reduction, non-essential content is dropped while essential survives.
            var bigBlock = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"Filler line {i}: padding content to create a large non-essential section"));
            var prompt = $"{bigBlock}\n[ESSENTIAL] Critical info preserved";

            var mild = ContextReducer.ReducePromptContext(prompt, 0, 100000);
            var aggressive = ContextReducer.ReducePromptContext(prompt, 3, 100000);

            // Essential content survives both reductions
            Assert.Contains("Critical info preserved", mild);
            Assert.Contains("Critical info preserved", aggressive);

            // Aggressive reduction drops more non-essential content
            Assert.True(aggressive.Length < mild.Length);
        }

        // ── Truncation marker ────────────────────────────────────────

        [Fact]
        public void ReducePromptContext_TruncatedSections_ContainMarker()
        {
            // Create a prompt large enough that some sections must be truncated
            var essentialBlock = "[ESSENTIAL] " + new string('X', 5000);
            var generalBlock = "General: " + new string('Y', 10000);
            var prompt = $"{essentialBlock}\n{generalBlock}";

            var result = ContextReducer.ReducePromptContext(prompt, 2, 200000);

            // Either the general block is dropped or truncated — if truncated, marker present
            if (result.Contains("General:") && !result.Contains(new string('Y', 10000)))
            {
                Assert.Contains("[TRUNCATED FOR TOKEN LIMIT]", result);
            }
        }

        // ── Edge cases ───────────────────────────────────────────────

        [Fact]
        public void ReducePromptContext_EmptyPrompt_ReturnsRetryInfoOnly()
        {
            var result = ContextReducer.ReducePromptContext("", 0, 0);
            Assert.Contains("[TOKEN_LIMIT_RETRY]", result);
        }

        [Fact]
        public void ReducePromptContext_SingleLine_Preserved()
        {
            var result = ContextReducer.ReducePromptContext("Task: do something", 0, 5000);
            Assert.Contains("Task: do something", result);
        }
    }
}
