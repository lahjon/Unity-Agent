using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace HappyEngine.Tests
{
    public class FollowUpProcessTests
    {
        private readonly ITestOutputHelper _output;

        public FollowUpProcessTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TestFollowUpScriptExecution()
        {
            // Create a test PowerShell script that simulates a follow-up
            var scriptPath = Path.Combine(Path.GetTempPath(), $"test_followup_{Guid.NewGuid()}.ps1");
            var scriptContent = @"
Write-Output 'Starting follow-up test...'
Write-Output ('{""type"":""system"",""message"":""Test system message""}')
Write-Output ('{""type"":""assistant"",""message"":{""content"":[{""type"":""text"",""text"":""Hello from test""}]}}')
Write-Output 'Test completed.'
";
            File.WriteAllText(scriptPath, scriptContent);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var outputReceived = false;
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        outputReceived = true;
                        outputBuilder.AppendLine(e.Data);
                        _output.WriteLine($"STDOUT: {e.Data}");
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        errorBuilder.AppendLine(e.Data);
                        _output.WriteLine($"STDERR: {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for process to complete
                var exited = await Task.Run(() => process.WaitForExit(5000));

                Assert.True(exited, "Process should exit within 5 seconds");
                Assert.True(outputReceived, "Should receive output from the script");
                Assert.Contains("Starting follow-up test", outputBuilder.ToString());
                Assert.Contains("Test completed", outputBuilder.ToString());

                _output.WriteLine($"Exit code: {process.ExitCode}");
                _output.WriteLine($"Total output: {outputBuilder}");
                _output.WriteLine($"Total errors: {errorBuilder}");
            }
            finally
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
        }

        [Fact]
        public async Task TestActualClaudeCommand()
        {
            // Test if claude command works in a PowerShell script
            var scriptPath = Path.Combine(Path.GetTempPath(), $"test_claude_{Guid.NewGuid()}.ps1");
            var scriptContent = @"
$ErrorActionPreference = 'Continue'
Write-Output 'Testing claude command...'
$env:CLAUDECODE = $null

# First test if claude exists
$claudeExists = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claudeExists) {
    Write-Output 'ERROR: Claude command not found'
    exit 1
}

Write-Output 'Claude found, testing version...'
& claude --version 2>&1 | ForEach-Object { Write-Output ""Claude output: $_"" }

Write-Output 'Testing simple prompt...'
& claude -p --verbose --output-format stream-json ""test"" 2>&1 | Select-Object -First 3 | ForEach-Object { Write-Output ""Stream: $_"" }

Write-Output 'Test completed.'
";
            File.WriteAllText(scriptPath, scriptContent);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                        _output.WriteLine($"STDOUT: {e.Data}");
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        errorBuilder.AppendLine(e.Data);
                        _output.WriteLine($"STDERR: {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for process to complete (longer timeout for claude)
                var exited = await Task.Run(() => process.WaitForExit(30000));

                Assert.True(exited, "Process should exit within 30 seconds");
                var output = outputBuilder.ToString();
                Assert.Contains("Claude found", output);

                _output.WriteLine($"Exit code: {process.ExitCode}");
            }
            finally
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
        }
    }
}