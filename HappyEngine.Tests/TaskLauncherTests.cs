using System.Diagnostics;
using HappyEngine.Helpers;
using HappyEngine.Managers;
using Xunit;

namespace HappyEngine.Tests
{
    public class TaskLauncherTests
    {
        private readonly ITaskFactory _factory = new Managers.TaskFactory();
        private readonly IPromptBuilder _prompt = new PromptBuilder();
        private readonly IGitHelper _git = new GitHelper();
        private readonly ICompletionAnalyzer _completion;

        public TaskLauncherTests()
        {
            _completion = new CompletionAnalyzer(_git);
        }

        // ── Validation ──────────────────────────────────────────────

        [Fact]
        public void ValidateTaskInput_Null_ReturnsFalse()
        {
            Assert.False(_factory.ValidateTaskInput(null));
        }

        [Fact]
        public void ValidateTaskInput_Empty_ReturnsFalse()
        {
            Assert.False(_factory.ValidateTaskInput(""));
        }

        [Fact]
        public void ValidateTaskInput_Whitespace_ReturnsFalse()
        {
            Assert.False(_factory.ValidateTaskInput("   "));
        }

        [Fact]
        public void ValidateTaskInput_ValidText_ReturnsTrue()
        {
            Assert.True(_factory.ValidateTaskInput("Fix the login bug"));
        }

        // ── CreateTask ──────────────────────────────────────────────

        [Fact]
        public void CreateTask_SetsAllProperties()
        {
            var task = _factory.CreateTask(
                "Do something",
                @"C:\Projects\Test",
                skipPermissions: true,
                remoteSession: true,
                headless: false,
                isFeatureMode: true,
                ignoreFileLocks: true,
                useMcp: true,
                spawnTeam: true);

            Assert.Equal("Do something", task.Description);
            Assert.Equal(@"C:\Projects\Test", task.ProjectPath);
            Assert.True(task.SkipPermissions);
            Assert.True(task.RemoteSession);
            Assert.False(task.Headless);
            Assert.True(task.IsFeatureMode);
            Assert.True(task.IgnoreFileLocks);
            Assert.True(task.UseMcp);
            Assert.True(task.SpawnTeam);
            Assert.Equal(2, task.MaxIterations);
        }

        [Fact]
        public void CreateTask_WithImages_CopiesImagePaths()
        {
            var images = new List<string> { "img1.png", "img2.jpg" };
            var task = _factory.CreateTask("desc", @"C:\proj", false, false, false, false, false, false, false, false, false, false, false, images);

            Assert.Equal(2, task.ImagePaths.Count);
            Assert.Contains("img1.png", task.ImagePaths);
            Assert.Contains("img2.jpg", task.ImagePaths);
        }

        [Fact]
        public void CreateTask_NullImages_EmptyList()
        {
            var task = _factory.CreateTask("desc", @"C:\proj", false, false, false, false, false, false);
            Assert.Empty(task.ImagePaths);
        }

        [Fact]
        public void CreateTask_NoGitWrite_SetsProperty()
        {
            var task = _factory.CreateTask("desc", @"C:\proj", false, false, false, false, false, false, noGitWrite: true);
            Assert.True(task.NoGitWrite);
        }

        [Fact]
        public void CreateTask_MaxIterationsIs2()
        {
            var task = _factory.CreateTask("desc", @"C:\proj", false, false, false, false, false, false);
            Assert.Equal(2, task.MaxIterations);
        }

        // ── Prompt Building ─────────────────────────────────────────

        [Fact]
        public void BuildBasePrompt_NormalMode_CombinesSystemAndDescription()
        {
            var result = _prompt.BuildBasePrompt("SYSTEM:", "do things", useMcp: false, isFeatureMode: false);
            Assert.StartsWith("SYSTEM:", result);
            Assert.EndsWith("do things", result);
            Assert.Contains("# USER PROMPT / TASK", result);
        }

        [Fact]
        public void BuildBasePrompt_WithMcp_IncludesMcpBlock()
        {
            var result = _prompt.BuildBasePrompt("SYS:", "task", useMcp: true, isFeatureMode: false);
            Assert.Contains("# MCP", result);
            Assert.Contains("mcp-for-unity-server", result);
            Assert.StartsWith("SYS:", result);
            Assert.EndsWith("task", result);
        }

        [Fact]
        public void BuildBasePrompt_FeatureMode_UsesFeatureModeTemplate()
        {
            var result = _prompt.BuildBasePrompt("SYSTEM:", "fix bugs", useMcp: false, isFeatureMode: true);
            Assert.StartsWith("# FEATURE MODE", result);
            Assert.EndsWith("fix bugs", result);
            Assert.DoesNotContain("SYSTEM:", result);
        }

        [Fact]
        public void BuildBasePrompt_WithNoGitWrite_IncludesGitBlock()
        {
            var result = _prompt.BuildBasePrompt("SYS:", "task", useMcp: false, isFeatureMode: false, noGitWrite: true);
            Assert.Contains("NO GIT WRITES", result);
            Assert.Contains("modify repository state", result);
            Assert.StartsWith("SYS:", result);
            Assert.EndsWith("task", result);
        }

        [Fact]
        public void BuildBasePrompt_WithoutNoGitWrite_IncludesNoPushBlock()
        {
            var result = _prompt.BuildBasePrompt("SYS:", "task", useMcp: false, isFeatureMode: false, noGitWrite: false);
            Assert.DoesNotContain("NO GIT WRITES", result);
            Assert.Contains("NO GIT PUSH", result);
            Assert.Contains("git push", result);
        }

        [Fact]
        public void BuildBasePrompt_FeatureMode_IgnoresMcp()
        {
            var result = _prompt.BuildBasePrompt("SYS:", "task", useMcp: true, isFeatureMode: true);
            Assert.DoesNotContain("# MCP\n", result);
            Assert.StartsWith("# FEATURE MODE", result);
        }

        [Fact]
        public void BuildPromptWithImages_NoImages_ReturnsBase()
        {
            var result = _prompt.BuildPromptWithImages("base prompt", new List<string>());
            Assert.Equal("base prompt", result);
        }

        [Fact]
        public void BuildPromptWithImages_WithImages_AppendsSection()
        {
            var images = new List<string> { @"C:\imgs\test.png", @"C:\imgs\test2.jpg" };
            var result = _prompt.BuildPromptWithImages("base", images);

            Assert.Contains("# ATTACHED IMAGES", result);
            Assert.Contains("Read tool", result);
            Assert.Contains(@"C:\imgs\test.png", result);
            Assert.Contains(@"C:\imgs\test2.jpg", result);
        }

        [Fact]
        public void BuildFullPrompt_NormalTask_CombinesCorrectly()
        {
            var task = new AgentTask
            {
                Description = "do work",
                UseMcp = false,
                IsFeatureMode = false
            };
            var result = _prompt.BuildFullPrompt("SYS:", task);
            Assert.StartsWith("SYS:", result);
            Assert.EndsWith("do work", result);
            Assert.Contains("# USER PROMPT / TASK", result);
        }

        [Fact]
        public void BuildFullPrompt_WithImages_AppendsImages()
        {
            var task = new AgentTask
            {
                Description = "do work",
                UseMcp = false,
                IsFeatureMode = false
            };
            task.ImagePaths.Add("img.png");
            var result = _prompt.BuildFullPrompt("SYS:", task);
            Assert.Contains("# ATTACHED IMAGES", result);
            Assert.Contains("img.png", result);
        }

        // ── Command Building ────────────────────────────────────────

        [Fact]
        public void BuildClaudeCommand_NoFlags_BasicCommand()
        {
            var cmd = _prompt.BuildClaudeCommand(false, false);
            Assert.Equal("claude -p --verbose --output-format stream-json", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_WithModel_IncludesModelFlag()
        {
            var cmd = _prompt.BuildClaudeCommand(false, false, "claude-opus-4-20250514");
            Assert.Contains("--model claude-opus-4-20250514", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_SkipPermissions_IncludesFlag()
        {
            var cmd = _prompt.BuildClaudeCommand(true, false);
            Assert.Contains("--dangerously-skip-permissions", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_Remote_IncludesFlag()
        {
            var cmd = _prompt.BuildClaudeCommand(false, true);
            Assert.Contains("--remote", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_NoSpawnTeamFlag()
        {
            var cmd = _prompt.BuildClaudeCommand(false, false);
            Assert.DoesNotContain("--spawn-team", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_AllFlags()
        {
            var cmd = _prompt.BuildClaudeCommand(true, true, "claude-sonnet-4-20250514");
            Assert.Contains("--dangerously-skip-permissions", cmd);
            Assert.Contains("--remote", cmd);
            Assert.Contains("--model claude-sonnet-4-20250514", cmd);
            Assert.DoesNotContain("--spawn-team", cmd);
            Assert.Contains("--verbose", cmd);
            Assert.Contains("--output-format stream-json", cmd);
        }

        [Fact]
        public void BuildClaudeCommand_AlwaysContainsStreamJson()
        {
            var cmd = _prompt.BuildClaudeCommand(false, false);
            Assert.Contains("stream-json", cmd);
        }

        [Fact]
        public void GetCliModelForTask_AutoDecompose_ReturnsSonnet()
        {
            var task = new AgentTask { AutoDecompose = true };
            Assert.Equal(PromptBuilder.CliSonnetModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_SpawnTeam_ReturnsSonnet()
        {
            var task = new AgentTask { SpawnTeam = true };
            Assert.Equal(PromptBuilder.CliSonnetModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_FeatureModeInitial_ReturnsSonnet()
        {
            var task = new AgentTask { IsFeatureMode = true };
            task.Data.FeatureModePhase = FeatureModePhase.None;
            Assert.Equal(PromptBuilder.CliSonnetModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_PlanningTeamMember_ReturnsSonnet()
        {
            var task = new AgentTask { NoGitWrite = true, ParentTaskId = "parent123" };
            Assert.Equal(PromptBuilder.CliSonnetModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_RegularTask_ReturnsOpus()
        {
            var task = new AgentTask();
            Assert.Equal(PromptBuilder.CliOpusModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForTask_ExecutionStep_ReturnsOpus()
        {
            var task = new AgentTask { ExtendedPlanning = true, ParentTaskId = "parent123", NoGitWrite = true };
            Assert.Equal(PromptBuilder.CliOpusModel, PromptBuilder.GetCliModelForTask(task));
        }

        [Fact]
        public void GetCliModelForPhase_Consolidation_ReturnsOpus()
        {
            Assert.Equal(PromptBuilder.CliOpusModel, PromptBuilder.GetCliModelForPhase(FeatureModePhase.PlanConsolidation));
        }

        [Fact]
        public void GetCliModelForPhase_Evaluation_ReturnsOpus()
        {
            Assert.Equal(PromptBuilder.CliOpusModel, PromptBuilder.GetCliModelForPhase(FeatureModePhase.Evaluation));
        }

        [Fact]
        public void GetCliModelForPhase_None_ReturnsSonnet()
        {
            Assert.Equal(PromptBuilder.CliSonnetModel, PromptBuilder.GetCliModelForPhase(FeatureModePhase.None));
        }

        // ── PowerShell Script Building ──────────────────────────────

        [Fact]
        public void BuildPowerShellScript_ContainsSetLocation()
        {
            var script = _prompt.BuildPowerShellScript(@"C:\proj", @"C:\scripts\prompt.txt", "claude -p");
            Assert.Contains("Set-Location -LiteralPath 'C:\\proj'", script);
        }

        [Fact]
        public void BuildPowerShellScript_PipesPromptViaStdin()
        {
            var script = _prompt.BuildPowerShellScript(@"C:\proj", @"C:\scripts\prompt.txt", "claude -p");
            Assert.Contains("Get-Content -Raw -LiteralPath 'C:\\scripts\\prompt.txt' | claude -p", script);
        }

        [Fact]
        public void BuildPowerShellScript_ContainsClaudeCommand()
        {
            var script = _prompt.BuildPowerShellScript(@"C:\proj", @"C:\scripts\prompt.txt", "my-command");
            Assert.Contains("my-command", script);
        }

        [Fact]
        public void BuildPowerShellScript_ClearsClaudeCodeEnv()
        {
            var script = _prompt.BuildPowerShellScript(@"C:\proj", @"C:\p.txt", "cmd");
            Assert.Contains("$env:CLAUDECODE = $null", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_ContainsReadKey()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("ReadKey", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_NoStreamJson()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.DoesNotContain("stream-json", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_SkipPermissions()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", true, false);
            Assert.Contains("--dangerously-skip-permissions", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_Remote()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, true);
            Assert.Contains("--remote", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_NoSpawnTeamFlag()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.DoesNotContain("--spawn-team", script);
        }

        [Fact]
        public void SpawnTeam_AddsTeamDecompositionPromptBlock()
        {
            var prompt = _prompt.BuildBasePrompt("system", "my task", false, false, spawnTeam: true);
            Assert.Contains("TEAM SPAWN MODE", prompt);
            Assert.Contains("```TEAM", prompt);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_ContainsPressAnyKey()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("Press any key to close", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_ContainsVerbose()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("--verbose", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_ContainsDiagnosticMessages()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("Starting Claude", script);
            Assert.Contains("Project:", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_ContainsErrorHandling()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("LASTEXITCODE", script);
        }

        [Fact]
        public void BuildHeadlessPowerShellScript_PipesPromptToStdin()
        {
            var script = _prompt.BuildHeadlessPowerShellScript(@"C:\proj", @"C:\p.txt", false, false);
            Assert.Contains("Get-Content -Raw", script);
            Assert.Contains("| claude -p", script);
        }

        // ── ProcessStartInfo ────────────────────────────────────────

        [Fact]
        public void BuildProcessStartInfo_Normal_RedirectsIO()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: false);
            Assert.True(psi.RedirectStandardOutput);
            Assert.True(psi.RedirectStandardError);
            Assert.True(psi.CreateNoWindow);
            Assert.False(psi.UseShellExecute);
        }

        [Fact]
        public void BuildProcessStartInfo_Normal_ContainsScriptPath()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\scripts\task.ps1", headless: false);
            Assert.Contains("task.ps1", psi.Arguments);
        }

        [Fact]
        public void BuildProcessStartInfo_Headless_UsesShellExecute()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: true);
            Assert.True(psi.UseShellExecute);
            Assert.False(psi.RedirectStandardOutput);
        }

        [Fact]
        public void BuildProcessStartInfo_Headless_NoExit()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: true);
            Assert.Contains("-NoExit", psi.Arguments);
        }

        [Fact]
        public void BuildProcessStartInfo_Normal_NoNoExit()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: false);
            Assert.DoesNotContain("-NoExit", psi.Arguments);
        }

        [Fact]
        public void BuildProcessStartInfo_Headless_NoProfile()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: true);
            Assert.Contains("-NoProfile", psi.Arguments);
        }

        [Fact]
        public void BuildProcessStartInfo_UsesPowerShell()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: false);
            Assert.Equal("powershell.exe", psi.FileName);
        }

        [Fact]
        public void BuildProcessStartInfo_BypassesExecutionPolicy()
        {
            var psi = _prompt.BuildProcessStartInfo(@"C:\script.ps1", headless: false);
            Assert.Contains("-ExecutionPolicy Bypass", psi.Arguments);
        }

        // ── Feature Mode Helpers ────────────────────────────────────

        [Fact]
        public void PrepareTaskForFeatureModeStart_ForcesSkipPermissions()
        {
            var task = new AgentTask { SkipPermissions = false };
            _factory.PrepareTaskForFeatureModeStart(task);
            Assert.True(task.SkipPermissions);
        }

        [Fact]
        public void PrepareTaskForFeatureModeStart_SetsIteration1()
        {
            var task = new AgentTask();
            _factory.PrepareTaskForFeatureModeStart(task);
            Assert.Equal(1, task.CurrentIteration);
        }

        [Fact]
        public void PrepareTaskForFeatureModeStart_ResetsFailures()
        {
            var task = new AgentTask { ConsecutiveFailures = 5 };
            _factory.PrepareTaskForFeatureModeStart(task);
            Assert.Equal(0, task.ConsecutiveFailures);
        }

        [Fact]
        public void PrepareTaskForFeatureModeStart_ResetsOutputStart()
        {
            var task = new AgentTask { LastIterationOutputStart = 999 };
            _factory.PrepareTaskForFeatureModeStart(task);
            Assert.Equal(0, task.LastIterationOutputStart);
        }

        [Fact]
        public void BuildFeatureModeContinuationPrompt_InterpolatesValues()
        {
            var prompt = _prompt.BuildFeatureModeContinuationPrompt(3, 50);
            Assert.Contains("iteration 3/50", prompt);
        }

        [Fact]
        public void BuildFeatureModeContinuationPrompt_ContainsRestrictions()
        {
            var prompt = _prompt.BuildFeatureModeContinuationPrompt(1, 10);
            Assert.Contains("No git", prompt);
            Assert.Contains("no OS modifications", prompt);
            Assert.Contains("project root", prompt);
        }

        [Fact]
        public void CheckFeatureModeComplete_Complete_ReturnsTrue()
        {
            Assert.True(_completion.CheckFeatureModeComplete("some output\nSTATUS: COMPLETE\n"));
        }

        [Fact]
        public void CheckFeatureModeComplete_NeedsMoreWork_ReturnsFalse()
        {
            Assert.False(_completion.CheckFeatureModeComplete("some output\nSTATUS: NEEDS_MORE_WORK\n"));
        }

        [Fact]
        public void CheckFeatureModeComplete_NoMarker_ReturnsFalse()
        {
            Assert.False(_completion.CheckFeatureModeComplete("just some output\nno markers here\n"));
        }

        [Fact]
        public void CheckFeatureModeComplete_CompleteWithWhitespace_ReturnsTrue()
        {
            Assert.True(_completion.CheckFeatureModeComplete("output\n  STATUS: COMPLETE  \n"));
        }

        [Fact]
        public void CheckFeatureModeComplete_NeedsMoreWorkThenComplete_ReturnsTrue()
        {
            // Last marker wins when scanning from the end
            var output = "STATUS: NEEDS_MORE_WORK\nmore work\nSTATUS: COMPLETE\n";
            Assert.True(_completion.CheckFeatureModeComplete(output));
        }

        [Fact]
        public void CheckFeatureModeComplete_CompleteThenNeedsMoreWork_ReturnsFalse()
        {
            var output = "STATUS: COMPLETE\nmore work\nSTATUS: NEEDS_MORE_WORK\n";
            Assert.False(_completion.CheckFeatureModeComplete(output));
        }

        // ── StripAnsi ───────────────────────────────────────────────

        [Fact]
        public void StripAnsi_PlainText_Unchanged()
        {
            Assert.Equal("hello world", FormatHelpers.StripAnsiCodes("hello world"));
        }

        [Fact]
        public void StripAnsi_RemovesColorCodes()
        {
            Assert.Equal("hello", FormatHelpers.StripAnsiCodes("\x1B[31mhello\x1B[0m"));
        }

        [Fact]
        public void StripAnsi_RemovesBoldCodes()
        {
            Assert.Equal("text", FormatHelpers.StripAnsiCodes("\x1B[1mtext\x1B[22m"));
        }

        [Fact]
        public void StripAnsi_EmptyString_ReturnsEmpty()
        {
            Assert.Equal("", FormatHelpers.StripAnsiCodes(""));
        }

        // ── IsImageFile ─────────────────────────────────────────────

        [Theory]
        [InlineData("test.png", true)]
        [InlineData("test.jpg", true)]
        [InlineData("test.jpeg", true)]
        [InlineData("test.gif", true)]
        [InlineData("test.bmp", true)]
        [InlineData("test.webp", true)]
        [InlineData("test.PNG", true)]
        [InlineData("test.txt", false)]
        [InlineData("test.cs", false)]
        [InlineData("test.pdf", false)]
        [InlineData("test", false)]
        public void IsImageFile_DetectsCorrectly(string path, bool expected)
        {
            Assert.Equal(expected, FormatHelpers.IsImageFile(path));
        }

        // ── IsFileModifyTool ────────────────────────────────────────

        [Theory]
        [InlineData("Write", true)]
        [InlineData("Edit", true)]
        [InlineData("MultiEdit", true)]
        [InlineData("NotebookEdit", true)]
        [InlineData("write", true)]
        [InlineData("EDIT", true)]
        [InlineData("Read", false)]
        [InlineData("Grep", false)]
        [InlineData("Bash", true)]
        [InlineData("TodoWrite", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void IsFileModifyTool_DetectsCorrectly(string? toolName, bool expected)
        {
            Assert.Equal(expected, FormatHelpers.IsFileModifyTool(toolName));
        }

        // ── NormalizePath ───────────────────────────────────────────

        [Fact]
        public void NormalizePath_ForwardSlashes_Normalized()
        {
            var result = FormatHelpers.NormalizePath("src/main/file.cs", null);
            Assert.DoesNotContain("/", result);
        }

        [Fact]
        public void NormalizePath_ReturnsLowercase()
        {
            var result = FormatHelpers.NormalizePath(@"C:\Users\TEST\File.CS", null);
            Assert.Equal(result, result.ToLowerInvariant());
        }

        [Fact]
        public void NormalizePath_RelativeWithBase_Combines()
        {
            var result = FormatHelpers.NormalizePath("src/file.cs", @"C:\Projects\MyApp");
            Assert.Contains("myapp", result);
            Assert.Contains("file.cs", result);
        }

        [Fact]
        public void NormalizePath_AbsolutePath_IgnoresBase()
        {
            var result = FormatHelpers.NormalizePath(@"C:\Absolute\Path.cs", @"C:\Other\Base");
            Assert.Contains("absolute", result);
            Assert.DoesNotContain("other", result);
        }

        // ── IsTokenLimitError ───────────────────────────────────────

        [Theory]
        [InlineData("Error: rate limit exceeded")]
        [InlineData("token limit reached")]
        [InlineData("server overloaded")]
        [InlineData("error 529")]
        [InlineData("at capacity")]
        [InlineData("too many requests")]
        public void IsTokenLimitError_DetectsLimitErrors(string output)
        {
            Assert.True(_completion.IsTokenLimitError(output));
        }

        [Fact]
        public void IsTokenLimitError_NormalOutput_ReturnsFalse()
        {
            Assert.False(_completion.IsTokenLimitError("Task completed successfully."));
        }

        [Fact]
        public void IsTokenLimitError_ChecksLast3000Chars()
        {
            // Error at the end of a long string should still be detected
            var longOutput = new string('x', 5000) + "rate limit";
            Assert.True(_completion.IsTokenLimitError(longOutput));
        }

        [Fact]
        public void IsTokenLimitError_ErrorBeyond3000Chars_NotDetected()
        {
            // Error at the start of a long string, beyond the 3000 char window
            var longOutput = "rate limit" + new string('x', 5000);
            Assert.False(_completion.IsTokenLimitError(longOutput));
        }

        // ── Constants ───────────────────────────────────────────────

        [Fact]
        public void DefaultSystemPrompt_ContainsStrictRule()
        {
            Assert.Contains("# RULES", PromptBuilder.DefaultSystemPrompt);
        }

        [Fact]
        public void DefaultSystemPrompt_EndsWithNewlines()
        {
            Assert.EndsWith("\n\n", PromptBuilder.DefaultSystemPrompt);
        }

        [Fact]
        public void DefaultSystemPrompt_ContainsNoSecretsRule()
        {
            Assert.Contains("secrets", PromptBuilder.DefaultSystemPrompt);
            Assert.Contains("API keys", PromptBuilder.DefaultSystemPrompt);
            Assert.Contains("%LOCALAPPDATA%", PromptBuilder.DefaultSystemPrompt);
        }

        [Fact]
        public void McpPromptBlock_ContainsServerUrl()
        {
            Assert.Contains("127.0.0.1:8080", PromptBuilder.McpPromptBlock);
        }

        [Fact]
        public void NoGitWriteBlock_ContainsRestrictions()
        {
            Assert.Contains("NO GIT WRITES", PromptBuilder.NoGitWriteBlock);
            Assert.Contains("commit", PromptBuilder.NoGitWriteBlock);
            Assert.Contains("push", PromptBuilder.NoGitWriteBlock);
        }

        [Fact]
        public void NoPushBlock_ContainsRestrictions()
        {
            Assert.Contains("NO GIT PUSH", PromptBuilder.NoPushBlock);
            Assert.Contains("git push", PromptBuilder.NoPushBlock);
            Assert.Contains("git panel", PromptBuilder.NoPushBlock);
        }

        [Fact]
        public void FeatureModeInitialTemplate_ContainsRestrictions()
        {
            Assert.Contains("No git commands", PromptBuilder.FeatureModeInitialTemplate);
            Assert.Contains("No file modifications", PromptBuilder.FeatureModeInitialTemplate);
            Assert.Contains("TEAM", PromptBuilder.FeatureModeInitialTemplate);
        }

        [Fact]
        public void FeatureModeContinuationTemplate_ContainsPlaceholders()
        {
            Assert.Contains("{0}", PromptBuilder.FeatureModeContinuationTemplate);
            Assert.Contains("{1}", PromptBuilder.FeatureModeContinuationTemplate);
        }

        // ── Completion Summary ─────────────────────────────────────────

        [Fact]
        public void FormatCompletionSummary_ContainsStatus()
        {
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.FromMinutes(5), null);
            Assert.Contains("Completed", result);
        }

        [Fact]
        public void FormatCompletionSummary_ContainsDuration()
        {
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.FromSeconds(323), null);
            Assert.Contains("5m 23s", result);
        }

        [Fact]
        public void FormatCompletionSummary_NullFiles_NoFileSection()
        {
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.Zero, null);
            Assert.DoesNotContain("Files modified", result);
            Assert.DoesNotContain("Lines changed", result);
        }

        [Fact]
        public void FormatCompletionSummary_WithFiles_NoFileSection()
        {
            var files = new List<(string name, int added, int removed)>
            {
                ("src/App.cs", 12, 5),
                ("src/Main.cs", 25, 8)
            };
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Failed, TimeSpan.FromMinutes(2), files);
            Assert.DoesNotContain("Files modified", result);
            Assert.DoesNotContain("Lines changed", result);
            Assert.DoesNotContain("src/App.cs", result);
            Assert.Contains("Failed", result);
        }

        [Fact]
        public void FormatCompletionSummary_ContainsSummaryHeader()
        {
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.Zero, null);
            Assert.Contains("TASK COMPLETION SUMMARY", result);
        }

        [Fact]
        public void FormatCompletionSummary_FileDetails_DoesNotListIndividualFiles()
        {
            var files = new List<(string name, int added, int removed)>
            {
                ("test.cs", 10, 3)
            };
            var result = _completion.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.Zero, files);
            Assert.DoesNotContain("test.cs", result);
            Assert.DoesNotContain("Modified files:", result);
        }

        [Fact]
        public async Task CaptureGitHeadAsync_ValidRepo_ReturnsHash()
        {
            // The project itself is a git repo
            var hash = await _git.CaptureGitHeadAsync(System.IO.Directory.GetCurrentDirectory());
            Assert.NotNull(hash);
            Assert.True(hash!.Length >= 7); // short hash at minimum
        }

        [Fact]
        public async Task CaptureGitHeadAsync_InvalidPath_ReturnsNull()
        {
            var hash = await _git.CaptureGitHeadAsync(@"C:\nonexistent_path_12345");
            Assert.Null(hash);
        }

        [Fact]
        public async Task GetGitFileChangesAsync_InvalidPath_ReturnsNull()
        {
            var changes = await _git.GetGitFileChangesAsync(@"C:\nonexistent_path_12345", null);
            Assert.Null(changes);
        }

        [Fact]
        public async Task GenerateCompletionSummaryAsync_InvalidPath_StillReturnsFormatted()
        {
            var result = await _completion.GenerateCompletionSummaryAsync(
                @"C:\nonexistent_path_12345", null, AgentTaskStatus.Completed, TimeSpan.FromMinutes(1));
            Assert.Contains("TASK COMPLETION SUMMARY", result);
            Assert.Contains("Completed", result);
        }
    }
}
