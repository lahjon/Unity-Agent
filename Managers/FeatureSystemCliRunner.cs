using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// Shared CLI invocation helper for feature system Haiku/Sonnet calls.
    /// Encapsulates ProcessStartInfo setup, stdin writing, stdout/stderr reading,
    /// timeout handling, and JSON unwrapping (structured_output/result fallback).
    /// </summary>
    public static class FeatureSystemCliRunner
    {
        /// <summary>
        /// Runs a Claude CLI call with the given prompt and JSON schema, returning the
        /// unwrapped JSON root element. Returns null on any failure (timeout, bad exit code,
        /// empty output, parse error).
        /// </summary>
        public static async Task<JsonElement?> RunAsync(
            string prompt,
            string jsonSchema,
            string callerName,
            TimeSpan timeout,
            CancellationToken ct = default,
            string? model = null)
        {
            model ??= AppConstants.ClaudeHaiku;

            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = $"-p --output-format json --model {model} --max-turns 3 --json-schema \"{jsonSchema.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);
            var linked = linkedCts.Token;

            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linked);
                process.StandardInput.Close();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn(callerName,
                    $"Cancelled during stdin write (PID: {process.Id}). Killing process.");
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            catch (IOException ioEx)
            {
                var earlyStderr = "";
                try { earlyStderr = await process.StandardError.ReadToEndAsync(CancellationToken.None); } catch { }
                AppLogger.Error(callerName,
                    $"Failed to write prompt to CLI stdin (pipe closed). stderr: {earlyStderr}", ioEx);
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked);
            var stderrTask = process.StandardError.ReadToEndAsync(linked);

            try
            {
                await process.WaitForExitAsync(linked);
            }
            catch (OperationCanceledException)
            {
                var reason = ct.IsCancellationRequested
                    ? "external cancellation"
                    : $"timeout after {timeout.TotalMinutes:F0} minutes";
                AppLogger.Warn(callerName,
                    $"CLI cancelled ({reason}, PID: {process.Id}). Killing process.");
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var output = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stderr))
                AppLogger.Warn(callerName, $"CLI stderr: {stderr[..Math.Min(500, stderr.Length)]}");

            if (process.ExitCode != 0)
            {
                AppLogger.Error(callerName,
                    $"CLI exited with code {process.ExitCode}. stderr: {stderr[..Math.Min(1000, stderr.Length)]}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                AppLogger.Warn(callerName, "CLI returned empty output");
                return null;
            }

            AppLogger.Info(callerName, $"CLI completed. stdout length: {output.Length}");

            var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

            using var doc = JsonDocument.Parse(text);
            var wrapper = doc.RootElement;

            // The CLI with --output-format json + --json-schema puts structured data
            // in "structured_output"; fall back to "result" for plain text responses
            if (wrapper.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                return structured.Clone();
            }

            if (wrapper.TryGetProperty("result", out var resultElement))
            {
                if (resultElement.ValueKind == JsonValueKind.String)
                {
                    using var innerDoc = JsonDocument.Parse(resultElement.GetString()!);
                    return innerDoc.RootElement.Clone();
                }
                return resultElement.Clone();
            }

            return wrapper.Clone();
        }
    }
}
