using System.Collections.Generic;
using System.Diagnostics;
using Spritely.Constants;
using Spritely.Managers;

namespace Spritely.Tests
{
    public class PromptBuilderTests
    {
        private readonly PromptBuilder _builder = new();

        // ── Model Selection ──────────────────────────────────────────

        [Fact]
        public void GetCliModelForTask_AutoDecompose_ReturnsSonnet()
        {
            var task = new AgentTask { AutoDecompose = true };
            Assert.Equal(AppConstants.ClaudeSonnet, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_SpawnTeam_ReturnsSonnet()
        {
            var task = new AgentTask { SpawnTeam = true };
            Assert.Equal(AppConstants.ClaudeSonnet, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_FeatureModePhaseNone_ReturnsSonnet()
        {
            var task = new AgentTask { IsFeatureMode = true, FeatureModePhase = FeatureModePhase.None };
            Assert.Equal(AppConstants.ClaudeSonnet, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_PlanningTeamMember_ReturnsSonnet()
        {
            var task = new AgentTask();
            task.Data.ParentTaskId = "parent1";
            task.ExtendedPlanning = false;
            task.PlanOnly = false;
            Assert.Equal(AppConstants.ClaudeSonnet, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_RegularTask_ReturnsOpus()
        {
            var task = new AgentTask { Description = "Fix a bug" };
            Assert.Equal(AppConstants.ClaudeOpus, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_ExtendedPlanningSubtask_ReturnsOpus()
        {
            var task = new AgentTask { ExtendedPlanning = true };
            task.Data.ParentTaskId = "parent1";
            Assert.Equal(AppConstants.ClaudeOpus, PromptBuilder.GetCliModelForTask(task));
        }

        [Theory]
        [InlineData(FeatureModePhase.None, "claude-sonnet-4-6")]
        [InlineData(FeatureModePhase.PlanConsolidation, "claude-opus-4-6")]
        [InlineData(FeatureModePhase.Evaluation, "claude-opus-4-6")]
        public void GetCliModelForPhase_ReturnsExpectedModel(FeatureModePhase phase, string expectedModel)
        {
            Assert.Equal(expectedModel, PromptBuilder.GetCliModelForPhase(phase));
        }

        [Theory]
        [InlineData("claude-opus-4-6", "Opus")]
        [InlineData("claude-sonnet-4-6", "Sonnet")]
        [InlineData("claude-haiku-4-5-20251001", "Haiku")]
        [InlineData("unknown-model", "unknown-model")]
        public void GetFriendlyModelName_ReturnsExpected(string modelId, string expected)
        {
            Assert.Equal(expected, PromptBuilder.GetFriendlyModelName(modelId));
        }

        // ── BuildBasePrompt ──────────────────────────────────────────

        [Fact]
        public void BuildBasePrompt_IncludesProjectDescription()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false,
                projectDescription: "My project");

            Assert.Contains("# PROJECT CONTEXT", result);
            Assert.Contains("My project", result);
        }

        [Fact]
        public void BuildBasePrompt_NoProjectDescription_OmitsBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false);

            Assert.DoesNotContain("# PROJECT CONTEXT", result);
        }

        [Fact]
        public void BuildBasePrompt_IncludesProjectRules()
        {
            var rules = "# PROJECT RULES\nNo console.log\n";
            var result = _builder.BuildBasePrompt("system", "task desc", false, false,
                projectRulesBlock: rules);

            Assert.Contains("No console.log", result);
        }

        [Fact]
        public void BuildBasePrompt_UseMcp_IncludesMcpBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", useMcp: true, isFeatureMode: false);
            Assert.Contains(PromptBuilder.McpPromptBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_NoMcp_ExcludesMcpBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", useMcp: false, isFeatureMode: false);
            Assert.DoesNotContain(PromptBuilder.McpPromptBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_IncludesNoGitWriteBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false);
            Assert.Contains(PromptBuilder.NoGitWriteBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_PlanOnly_OmitsGitBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, planOnly: true);
            Assert.DoesNotContain(PromptBuilder.NoGitWriteBlock, result);
            Assert.Contains(PromptBuilder.PlanOnlyBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_ExtendedPlanning_IncludesPlanningBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, extendedPlanning: true);
            Assert.Contains(PromptBuilder.ExtendedPlanningBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_AutoDecompose_IncludesDecompositionBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, autoDecompose: true);
            Assert.Contains(PromptBuilder.DecompositionPromptBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_SpawnTeam_IncludesTeamBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, spawnTeam: true);
            Assert.Contains(PromptBuilder.TeamDecompositionPromptBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_GameProject_IncludesGameRulesBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, isGameProject: true);
            Assert.Contains(PromptBuilder.GameRulesBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_ApplyFixTrue_IncludesApplyFixBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, applyFix: true);
            Assert.Contains(PromptBuilder.ApplyFixBlock, result);
            Assert.DoesNotContain(PromptBuilder.ConfirmBeforeChangesBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_ApplyFixFalse_IncludesConfirmBlock()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, false, applyFix: false);
            Assert.Contains(PromptBuilder.ConfirmBeforeChangesBlock, result);
            Assert.DoesNotContain(PromptBuilder.ApplyFixBlock, result);
        }

        [Fact]
        public void BuildBasePrompt_FeatureMode_UsesFeatureTemplate()
        {
            var result = _builder.BuildBasePrompt("system", "task desc", false, isFeatureMode: true);
            Assert.Contains(PromptBuilder.FeatureModeInitialTemplate, result);
            Assert.Contains("task desc", result);
        }

        [Fact]
        public void BuildBasePrompt_RegularTask_EndsWithUserPrompt()
        {
            var result = _builder.BuildBasePrompt("system", "do the thing", false, false);
            Assert.Contains("# USER PROMPT / TASK\ndo the thing", result);
        }

        [Fact]
        public void BuildBasePrompt_SuppressOutputEfficiency_OmitsBlock()
        {
            var withEfficiency = _builder.BuildBasePrompt("system", "task", false, false,
                suppressOutputEfficiency: false);
            var withoutEfficiency = _builder.BuildBasePrompt("system", "task", false, false,
                suppressOutputEfficiency: true);

            Assert.Contains(PromptBuilder.OutputEfficiencyBlock, withEfficiency);
            Assert.DoesNotContain(PromptBuilder.OutputEfficiencyBlock, withoutEfficiency);
        }

        // ── BuildFullPrompt ─────────────────────────────────────────

        [Fact]
        public void BuildFullPrompt_UsesStoredPromptIfPresent()
        {
            var task = new AgentTask { Description = "orig desc" };
            task.Data.StoredPrompt = "stored prompt text";

            var result = _builder.BuildFullPrompt("system", task);
            Assert.Contains("stored prompt text", result);
        }

        [Fact]
        public void BuildFullPrompt_AppendsDependencyContext()
        {
            var task = new AgentTask { Description = "desc" };
            task.Data.DependencyContext = "# DEPENDENCY CONTEXT\nSome dep info";

            var result = _builder.BuildFullPrompt("system", task);
            Assert.Contains("# DEPENDENCY CONTEXT", result);
            Assert.Contains("Some dep info", result);
        }

        [Fact]
        public void BuildFullPrompt_AppendsAdditionalInstructions()
        {
            var task = new AgentTask { Description = "main task", AdditionalInstructions = "also do this" };

            var result = _builder.BuildFullPrompt("system", task);
            Assert.Contains("# Additional Instructions", result);
            Assert.Contains("also do this", result);
        }

        [Fact]
        public void BuildFullPrompt_PlanningMember_SuppressesEfficiency()
        {
            var task = new AgentTask { Description = "explore" };
            task.Data.ParentTaskId = "parent1";
            task.ExtendedPlanning = false;
            task.PlanOnly = false;

            var result = _builder.BuildFullPrompt("system", task);
            Assert.DoesNotContain(PromptBuilder.OutputEfficiencyBlock, result);
        }

        // ── BuildPromptWithImages ────────────────────────────────────

        [Fact]
        public void BuildPromptWithImages_NoImages_ReturnsBasePrompt()
        {
            var result = _builder.BuildPromptWithImages("base prompt", new List<string>());
            Assert.Equal("base prompt", result);
        }

        [Fact]
        public void BuildPromptWithImages_WithImages_AppendsImageSection()
        {
            var images = new List<string> { @"C:\img1.png", @"C:\img2.jpg" };
            var result = _builder.BuildPromptWithImages("base prompt", images);

            Assert.Contains("# ATTACHED IMAGES", result);
            Assert.Contains(@"C:\img1.png", result);
            Assert.Contains(@"C:\img2.jpg", result);
        }

        // ── BuildClaudeCommand ───────────────────────────────────────

        [Fact]
        public void BuildClaudeCommand_Basic_ReturnsExpected()
        {
            var cmd = _builder.BuildClaudeCommand(skipPermissions: false);
            Assert.Equal("claude -p --verbose --output-format stream-json", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_SkipPermissions_IncludesFlag()
        {
            var cmd = _builder.BuildClaudeCommand(skipPermissions: true);
            Assert.Contains("--dangerously-skip-permissions", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_WithModel_IncludesModelFlag()
        {
            var cmd = _builder.BuildClaudeCommand(false, modelId: "claude-opus-4-6");
            Assert.Contains("--model claude-opus-4-6", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_PlanMode_IncludesPlanFlag()
        {
            var cmd = _builder.BuildClaudeCommand(false, planMode: true);
            Assert.Contains("--plan", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_EffortHigh_OmitsEffortFlag()
        {
            var cmd = _builder.BuildClaudeCommand(false, effortLevel: "high");
            Assert.DoesNotContain("--effort", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_EffortLow_IncludesEffortFlag()
        {
            var cmd = _builder.BuildClaudeCommand(false, effortLevel: "low");
            Assert.Contains("--effort low", cmd);
        }

        // ── BuildDependencyContext ───────────────────────────────────

        [Fact]
        public void BuildDependencyContext_NoMatchingDeps_ReturnsEmpty()
        {
            var result = _builder.BuildDependencyContext(
                new List<string> { "nonexistent" },
                Enumerable.Empty<AgentTask>(),
                Enumerable.Empty<AgentTask>());

            Assert.Equal("", result);
        }

        [Fact]
        public void BuildDependencyContext_FoundDep_IncludesDetails()
        {
            var dep = new AgentTask { Description = "Setup DB" };
            dep.Data.CompletionSummary = "Created tables";
            dep.Data.Summary = "DB Setup";

            var result = _builder.BuildDependencyContext(
                new List<string> { dep.Id },
                new[] { dep },
                Enumerable.Empty<AgentTask>());

            Assert.Contains("# DEPENDENCY CONTEXT", result);
            Assert.Contains("DB Setup", result);
            Assert.Contains("Setup DB", result);
            Assert.Contains("Created tables", result);
        }

        [Fact]
        public void BuildDependencyContext_FindsDepInHistory()
        {
            var dep = new AgentTask { Description = "Old task" };
            dep.Data.Summary = "Historical";

            var result = _builder.BuildDependencyContext(
                new List<string> { dep.Id },
                Enumerable.Empty<AgentTask>(),
                new[] { dep });

            Assert.Contains("Historical", result);
        }

        // ── BuildProcessStartInfo ────────────────────────────────────

        [Fact]
        public void BuildProcessStartInfo_Headless_UsesShellExecute()
        {
            var psi = _builder.BuildProcessStartInfo(@"C:\test.ps1", headless: true);

            Assert.Equal("powershell.exe", psi.FileName);
            Assert.True(psi.UseShellExecute);
            Assert.Contains("-NoExit", psi.Arguments);
        }

        [Fact]
        public void BuildProcessStartInfo_NotHeadless_RedirectsOutput()
        {
            var psi = _builder.BuildProcessStartInfo(@"C:\test.ps1", headless: false);

            Assert.False(psi.UseShellExecute);
            Assert.True(psi.RedirectStandardOutput);
            Assert.True(psi.RedirectStandardError);
            Assert.True(psi.CreateNoWindow);
        }

        // ── GetFeatureModeLogFilename ────────────────────────────────

        [Fact]
        public void GetFeatureModeLogFilename_NoTaskId_ReturnsDefault()
        {
            Assert.Equal(".feature_log.md", PromptBuilder.GetFeatureModeLogFilename());
        }

        [Fact]
        public void GetFeatureModeLogFilename_WithTaskId_IncludesId()
        {
            Assert.Equal(".feature_log_abc123.md", PromptBuilder.GetFeatureModeLogFilename("abc123"));
        }
    }
}
