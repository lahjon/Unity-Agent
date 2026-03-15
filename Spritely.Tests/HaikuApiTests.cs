using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Managers;
using Xunit;
using Xunit.Abstractions;

namespace Spritely.Tests
{
    /// <summary>
    /// Integration tests that invoke Claude CLI with the Haiku model.
    /// </summary>
    public class HaikuApiTests
    {
        private readonly ITestOutputHelper _output;

        public HaikuApiTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Haiku_CliPrompt_ReturnsValidResponse()
        {
            var model = AppConstants.ClaudeHaiku;
            _output.WriteLine($"Using model: {model}");

            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = $"-p --model {model}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.StandardInput.WriteLineAsync("Reply with exactly: HAIKU_OK");
            process.StandardInput.Close();

            var exited = process.WaitForExit(30_000);
            if (!exited)
            {
                process.Kill();
                Assert.Fail("Claude CLI timed out after 30 seconds.");
            }

            await Task.Delay(200);

            var output = stdout.ToString().Trim();
            var errors = stderr.ToString().Trim();

            _output.WriteLine($"Exit code: {process.ExitCode}");
            _output.WriteLine($"Stdout: {output}");
            if (!string.IsNullOrEmpty(errors))
                _output.WriteLine($"Stderr: {errors}");

            Assert.Equal(0, process.ExitCode);
            Assert.NotEmpty(output);
            Assert.Contains("HAIKU_OK", output);
        }

        [Fact]
        public async Task Haiku_Preprocessor_ReturnsValidResult()
        {
            _output.WriteLine($"Preprocessor using model: {AppConstants.ClaudeHaiku}");

            var preprocessor = new TaskPreprocessor();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var result = await preprocessor.PreprocessAsync(
                "Fix the null reference exception in UserService.GetById when the user doesn't exist",
                cts.Token);

            Assert.NotNull(result);

            _output.WriteLine($"Header: {result!.Header}");
            _output.WriteLine($"Enhanced prompt: {result.EnhancedPrompt}");
            _output.WriteLine($"ApplyFix: {result.ApplyFix}");
            _output.WriteLine($"ExtendedPlanning: {result.ExtendedPlanning}");
            _output.WriteLine($"TeamsMode: {result.IsTeamsMode}");
            _output.WriteLine($"AutoDecompose: {result.AutoDecompose}");
            _output.WriteLine($"SpawnTeam: {result.SpawnTeam}");
            _output.WriteLine($"UseMcp: {result.UseMcp}");
            _output.WriteLine($"Iterations: {result.Iterations}");

            // Header should be 2-5 words
            var words = result.Header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.InRange(words.Length, 1, 5);

            // Enhanced prompt should not be empty
            Assert.NotEmpty(result.EnhancedPrompt);

            // A simple bug fix should have sensible defaults
            Assert.True(result.ApplyFix, "A bug fix task should have ApplyFix=true");
            Assert.False(result.IsTeamsMode, "A simple fix should not trigger teams mode");
            Assert.False(result.AutoDecompose, "A simple fix should not auto-decompose");
            Assert.False(result.SpawnTeam, "A simple fix should not spawn a team");
            Assert.False(result.UseMcp, "A non-Unity task should not use MCP");
        }
    }
}
