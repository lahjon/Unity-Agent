using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HappyEngine.Constants;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Handles progressive context reduction for tasks that hit token limits.
    /// Preserves essential context while reducing overall prompt size.
    /// </summary>
    public static class ContextReducer
    {
        private const string TokenLimitMarker = "[TOKEN_LIMIT_RETRY]";
        private const string EssentialMarker = "[ESSENTIAL]";

        /// <summary>
        /// Reduces the prompt context based on retry count and last token estimate.
        /// </summary>
        public static string ReducePromptContext(string originalPrompt, int retryCount, long lastTokenEstimate)
        {
            // Get the reduction factor based on retry count
            var reductionFactor = GetReductionFactor(retryCount);

            // Add retry context information
            var retryInfo = BuildRetryContextInfo(retryCount, lastTokenEstimate, reductionFactor);

            // Parse prompt into sections
            var sections = ParsePromptSections(originalPrompt);

            // Apply reduction strategy
            var reducedSections = ApplyReductionStrategy(sections, reductionFactor);

            // Rebuild the prompt
            return retryInfo + "\n\n" + RebuildPrompt(reducedSections);
        }

        private static double GetReductionFactor(int retryCount)
        {
            if (retryCount < 0 || retryCount >= AppConstants.TokenRetryReductionFactors.Length)
                return AppConstants.TokenRetryReductionFactors[AppConstants.TokenRetryReductionFactors.Length - 1];

            return AppConstants.TokenRetryReductionFactors[retryCount];
        }

        private static string BuildRetryContextInfo(int retryCount, long lastTokenEstimate, double reductionFactor)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{TokenLimitMarker} Retry #{retryCount + 1}");
            sb.AppendLine($"Previous attempt used approximately {lastTokenEstimate:N0} tokens.");
            sb.AppendLine($"Context has been reduced to {reductionFactor:P0} of original size.");
            sb.AppendLine("Focus on the most recent context and errors from the previous attempt.");

            return sb.ToString();
        }

        private static List<PromptSection> ParsePromptSections(string prompt)
        {
            var sections = new List<PromptSection>();
            var lines = prompt.Split('\n');
            var currentSection = new StringBuilder();
            var currentType = SectionType.General;
            var currentPriority = 0.5;

            foreach (var line in lines)
            {
                // Detect section types based on markers and content
                if (line.Contains(EssentialMarker) || IsEssentialContent(line))
                {
                    if (currentSection.Length > 0)
                    {
                        sections.Add(new PromptSection
                        {
                            Content = currentSection.ToString(),
                            Type = currentType,
                            Priority = currentPriority
                        });
                        currentSection.Clear();
                    }
                    currentType = SectionType.Essential;
                    currentPriority = 1.0;
                }
                else if (IsErrorSection(line))
                {
                    currentType = SectionType.Error;
                    currentPriority = 0.9;
                }
                else if (IsTaskDescription(line))
                {
                    currentType = SectionType.TaskDescription;
                    currentPriority = 0.95;
                }
                else if (IsHistorySection(line))
                {
                    currentType = SectionType.History;
                    currentPriority = 0.3;
                }
                else if (IsRulesSection(line))
                {
                    currentType = SectionType.Rules;
                    currentPriority = 0.4;
                }

                currentSection.AppendLine(line);
            }

            // Add the last section
            if (currentSection.Length > 0)
            {
                sections.Add(new PromptSection
                {
                    Content = currentSection.ToString(),
                    Type = currentType,
                    Priority = currentPriority
                });
            }

            return sections;
        }

        private static bool IsEssentialContent(string line)
        {
            // Identify essential content patterns
            return line.Contains("USER PROMPT", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("TASK:", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("FAILED:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsErrorSection(string line)
        {
            return line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("failed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTaskDescription(string line)
        {
            return line.StartsWith("Task:", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("USER PROMPT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHistorySection(string line)
        {
            return line.Contains("history", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("previous", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("conversation", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRulesSection(string line)
        {
            return line.Contains("RULES", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("INSTRUCTIONS", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("GUIDELINES", StringComparison.OrdinalIgnoreCase);
        }

        private static List<PromptSection> ApplyReductionStrategy(List<PromptSection> sections, double reductionFactor)
        {
            var result = new List<PromptSection>();

            // Always keep essential sections
            result.AddRange(sections.Where(s => s.Priority >= 0.9));

            // Calculate remaining budget
            var essentialSize = result.Sum(s => s.Content.Length);
            var totalOriginalSize = sections.Sum(s => s.Content.Length);
            var targetSize = (int)(totalOriginalSize * reductionFactor);
            var remainingBudget = Math.Max(0, targetSize - essentialSize);

            // Add other sections by priority until budget exhausted
            var nonEssentialSections = sections.Where(s => s.Priority < 0.9)
                                              .OrderByDescending(s => s.Priority)
                                              .ToList();

            foreach (var section in nonEssentialSections)
            {
                if (section.Content.Length <= remainingBudget)
                {
                    result.Add(section);
                    remainingBudget -= section.Content.Length;
                }
                else if (remainingBudget > 1000 && section.Priority >= 0.5)
                {
                    // Truncate medium-priority sections if there's enough budget
                    var truncated = TruncateSection(section, remainingBudget);
                    if (truncated.Content.Length > 500)
                    {
                        result.Add(truncated);
                        break;
                    }
                }
            }

            return result;
        }

        private static PromptSection TruncateSection(PromptSection section, int maxLength)
        {
            if (section.Content.Length <= maxLength)
                return section;

            var truncatedContent = section.Content.Substring(0, maxLength - 100) +
                                  "\n... [TRUNCATED FOR TOKEN LIMIT] ...\n";

            return new PromptSection
            {
                Content = truncatedContent,
                Type = section.Type,
                Priority = section.Priority
            };
        }

        private static string RebuildPrompt(List<PromptSection> sections)
        {
            var sb = new StringBuilder();

            // Group sections by type for better organization
            var grouped = sections.GroupBy(s => s.Type)
                                 .OrderByDescending(g => g.Max(s => s.Priority));

            foreach (var group in grouped)
            {
                foreach (var section in group)
                {
                    sb.Append(section.Content);
                    if (!section.Content.EndsWith("\n"))
                        sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        private enum SectionType
        {
            Essential,
            TaskDescription,
            Error,
            Rules,
            History,
            General
        }

        private class PromptSection
        {
            public string Content { get; set; } = "";
            public SectionType Type { get; set; }
            public double Priority { get; set; }
        }
    }
}