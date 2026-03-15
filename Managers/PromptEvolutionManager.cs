using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Tracks prompt variant success rates in SQLite and uses Haiku to mutate
    /// underperforming prompt blocks. A/B tests mutated variants against control
    /// to self-optimize prompt templates over time.
    /// </summary>
    public class PromptEvolutionManager
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly object _lock = new();
        private readonly FeedbackStore _feedbackStore;
        private readonly Random _random = new();

        private const string MutationJsonSchema =
            """{"type":"object","properties":{"mutated_prompt":{"type":"string"},"reasoning":{"type":"string"},"changes_made":{"type":"array","items":{"type":"string"}}},"required":["mutated_prompt","reasoning","changes_made"]}""";

        public PromptEvolutionManager(FeedbackStore feedbackStore)
        {
            _feedbackStore = feedbackStore;
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spritely");
            Directory.CreateDirectory(appDataDir);
            _dbPath = Path.Combine(appDataDir, "prompt_evolution.db");
            _connectionString = $"Data Source={_dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS prompt_variants (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        variant_hash TEXT NOT NULL,
                        project_path TEXT NOT NULL,
                        block_name TEXT NOT NULL,
                        original_text TEXT NOT NULL,
                        mutated_text TEXT NOT NULL,
                        failure_patterns TEXT NOT NULL DEFAULT '',
                        created_at TEXT NOT NULL,
                        is_active INTEGER NOT NULL DEFAULT 1,
                        trial_count INTEGER NOT NULL DEFAULT 0,
                        success_count INTEGER NOT NULL DEFAULT 0,
                        control_trial_count INTEGER NOT NULL DEFAULT 0,
                        control_success_count INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS task_variant_assignments (
                        task_id TEXT PRIMARY KEY,
                        variant_id INTEGER NOT NULL,
                        used_variant INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY (variant_id) REFERENCES prompt_variants(id)
                    );

                    CREATE INDEX IF NOT EXISTS idx_variants_project
                        ON prompt_variants(project_path, is_active);
                    CREATE INDEX IF NOT EXISTS idx_variants_hash
                        ON prompt_variants(variant_hash);
                    """;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("PromptEvolution", "Failed to initialize SQLite database", ex);
            }
        }

        /// <summary>
        /// Returns the active variant for a project, or null if no A/B test is running.
        /// Assigns the task to either the variant or control group (50/50).
        /// </summary>
        public PromptVariant? GetActiveVariant(string projectPath, string taskId)
        {
            try
            {
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    var variant = LoadActiveVariant(conn, projectPath);
                    if (variant == null) return null;

                    // 50% assignment: use variant or control
                    var useVariant = _random.Next(2) == 0;

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO task_variant_assignments (task_id, variant_id, used_variant)
                        VALUES ($taskId, $variantId, $usedVariant)
                        """;
                    cmd.Parameters.AddWithValue("$taskId", taskId);
                    cmd.Parameters.AddWithValue("$variantId", variant.Id);
                    cmd.Parameters.AddWithValue("$usedVariant", useVariant ? 1 : 0);
                    cmd.ExecuteNonQuery();

                    return useVariant ? variant : null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PromptEvolution", "Failed to get active variant", ex);
                return null;
            }
        }

        /// <summary>
        /// Records the outcome of a task and updates variant/control statistics.
        /// Triggers mutation check if enough feedback has accumulated.
        /// </summary>
        public void RecordOutcome(string taskId, string projectPath, bool success)
        {
            try
            {
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    // Check if this task was assigned to a variant
                    using var lookupCmd = conn.CreateCommand();
                    lookupCmd.CommandText = """
                        SELECT variant_id, used_variant FROM task_variant_assignments
                        WHERE task_id = $taskId
                        """;
                    lookupCmd.Parameters.AddWithValue("$taskId", taskId);
                    using var reader = lookupCmd.ExecuteReader();

                    if (reader.Read())
                    {
                        var variantId = reader.GetInt64(0);
                        var usedVariant = reader.GetInt32(1) == 1;

                        using var updateCmd = conn.CreateCommand();
                        if (usedVariant)
                        {
                            updateCmd.CommandText = """
                                UPDATE prompt_variants
                                SET trial_count = trial_count + 1,
                                    success_count = success_count + $success
                                WHERE id = $id
                                """;
                        }
                        else
                        {
                            updateCmd.CommandText = """
                                UPDATE prompt_variants
                                SET control_trial_count = control_trial_count + 1,
                                    control_success_count = control_success_count + $success
                                WHERE id = $id
                                """;
                        }
                        updateCmd.Parameters.AddWithValue("$id", variantId);
                        updateCmd.Parameters.AddWithValue("$success", success ? 1 : 0);
                        updateCmd.ExecuteNonQuery();

                        // Check if we can make a decision on this variant
                        EvaluateVariant(conn, variantId);
                    }
                }

                // Fire-and-forget: check if we should create a new mutation
                _ = TryCreateMutationAsync(projectPath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PromptEvolution", "Failed to record outcome", ex);
            }
        }

        /// <summary>
        /// Applies a variant's mutated text to the system prompt if a variant is active
        /// and the task was assigned to the variant group.
        /// </summary>
        public string ApplyVariant(string systemPrompt, PromptVariant? variant)
        {
            if (variant == null) return systemPrompt;

            // Replace the original block within the system prompt with the mutated version
            if (systemPrompt.Contains(variant.OriginalText))
                return systemPrompt.Replace(variant.OriginalText, variant.MutatedText);

            // If exact match fails, append the mutation as an addendum
            return systemPrompt + "\n\n# EVOLVED PROMPT ENHANCEMENT\n" + variant.MutatedText;
        }

        /// <summary>
        /// Gets statistics about prompt evolution for a project.
        /// </summary>
        public List<PromptVariant> GetVariantHistory(string projectPath, int limit = 20)
        {
            var variants = new List<PromptVariant>();
            try
            {
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        SELECT id, variant_hash, project_path, block_name,
                               original_text, mutated_text, failure_patterns,
                               created_at, is_active, trial_count, success_count,
                               control_trial_count, control_success_count
                        FROM prompt_variants
                        WHERE project_path = $projectPath
                        ORDER BY created_at DESC
                        LIMIT $limit
                        """;
                    cmd.Parameters.AddWithValue("$projectPath", projectPath);
                    cmd.Parameters.AddWithValue("$limit", limit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        variants.Add(ReadVariantFromRow(reader));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PromptEvolution", "Failed to load variant history", ex);
            }
            return variants;
        }

        private void EvaluateVariant(SqliteConnection conn, long variantId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT trial_count, success_count, control_trial_count, control_success_count
                FROM prompt_variants WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", variantId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return;

            var trialCount = reader.GetInt32(0);
            var successCount = reader.GetInt32(1);
            var controlTrialCount = reader.GetInt32(2);
            var controlSuccessCount = reader.GetInt32(3);
            var totalTrials = trialCount + controlTrialCount;

            if (totalTrials < AppConstants.PromptEvolutionAbTestSize) return;

            var variantRate = trialCount > 0 ? (double)successCount / trialCount : 0;
            var controlRate = controlTrialCount > 0 ? (double)controlSuccessCount / controlTrialCount : 0;

            // Deactivate the variant — the A/B test is complete
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE prompt_variants SET is_active = 0 WHERE id = $id";
            updateCmd.Parameters.AddWithValue("$id", variantId);
            updateCmd.ExecuteNonQuery();

            var outcome = variantRate > controlRate ? "WINNER" : "LOSER";
            AppLogger.Info("PromptEvolution",
                $"A/B test complete: variant #{variantId} is {outcome} " +
                $"(variant: {variantRate:P0} [{successCount}/{trialCount}], " +
                $"control: {controlRate:P0} [{controlSuccessCount}/{controlTrialCount}])");
        }

        private async Task TryCreateMutationAsync(string projectPath, CancellationToken ct = default)
        {
            try
            {
                // Don't create a new mutation if one is already active
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    if (LoadActiveVariant(conn, projectPath) != null) return;
                }

                // Check if we have enough feedback data
                var entries = _feedbackStore.LoadEntries(projectPath);
                if (entries.Count < AppConstants.PromptEvolutionThreshold) return;

                // Analyze recent failure patterns
                var recentEntries = entries.TakeLast(AppConstants.PromptEvolutionAnalysisWindow).ToList();
                var failedEntries = recentEntries.Where(e =>
                    e.Status != "Completed" && e.Status != "Recommendation").ToList();

                if (failedEntries.Count < 3) return; // Not enough failures to warrant mutation

                var failurePatterns = failedEntries
                    .SelectMany(e => e.FailureFactors)
                    .GroupBy(f => f)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => $"{g.Key} ({g.Count()}x)")
                    .ToList();

                if (failurePatterns.Count == 0) return;

                // Get the current system prompt block to mutate
                var originalBlock = ExtractMutableBlock(PromptBuilder.DefaultSystemPrompt);
                if (string.IsNullOrWhiteSpace(originalBlock)) return;

                var mutationPrompt = BuildMutationPrompt(originalBlock, failurePatterns, recentEntries);

                var result = await FeatureSystemCliRunner.RunAsync(
                    mutationPrompt,
                    MutationJsonSchema,
                    "PromptEvolution",
                    TimeSpan.FromMinutes(2),
                    ct);

                if (result == null) return;

                var mutatedText = result.Value.TryGetProperty("mutated_prompt", out var mp)
                    ? mp.GetString() ?? "" : "";
                var reasoning = result.Value.TryGetProperty("reasoning", out var r)
                    ? r.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(mutatedText) || mutatedText == originalBlock)
                    return;

                var hash = ComputeHash(mutatedText);

                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    // Don't create duplicate variants
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = "SELECT COUNT(*) FROM prompt_variants WHERE variant_hash = $hash";
                    checkCmd.Parameters.AddWithValue("$hash", hash);
                    if ((long)checkCmd.ExecuteScalar()! > 0) return;

                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = """
                        INSERT INTO prompt_variants
                            (variant_hash, project_path, block_name, original_text, mutated_text,
                             failure_patterns, created_at, is_active)
                        VALUES ($hash, $project, $block, $original, $mutated, $patterns, $created, 1)
                        """;
                    insertCmd.Parameters.AddWithValue("$hash", hash);
                    insertCmd.Parameters.AddWithValue("$project", projectPath);
                    insertCmd.Parameters.AddWithValue("$block", "DefaultSystemPrompt");
                    insertCmd.Parameters.AddWithValue("$original", originalBlock);
                    insertCmd.Parameters.AddWithValue("$mutated", mutatedText);
                    insertCmd.Parameters.AddWithValue("$patterns", string.Join("; ", failurePatterns));
                    insertCmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
                    insertCmd.ExecuteNonQuery();
                }

                AppLogger.Info("PromptEvolution",
                    $"Created new prompt variant for {projectPath}: {reasoning[..Math.Min(200, reasoning.Length)]}");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PromptEvolution", "Mutation creation failed (non-critical)", ex);
            }
        }

        private static string BuildMutationPrompt(string originalBlock, List<string> failurePatterns,
            List<FeedbackEntry> recentEntries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a prompt engineer optimizing an AI coding assistant's system prompt.");
            sb.AppendLine("The assistant executes coding tasks against local repositories.");
            sb.AppendLine();
            sb.AppendLine("## Current System Prompt Block");
            sb.AppendLine("```");
            sb.AppendLine(originalBlock);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Observed Failure Patterns");
            foreach (var p in failurePatterns)
                sb.AppendLine($"- {p}");
            sb.AppendLine();
            sb.AppendLine("## Recent Task Results");
            foreach (var entry in recentEntries.TakeLast(10))
            {
                sb.AppendLine($"- [{entry.Status}] {entry.Description[..Math.Min(100, entry.Description.Length)]}");
                if (entry.FailureFactors.Count > 0)
                    sb.AppendLine($"  Failures: {string.Join(", ", entry.FailureFactors)}");
            }
            sb.AppendLine();
            sb.AppendLine("## Instructions");
            sb.AppendLine("Mutate the system prompt block to address the observed failure patterns.");
            sb.AppendLine("Keep the same overall structure and intent, but adjust wording, emphasis,");
            sb.AppendLine("or add targeted instructions that would reduce the failure rate.");
            sb.AppendLine("Do NOT remove critical safety rules (no secrets, git safety, etc).");
            sb.AppendLine("Return the complete mutated prompt block in 'mutated_prompt'.");

            return sb.ToString();
        }

        /// <summary>
        /// Extracts the core mutable section of the system prompt (the rules/guidelines portion).
        /// Avoids mutating structural elements like headers.
        /// </summary>
        private static string ExtractMutableBlock(string systemPrompt)
        {
            // Use the full system prompt as the mutable block — Haiku will preserve structure
            if (systemPrompt.Length > 4000)
                return systemPrompt[..4000];
            return systemPrompt;
        }

        private static PromptVariant? LoadActiveVariant(SqliteConnection conn, string projectPath)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, variant_hash, project_path, block_name,
                       original_text, mutated_text, failure_patterns,
                       created_at, is_active, trial_count, success_count,
                       control_trial_count, control_success_count
                FROM prompt_variants
                WHERE project_path = $projectPath AND is_active = 1
                ORDER BY created_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$projectPath", projectPath);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadVariantFromRow(reader) : null;
        }

        private static PromptVariant ReadVariantFromRow(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt64(0),
            VariantHash = reader.GetString(1),
            ProjectPath = reader.GetString(2),
            BlockName = reader.GetString(3),
            OriginalText = reader.GetString(4),
            MutatedText = reader.GetString(5),
            FailurePatterns = reader.GetString(6),
            CreatedAt = DateTime.TryParse(reader.GetString(7), out var dt) ? dt : DateTime.UtcNow,
            IsActive = reader.GetInt32(8) == 1,
            TrialCount = reader.GetInt32(9),
            SuccessCount = reader.GetInt32(10),
            ControlTrialCount = reader.GetInt32(11),
            ControlSuccessCount = reader.GetInt32(12)
        };

        private static string ComputeHash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexStringLower(bytes)[..16];
        }

        /// <summary>Cleans up old completed variants, keeping the last N per project.</summary>
        public void CleanupOldVariants(string projectPath, int keepCount = 20)
        {
            try
            {
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM prompt_variants
                        WHERE project_path = $projectPath AND is_active = 0
                        AND id NOT IN (
                            SELECT id FROM prompt_variants
                            WHERE project_path = $projectPath AND is_active = 0
                            ORDER BY created_at DESC
                            LIMIT $keepCount
                        )
                        """;
                    cmd.Parameters.AddWithValue("$projectPath", projectPath);
                    cmd.Parameters.AddWithValue("$keepCount", keepCount);
                    var deleted = cmd.ExecuteNonQuery();
                    if (deleted > 0)
                        AppLogger.Debug("PromptEvolution", $"Cleaned up {deleted} old variants for {projectPath}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PromptEvolution", "Variant cleanup failed", ex);
            }
        }
    }
}
