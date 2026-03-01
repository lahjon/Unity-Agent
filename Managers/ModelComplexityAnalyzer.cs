using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HappyEngine.Constants;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Analyzes task complexity to determine optimal model selection (Sonnet vs Opus)
    /// for feature mode consolidation and evaluation phases.
    /// </summary>
    public class ModelComplexityAnalyzer
    {
        // Complexity indicators and their weights
        private static readonly Dictionary<string, double> ComplexityIndicators = new()
        {
            // Error patterns
            ["compilation_errors"] = 0.3,
            ["runtime_errors"] = 0.3,
            ["test_failures"] = 0.25,
            ["conflicting_implementations"] = 0.4,

            // Task complexity
            ["multi_file_changes"] = 0.2,
            ["architectural_decisions"] = 0.3,
            ["api_design"] = 0.25,
            ["performance_optimization"] = 0.3,
            ["security_concerns"] = 0.35,

            // Team dynamics
            ["disagreements"] = 0.3,
            ["unclear_requirements"] = 0.35,
            ["multiple_approaches"] = 0.2,

            // Content volume
            ["large_codebase"] = 0.15,
            ["extensive_output"] = 0.1,
        };

        // Patterns for detecting complexity indicators
        private static readonly Dictionary<string, Regex[]> IndicatorPatterns = new()
        {
            ["compilation_errors"] = new[]
            {
                new Regex(@"(?:compilation|compile|syntax) error", RegexOptions.IgnoreCase),
                new Regex(@"error CS\d+:", RegexOptions.IgnoreCase),
                new Regex(@"(?:cannot find|unresolved) (?:symbol|reference)", RegexOptions.IgnoreCase)
            },
            ["runtime_errors"] = new[]
            {
                new Regex(@"(?:runtime|execution) (?:error|exception)", RegexOptions.IgnoreCase),
                new Regex(@"(?:NullReference|IndexOutOfRange|InvalidOperation)Exception", RegexOptions.IgnoreCase),
                new Regex(@"Stack ?trace:", RegexOptions.IgnoreCase)
            },
            ["test_failures"] = new[]
            {
                new Regex(@"(?:test|tests) (?:failed|failing)", RegexOptions.IgnoreCase),
                new Regex(@"FAILED:? \d+", RegexOptions.IgnoreCase),
                new Regex(@"assertion failed", RegexOptions.IgnoreCase)
            },
            ["conflicting_implementations"] = new[]
            {
                new Regex(@"(?:conflict|conflicting|incompatible) (?:implementation|approach|design)", RegexOptions.IgnoreCase),
                new Regex(@"(?:disagree|different approach|alternative solution)", RegexOptions.IgnoreCase)
            },
            ["architectural_decisions"] = new[]
            {
                new Regex(@"(?:architecture|architectural|design) (?:decision|choice|pattern)", RegexOptions.IgnoreCase),
                new Regex(@"(?:should we|we need to decide|considering)", RegexOptions.IgnoreCase)
            },
            ["disagreements"] = new[]
            {
                new Regex(@"(?:disagree|opposed|against|conflicting opinion)", RegexOptions.IgnoreCase),
                new Regex(@"(?:but|however|alternatively).*(?:better|prefer|recommend)", RegexOptions.IgnoreCase)
            },
            ["security_concerns"] = new[]
            {
                new Regex(@"(?:security|vulnerability|injection|XSS|CSRF)", RegexOptions.IgnoreCase),
                new Regex(@"(?:authentication|authorization|encrypt|sanitize)", RegexOptions.IgnoreCase)
            },
            ["performance_optimization"] = new[]
            {
                new Regex(@"(?:performance|optimize|optimization|slow|latency)", RegexOptions.IgnoreCase),
                new Regex(@"(?:O\(n[\^²³]?\)|complexity|algorithm)", RegexOptions.IgnoreCase)
            }
        };

        /// <summary>
        /// Analyzes the complexity of a feature mode task to determine the appropriate model.
        /// </summary>
        public ModelSelectionResult AnalyzeComplexity(
            int teamSize,
            Dictionary<string, string> childResults,
            FeatureModePhase phase,
            bool hasErrors = false)
        {
            var result = new ModelSelectionResult
            {
                BaseScore = 0.3, // Start with base complexity
                Phase = phase,
                TeamSize = teamSize
            };

            // Team size factor
            if (teamSize > 3)
            {
                result.BaseScore += 0.2;
                result.Factors.Add("Large team size", 0.2);
            }
            else if (teamSize > 5)
            {
                result.BaseScore += 0.3;
                result.Factors.Add("Very large team size", 0.3);
            }

            // Result size factor
            var totalResultSize = childResults.Values.Sum(r => r?.Length ?? 0);
            if (totalResultSize > 8000)
            {
                var sizeFactor = Math.Min(0.3, totalResultSize / 50000.0);
                result.BaseScore += sizeFactor;
                result.Factors.Add($"Extensive results ({totalResultSize:N0} chars)", sizeFactor);
            }

            // Explicit error flag
            if (hasErrors)
            {
                result.BaseScore += 0.3;
                result.Factors.Add("Errors detected", 0.3);
            }

            // Analyze content for complexity indicators
            var contentAnalysis = AnalyzeContent(childResults);
            foreach (var indicator in contentAnalysis.DetectedIndicators)
            {
                result.BaseScore += indicator.Weight;
                result.Factors.Add(indicator.Name, indicator.Weight);
            }

            // Phase-specific adjustments
            var phaseMultiplier = phase switch
            {
                FeatureModePhase.PlanConsolidation => 0.9, // Planning usually needs less complexity
                FeatureModePhase.Evaluation => 1.1, // Evaluation benefits from deeper analysis
                _ => 1.0
            };

            result.FinalScore = Math.Min(1.0, result.BaseScore * phaseMultiplier);

            // Determine model recommendation
            result.RecommendedModel = result.FinalScore >= AppConstants.ModelComplexityThreshold
                ? "opus"
                : "sonnet";

            // Add reasoning
            result.Reasoning = BuildReasoningExplanation(result);

            return result;
        }

        /// <summary>
        /// Quick analysis for simple use cases - returns just the model recommendation.
        /// </summary>
        public string QuickModelSelection(int teamSize, int resultSizeChars, bool hasErrors)
        {
            var score = 0.3;

            if (teamSize > 3) score += 0.2;
            if (resultSizeChars > 8000) score += 0.2;
            if (hasErrors) score += 0.3;

            return score >= AppConstants.ModelComplexityThreshold ? "opus" : "sonnet";
        }

        private ContentAnalysisResult AnalyzeContent(Dictionary<string, string> childResults)
        {
            var result = new ContentAnalysisResult();
            var combinedContent = string.Join("\n\n", childResults.Values);

            foreach (var indicator in IndicatorPatterns)
            {
                var indicatorName = indicator.Key;
                var patterns = indicator.Value;
                var matchCount = 0;

                foreach (var pattern in patterns)
                {
                    matchCount += pattern.Matches(combinedContent).Count;
                }

                if (matchCount > 0)
                {
                    var weight = ComplexityIndicators.GetValueOrDefault(indicatorName, 0.1);

                    // Scale weight based on frequency (diminishing returns)
                    var scaledWeight = weight * Math.Min(1.0, Math.Log(matchCount + 1) / 2);

                    result.DetectedIndicators.Add(new ComplexityIndicator
                    {
                        Name = FormatIndicatorName(indicatorName),
                        Weight = scaledWeight,
                        Occurrences = matchCount
                    });
                }
            }

            // Check for additional complexity patterns
            CheckStructuralComplexity(combinedContent, result);

            return result;
        }

        private void CheckStructuralComplexity(string content, ContentAnalysisResult result)
        {
            // Check for multi-file changes
            var fileReferences = Regex.Matches(content, @"(?:file:|modif(?:y|ied)|created?)\s+[\w/\\.-]+\.[a-zA-Z]+", RegexOptions.IgnoreCase);
            if (fileReferences.Count > 5)
            {
                result.DetectedIndicators.Add(new ComplexityIndicator
                {
                    Name = "Multiple file modifications",
                    Weight = 0.15,
                    Occurrences = fileReferences.Count
                });
            }

            // Check for API design discussions
            if (Regex.IsMatch(content, @"(?:API|interface|contract|endpoint)", RegexOptions.IgnoreCase))
            {
                var apiMatches = Regex.Matches(content, @"(?:API|interface|contract|endpoint)", RegexOptions.IgnoreCase).Count;
                if (apiMatches > 3)
                {
                    result.DetectedIndicators.Add(new ComplexityIndicator
                    {
                        Name = "API design considerations",
                        Weight = 0.2,
                        Occurrences = apiMatches
                    });
                }
            }

            // Check for unclear requirements
            var uncertaintyPatterns = new[]
            {
                @"(?:unclear|ambiguous|not sure|uncertain)",
                @"(?:could be|might be|possibly|perhaps)",
                @"(?:need clarification|need to clarify|should we)"
            };

            var uncertaintyCount = uncertaintyPatterns.Sum(pattern =>
                Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count);

            if (uncertaintyCount > 2)
            {
                result.DetectedIndicators.Add(new ComplexityIndicator
                {
                    Name = "Requirement ambiguity",
                    Weight = 0.25,
                    Occurrences = uncertaintyCount
                });
            }
        }

        private string FormatIndicatorName(string indicator)
        {
            return indicator.Replace('_', ' ')
                          .Split(' ')
                          .Select(word => char.ToUpper(word[0]) + word.Substring(1))
                          .Aggregate((a, b) => $"{a} {b}");
        }

        private string BuildReasoningExplanation(ModelSelectionResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Complexity score: {result.FinalScore:F2} (threshold: {AppConstants.ModelComplexityThreshold})");

            if (result.Factors.Any())
            {
                sb.AppendLine("Contributing factors:");
                foreach (var factor in result.Factors.OrderByDescending(f => f.Value))
                {
                    sb.AppendLine($"  - {factor.Key}: +{factor.Value:F2}");
                }
            }

            sb.AppendLine($"\nRecommendation: Use {result.RecommendedModel.ToUpper()} for this {result.Phase} task.");

            if (result.RecommendedModel == "sonnet")
            {
                sb.AppendLine("Rationale: Task complexity is relatively straightforward. Sonnet will provide good results with better speed and cost efficiency.");
            }
            else
            {
                sb.AppendLine("Rationale: Task shows significant complexity requiring Opus's advanced reasoning capabilities for optimal results.");
            }

            return sb.ToString();
        }

        // Data structures
        private class ContentAnalysisResult
        {
            public List<ComplexityIndicator> DetectedIndicators { get; set; } = new();
        }

        private class ComplexityIndicator
        {
            public string Name { get; set; } = "";
            public double Weight { get; set; }
            public int Occurrences { get; set; }
        }

        public class ModelSelectionResult
        {
            public double BaseScore { get; set; }
            public double FinalScore { get; set; }
            public string RecommendedModel { get; set; } = "sonnet";
            public Dictionary<string, double> Factors { get; set; } = new();
            public string Reasoning { get; set; } = "";
            public FeatureModePhase Phase { get; set; }
            public int TeamSize { get; set; }
        }
    }
}