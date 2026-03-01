using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HappyEngine.Constants;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Service for intelligent truncation of task results to preserve important information
    /// while staying within token limits.
    /// </summary>
    public class SmartTruncationService
    {
        // Section importance scores
        private const double ErrorScore = 1.0;
        private const double DecisionScore = 0.9;
        private const double FindingScore = 0.8;
        private const double ImplementationScore = 0.7;
        private const double DetailScore = 0.6;
        private const double ContextScore = 0.5;
        private const double VerboseScore = 0.3;

        // Minimum section size to keep (prevents fragmentation)
        private const int MinSectionSize = 100;

        // Pattern matchers for content categorization
        private static readonly Dictionary<ContentType, List<Regex>> ContentPatterns = new()
        {
            [ContentType.Error] = new List<Regex>
            {
                new Regex(@"(?:ERROR|FAILED|EXCEPTION|Fatal|Critical)[\s:]+", RegexOptions.IgnoreCase),
                new Regex(@"^\s*(?:✗|❌|⚠️)", RegexOptions.Multiline),
                new Regex(@"(?:Stack trace|Traceback):", RegexOptions.IgnoreCase),
                new Regex(@"(?:Could not|Unable to|Failed to)", RegexOptions.IgnoreCase)
            },
            [ContentType.Decision] = new List<Regex>
            {
                new Regex(@"(?:DECISION|CHOSE|SELECTED|DETERMINED)[\s:]+", RegexOptions.IgnoreCase),
                new Regex(@"(?:I (?:will|decided to|chose to|selected)|Let me)", RegexOptions.IgnoreCase),
                new Regex(@"(?:Approach|Strategy|Solution):", RegexOptions.IgnoreCase)
            },
            [ContentType.Finding] = new List<Regex>
            {
                new Regex(@"(?:FOUND|DISCOVERED|IDENTIFIED|DETECTED)[\s:]+", RegexOptions.IgnoreCase),
                new Regex(@"(?:Key (?:finding|insight|observation)|Important)", RegexOptions.IgnoreCase),
                new Regex(@"^\s*(?:✓|✔️|👍)", RegexOptions.Multiline)
            },
            [ContentType.Implementation] = new List<Regex>
            {
                new Regex(@"(?:IMPLEMENTED|ADDED|CREATED|MODIFIED)[\s:]+", RegexOptions.IgnoreCase),
                new Regex(@"(?:Created|Modified|Updated|Added) (?:file|class|method|function)", RegexOptions.IgnoreCase),
                new Regex(@"```[\w]*\n[\s\S]*?```", RegexOptions.Multiline) // Code blocks
            },
            [ContentType.Detail] = new List<Regex>
            {
                new Regex(@"(?:Details?|Note|Info|Summary):", RegexOptions.IgnoreCase),
                new Regex(@"^\s*[-•]", RegexOptions.Multiline) // Bullet points
            }
        };

        /// <summary>
        /// Truncates content intelligently while preserving the most important information.
        /// </summary>
        public TruncatedResult TruncateWithSemantics(string content, int maxTokens, TruncationPriority priority)
        {
            // Estimate characters from tokens (rough approximation: 1 token ≈ 4 chars)
            var maxChars = maxTokens * 4;

            if (content.Length <= maxChars)
            {
                return new TruncatedResult
                {
                    Content = content,
                    OriginalLength = content.Length,
                    TruncatedLength = content.Length,
                    PreservedSections = new List<string> { "Full content" },
                    TruncatedSections = new List<string>(),
                    ImportanceScore = 1.0
                };
            }

            // Parse content into scored sections
            var sections = ParseAndScoreSections(content);

            // Apply truncation based on priority
            var truncatedContent = ApplyTruncation(sections, maxChars, priority);

            // Build result
            var result = new TruncatedResult
            {
                Content = truncatedContent.Content,
                OriginalLength = content.Length,
                TruncatedLength = truncatedContent.Content.Length,
                PreservedSections = truncatedContent.PreservedSections,
                TruncatedSections = truncatedContent.TruncatedSections,
                ImportanceScore = truncatedContent.AverageImportance,
                TruncationMetrics = new TruncationMetrics
                {
                    ErrorsPreserved = truncatedContent.PreservedByType[ContentType.Error],
                    DecisionsPreserved = truncatedContent.PreservedByType[ContentType.Decision],
                    FindingsPreserved = truncatedContent.PreservedByType[ContentType.Finding],
                    TotalSectionsAnalyzed = sections.Count,
                    TruncationRatio = 1.0 - ((double)truncatedContent.Content.Length / content.Length)
                }
            };

            return result;
        }

        /// <summary>
        /// Truncates multiple child results while maintaining balance across all children.
        /// </summary>
        public List<TruncatedResult> TruncateMultipleResults(
            Dictionary<string, string> childResults,
            int totalMaxTokens,
            bool preserveBalance = true)
        {
            var results = new List<TruncatedResult>();

            if (!childResults.Any())
                return results;

            // Calculate token budget per child
            int tokenBudgetPerChild;
            if (preserveBalance)
            {
                // Equal distribution
                tokenBudgetPerChild = totalMaxTokens / childResults.Count;
            }
            else
            {
                // Proportional distribution based on content length
                var totalLength = childResults.Values.Sum(c => c.Length);
                tokenBudgetPerChild = totalMaxTokens / childResults.Count; // Base allocation
            }

            foreach (var kvp in childResults)
            {
                var childBudget = tokenBudgetPerChild;

                // Adjust budget for very short results
                if (!preserveBalance && kvp.Value.Length < tokenBudgetPerChild * 2)
                {
                    childBudget = kvp.Value.Length / 4; // Rough token estimate
                }

                var truncated = TruncateWithSemantics(
                    kvp.Value,
                    childBudget,
                    TruncationPriority.Balanced);

                truncated.ChildId = kvp.Key;
                results.Add(truncated);
            }

            // If we have leftover budget due to short results, redistribute to longer ones
            if (!preserveBalance)
            {
                RedistributeUnusedBudget(results, childResults, totalMaxTokens);
            }

            return results;
        }

        private List<ScoredSection> ParseAndScoreSections(string content)
        {
            var sections = new List<ScoredSection>();
            var lines = content.Split('\n');
            var currentSection = new StringBuilder();
            var currentType = ContentType.Context;
            var currentScore = ContextScore;
            var lineNumber = 0;

            foreach (var line in lines)
            {
                lineNumber++;
                var lineType = DetermineContentType(line);
                var lineScore = GetScoreForType(lineType);

                // Check if we should start a new section
                if (lineType != currentType || Math.Abs(lineScore - currentScore) > 0.3)
                {
                    if (currentSection.Length > MinSectionSize)
                    {
                        sections.Add(new ScoredSection
                        {
                            Content = currentSection.ToString(),
                            Type = currentType,
                            Score = currentScore,
                            StartLine = lineNumber - currentSection.ToString().Count(c => c == '\n'),
                            EndLine = lineNumber - 1
                        });
                        currentSection.Clear();
                    }

                    currentType = lineType;
                    currentScore = lineScore;
                }

                currentSection.AppendLine(line);
            }

            // Add the last section
            if (currentSection.Length > 0)
            {
                sections.Add(new ScoredSection
                {
                    Content = currentSection.ToString(),
                    Type = currentType,
                    Score = currentScore,
                    StartLine = lineNumber - currentSection.ToString().Count(c => c == '\n'),
                    EndLine = lineNumber
                });
            }

            return sections;
        }

        private ContentType DetermineContentType(string line)
        {
            foreach (var kvp in ContentPatterns)
            {
                if (kvp.Value.Any(pattern => pattern.IsMatch(line)))
                {
                    return kvp.Key;
                }
            }

            // Check for verbose/repetitive content
            if (string.IsNullOrWhiteSpace(line) || line.Length > 200)
                return ContentType.Verbose;

            return ContentType.Context;
        }

        private double GetScoreForType(ContentType type) => type switch
        {
            ContentType.Error => ErrorScore,
            ContentType.Decision => DecisionScore,
            ContentType.Finding => FindingScore,
            ContentType.Implementation => ImplementationScore,
            ContentType.Detail => DetailScore,
            ContentType.Context => ContextScore,
            ContentType.Verbose => VerboseScore,
            _ => ContextScore
        };

        private TruncationResult ApplyTruncation(
            List<ScoredSection> sections,
            int maxChars,
            TruncationPriority priority)
        {
            var result = new TruncationResult
            {
                PreservedByType = Enum.GetValues<ContentType>().ToDictionary(t => t, _ => 0)
            };

            // Sort sections by importance
            var sortedSections = priority switch
            {
                TruncationPriority.ErrorsFirst => sections.OrderByDescending(s => s.Type == ContentType.Error)
                    .ThenByDescending(s => s.Score).ToList(),
                TruncationPriority.DecisionsFirst => sections.OrderByDescending(s => s.Type == ContentType.Decision)
                    .ThenByDescending(s => s.Score).ToList(),
                _ => sections.OrderByDescending(s => s.Score).ToList()
            };

            var sb = new StringBuilder();
            var currentLength = 0;
            var totalScore = 0.0;
            var preservedCount = 0;

            // Always try to preserve high-priority content
            foreach (var section in sortedSections)
            {
                if (section.Score >= 0.8) // High priority threshold
                {
                    if (currentLength + section.Content.Length <= maxChars)
                    {
                        sb.Append(section.Content);
                        currentLength += section.Content.Length;
                        totalScore += section.Score;
                        preservedCount++;
                        result.PreservedSections.Add($"{section.Type} (lines {section.StartLine}-{section.EndLine})");
                        result.PreservedByType[section.Type]++;
                    }
                    else if (currentLength < maxChars * 0.8) // Still have some room
                    {
                        // Truncate this section to fit
                        var remainingSpace = maxChars - currentLength - 50; // Leave buffer for truncation marker
                        if (remainingSpace > MinSectionSize)
                        {
                            var truncated = section.Content.Substring(0, remainingSpace);
                            sb.Append(truncated);
                            sb.AppendLine("\n... [TRUNCATED - High priority content continues] ...");
                            currentLength += truncated.Length + 50;
                            result.TruncatedSections.Add($"{section.Type} (partial)");
                            break;
                        }
                    }
                }
            }

            // Fill remaining space with medium priority content
            foreach (var section in sortedSections.Where(s => s.Score >= 0.5 && s.Score < 0.8))
            {
                if (currentLength >= maxChars * 0.9)
                    break;

                if (currentLength + section.Content.Length <= maxChars)
                {
                    sb.Append(section.Content);
                    currentLength += section.Content.Length;
                    totalScore += section.Score;
                    preservedCount++;
                    result.PreservedSections.Add($"{section.Type} (lines {section.StartLine}-{section.EndLine})");
                    result.PreservedByType[section.Type]++;
                }
                else
                {
                    // Summarize medium priority content
                    var summary = SummarizeSection(section);
                    if (currentLength + summary.Length <= maxChars)
                    {
                        sb.AppendLine($"\n[SUMMARY - {section.Type}]: {summary}");
                        currentLength += summary.Length + 20;
                        result.TruncatedSections.Add($"{section.Type} (summarized)");
                    }
                }
            }

            // Add truncation notice if significant content was removed
            if (sections.Count > preservedCount)
            {
                sb.AppendLine($"\n... [{sections.Count - preservedCount} sections truncated for token limit] ...");
            }

            result.Content = sb.ToString();
            result.AverageImportance = preservedCount > 0 ? totalScore / preservedCount : 0;

            return result;
        }

        private string SummarizeSection(ScoredSection section)
        {
            // Extract key information based on section type
            var summary = section.Type switch
            {
                ContentType.Implementation => ExtractImplementationSummary(section.Content),
                ContentType.Finding => ExtractFindingSummary(section.Content),
                ContentType.Detail => ExtractDetailSummary(section.Content),
                _ => section.Content.Length > 200
                    ? section.Content.Substring(0, 150) + "..."
                    : section.Content.Trim()
            };

            return summary;
        }

        private string ExtractImplementationSummary(string content)
        {
            // Look for file names and method/class names
            var fileMatch = Regex.Match(content, @"(?:file|File):\s*([^\s,]+)", RegexOptions.IgnoreCase);
            var methodMatch = Regex.Match(content, @"(?:method|function|class):\s*(\w+)", RegexOptions.IgnoreCase);

            var parts = new List<string>();
            if (fileMatch.Success) parts.Add($"File: {fileMatch.Groups[1].Value}");
            if (methodMatch.Success) parts.Add($"Changed: {methodMatch.Groups[1].Value}");

            return parts.Any()
                ? string.Join(", ", parts)
                : "Implementation changes made";
        }

        private string ExtractFindingSummary(string content)
        {
            // Extract first sentence or key point
            var sentences = content.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            return sentences.FirstOrDefault()?.Trim() ?? "Finding recorded";
        }

        private string ExtractDetailSummary(string content)
        {
            // Count items if it's a list
            var bulletPoints = Regex.Matches(content, @"^\s*[-•*]\s*", RegexOptions.Multiline).Count;
            if (bulletPoints > 0)
                return $"{bulletPoints} detail items";

            return content.Length > 100
                ? content.Substring(0, 80) + "..."
                : content.Trim();
        }

        private void RedistributeUnusedBudget(
            List<TruncatedResult> results,
            Dictionary<string, string> originalContent,
            int totalMaxTokens)
        {
            var usedTokens = results.Sum(r => r.TruncatedLength / 4); // Rough estimate
            var remainingBudget = totalMaxTokens - usedTokens;

            if (remainingBudget <= 100) // Not worth redistributing
                return;

            // Find results that were significantly truncated
            var needsMore = results
                .Where(r => r.TruncationMetrics.TruncationRatio > 0.5)
                .OrderByDescending(r => r.OriginalLength)
                .ToList();

            foreach (var result in needsMore)
            {
                if (remainingBudget <= 0)
                    break;

                var additionalBudget = Math.Min(remainingBudget, result.OriginalLength / 4 / 2); // Up to 50% more
                var newResult = TruncateWithSemantics(
                    originalContent[result.ChildId!],
                    (result.TruncatedLength / 4) + additionalBudget,
                    TruncationPriority.Balanced);

                // Update the result
                result.Content = newResult.Content;
                result.TruncatedLength = newResult.TruncatedLength;
                result.PreservedSections = newResult.PreservedSections;
                result.TruncatedSections = newResult.TruncatedSections;
                result.ImportanceScore = newResult.ImportanceScore;
                result.TruncationMetrics = newResult.TruncationMetrics;

                remainingBudget -= additionalBudget;
            }
        }

        // Data structures
        private enum ContentType
        {
            Error,
            Decision,
            Finding,
            Implementation,
            Detail,
            Context,
            Verbose
        }

        private class ScoredSection
        {
            public string Content { get; set; } = "";
            public ContentType Type { get; set; }
            public double Score { get; set; }
            public int StartLine { get; set; }
            public int EndLine { get; set; }
        }

        private class TruncationResult
        {
            public string Content { get; set; } = "";
            public List<string> PreservedSections { get; set; } = new();
            public List<string> TruncatedSections { get; set; } = new();
            public Dictionary<ContentType, int> PreservedByType { get; set; } = new();
            public double AverageImportance { get; set; }
        }

        public class TruncatedResult
        {
            public string? ChildId { get; set; }
            public string Content { get; set; } = "";
            public int OriginalLength { get; set; }
            public int TruncatedLength { get; set; }
            public List<string> PreservedSections { get; set; } = new();
            public List<string> TruncatedSections { get; set; } = new();
            public double ImportanceScore { get; set; }
            public TruncationMetrics TruncationMetrics { get; set; } = new();
        }

        public class TruncationMetrics
        {
            public int ErrorsPreserved { get; set; }
            public int DecisionsPreserved { get; set; }
            public int FindingsPreserved { get; set; }
            public int TotalSectionsAnalyzed { get; set; }
            public double TruncationRatio { get; set; }
        }

        public enum TruncationPriority
        {
            Balanced,
            ErrorsFirst,
            DecisionsFirst,
            FindingsFirst
        }
    }
}