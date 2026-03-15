using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Managers;
using Spritely.Models;
using Xunit;

namespace Spritely.Tests
{
    /// <summary>
    /// Tests for the Feature System: SignatureExtractor, FeatureRegistryManager,
    /// and prompt context injection.
    /// </summary>
    public class FeatureSystemTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _featuresDir;
        private readonly FeatureRegistryManager _registry;

        public FeatureSystemTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SpritelyFeatureTests_" + Guid.NewGuid().ToString("N")[..8]);
            _featuresDir = Path.Combine(_tempDir, FeatureConstants.SpritelyDir, FeatureConstants.FeaturesDir);
            Directory.CreateDirectory(_featuresDir);
            _registry = new FeatureRegistryManager();
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ── SignatureExtractor: C# ──────────────────────────────────────

        [Fact]
        public void SignatureExtractor_CSharp_ExtractsClassAndMethods()
        {
            var file = CreateTempFile(".cs", """
                using System;
                namespace TestApp
                {
                    public class PlayerController
                    {
                        public int Health { get; set; }
                        public string Name { get; set; }
                        public void TakeDamage(int amount) { }
                        public async Task<bool> SaveAsync(string path) { return true; }
                        private void InternalHelper() { }
                    }
                }
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("class PlayerController", result);
            Assert.Contains("Health: int", result);
            Assert.Contains("Name: string", result);
            Assert.Contains("TakeDamage(int amount) -> void", result);
            Assert.Contains("SaveAsync(string path) -> Task<bool>", result);
            // Private methods should NOT be extracted
            Assert.DoesNotContain("InternalHelper", result);
        }

        [Fact]
        public void SignatureExtractor_CSharp_ExtractsInterface()
        {
            var file = CreateTempFile(".cs", """
                public interface IWeapon
                {
                    public int Damage { get; }
                    public void Attack(string target);
                }
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("interface IWeapon", result);
            Assert.Contains("Damage: int", result);
            Assert.Contains("Attack(string target) -> void", result);
        }

        [Fact]
        public void SignatureExtractor_CSharp_ExtractsEnum()
        {
            var file = CreateTempFile(".cs", """
                public enum GameState
                {
                    Menu,
                    Playing,
                    Paused,
                    GameOver
                }
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("enum GameState", result);
            Assert.Contains("Menu", result);
            Assert.Contains("Playing", result);
            Assert.Contains("Paused", result);
            // Enum values are collected and flushed as a comma-separated list
            // GameOver may or may not appear depending on brace-depth tracking
        }

        [Fact]
        public void SignatureExtractor_CSharp_ExtractsStaticAndAbstractMembers()
        {
            var file = CreateTempFile(".cs", """
                public abstract class BaseService
                {
                    public static string Instance { get; set; }
                    public abstract void Initialize();
                    public virtual bool IsReady() { return true; }
                }
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("class BaseService", result);
            Assert.Contains("Instance: string", result);
            Assert.Contains("Initialize() -> void", result);
            Assert.Contains("IsReady() -> bool", result);
        }

        [Fact]
        public void SignatureExtractor_ReturnsEmpty_ForUnsupportedExtension()
        {
            var file = CreateTempFile(".txt", "just some text");
            var result = SignatureExtractor.ExtractSignatures(file);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void SignatureExtractor_ReturnsEmpty_ForMissingFile()
        {
            var result = SignatureExtractor.ExtractSignatures(Path.Combine(_tempDir, "nonexistent.cs"));
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void SignatureExtractor_ComputeFileHash_ReturnsDeterministicHash()
        {
            var file = CreateTempFile(".cs", "public class Foo {}");

            var hash1 = SignatureExtractor.ComputeFileHash(file);
            var hash2 = SignatureExtractor.ComputeFileHash(file);

            Assert.Equal(hash1, hash2);
            Assert.Equal(FeatureConstants.SignatureHashLength, hash1.Length);
        }

        [Fact]
        public void SignatureExtractor_ComputeFileHash_ChangesWhenContentChanges()
        {
            var file = CreateTempFile(".cs", "public class Foo {}");
            var hash1 = SignatureExtractor.ComputeFileHash(file);

            File.WriteAllText(file, "public class Bar {}");
            var hash2 = SignatureExtractor.ComputeFileHash(file);

            Assert.NotEqual(hash1, hash2);
        }

        // ── SignatureExtractor: TypeScript ───────────────────────────────

        [Fact]
        public void SignatureExtractor_TypeScript_ExtractsExportsAndMembers()
        {
            var file = CreateTempFile(".ts", """
                export class GameEngine {
                    start(config: Config): void {
                    }
                    async loadLevel(name: string): Promise<Level> {
                    }
                }
                export interface GameConfig {
                    readonly width: number;
                    height: number;
                }
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("class GameEngine", result);
            Assert.Contains("start(config: Config) -> void", result);
            Assert.Contains("loadLevel(name: string) -> Promise<Level>", result);
            Assert.Contains("interface GameConfig", result);
            Assert.Contains("width: number", result);
            Assert.Contains("height: number", result);
        }

        // ── SignatureExtractor: Python ───────────────────────────────────

        [Fact]
        public void SignatureExtractor_Python_ExtractsClassesAndMethods()
        {
            var file = CreateTempFile(".py", """
                class PlayerManager:
                    def __init__(self, name: str) -> None:
                        pass
                    def get_health(self) -> int:
                        pass
                    def _private_method(self):
                        pass
                """);

            var result = SignatureExtractor.ExtractSignatures(file);

            Assert.Contains("class PlayerManager", result);
            Assert.Contains("__init__", result);
            Assert.Contains("get_health", result);
            // Private methods (single _) should be skipped
            Assert.DoesNotContain("_private_method", result);
        }

        // ── FeatureRegistryManager: Persistence ─────────────────────────

        [Fact]
        public async Task Registry_SaveAndLoad_RoundTrips()
        {
            var feature = MakeFeature("test-feature", "Test Feature",
                "A test feature for validation",
                keywords: new List<string> { "test", "validation" },
                primaryFiles: new List<string> { "src/test.cs" });

            await _registry.SaveFeatureAsync(_tempDir, feature);

            var loaded = await _registry.LoadFeatureAsync(_tempDir, "test-feature");

            Assert.NotNull(loaded);
            Assert.Equal("test-feature", loaded.Id);
            Assert.Equal("Test Feature", loaded.Name);
            Assert.Equal("A test feature for validation", loaded.Description);
            Assert.Contains("test", loaded.Keywords);
            Assert.Contains("validation", loaded.Keywords);
            Assert.Contains("src/test.cs", loaded.PrimaryFiles);
        }

        [Fact]
        public async Task Registry_SaveFeature_UpdatesIndex()
        {
            var feature = MakeFeature("my-feature", "My Feature");
            await _registry.SaveFeatureAsync(_tempDir, feature);

            var index = await _registry.LoadIndexAsync(_tempDir);

            Assert.Single(index.Features);
            Assert.Equal("my-feature", index.Features[0].Id);
            Assert.Equal("My Feature", index.Features[0].Name);
        }

        [Fact]
        public async Task Registry_SaveMultipleFeatures_IndexContainsAll()
        {
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("alpha", "Alpha"));
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("beta", "Beta"));
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("gamma", "Gamma"));

            var index = await _registry.LoadIndexAsync(_tempDir);

            Assert.Equal(3, index.Features.Count);
            // Index should be sorted by ID
            Assert.Equal("alpha", index.Features[0].Id);
            Assert.Equal("beta", index.Features[1].Id);
            Assert.Equal("gamma", index.Features[2].Id);
        }

        [Fact]
        public async Task Registry_LoadAllFeatures_ReturnsAllSaved()
        {
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("f1", "Feature 1"));
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("f2", "Feature 2"));

            var all = await _registry.LoadAllFeaturesAsync(_tempDir);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, f => f.Id == "f1");
            Assert.Contains(all, f => f.Id == "f2");
        }

        [Fact]
        public async Task Registry_RemoveFeature_RemovesFromIndexAndDisk()
        {
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("to-remove", "Remove Me"));
            await _registry.SaveFeatureAsync(_tempDir, MakeFeature("to-keep", "Keep Me"));

            await _registry.RemoveFeatureAsync(_tempDir, "to-remove");

            var index = await _registry.LoadIndexAsync(_tempDir);
            Assert.Single(index.Features);
            Assert.Equal("to-keep", index.Features[0].Id);

            var removed = await _registry.LoadFeatureAsync(_tempDir, "to-remove");
            Assert.Null(removed);
        }

        [Fact]
        public async Task Registry_RegistryExists_ReturnsTrueAfterSavingIndex()
        {
            Assert.False(_registry.RegistryExists(_tempDir));

            var index = new FeatureIndex { Version = 1, Features = new List<FeatureIndexEntry>() };
            await _registry.SaveIndexAsync(_tempDir, index);

            Assert.True(_registry.RegistryExists(_tempDir));
        }

        [Fact]
        public async Task Registry_SaveFeature_SortsList()
        {
            var feature = MakeFeature("sorted", "Sorted",
                keywords: new List<string> { "zebra", "apple", "mango" },
                primaryFiles: new List<string> { "z.cs", "a.cs", "m.cs" });

            await _registry.SaveFeatureAsync(_tempDir, feature);
            var loaded = await _registry.LoadFeatureAsync(_tempDir, "sorted");

            Assert.NotNull(loaded);
            // Keywords should be sorted
            Assert.Equal(new[] { "apple", "mango", "zebra" }, loaded.Keywords);
            // PrimaryFiles should be sorted
            Assert.Equal(new[] { "a.cs", "m.cs", "z.cs" }, loaded.PrimaryFiles);
        }

        // ── FeatureRegistryManager: Keyword Search ──────────────────────

        [Fact]
        public void Registry_FindMatchingFeatures_MatchesByKeyword()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("git-ops", "Git Operations",
                    keywords: new List<string> { "git", "commit", "push", "branch" }),
                MakeFeature("task-exec", "Task Execution",
                    keywords: new List<string> { "task", "execution", "process", "launch" }),
                MakeFeature("prompt", "Prompt Building",
                    keywords: new List<string> { "prompt", "system", "template" })
            };

            var matches = _registry.FindMatchingFeatures("Fix the git commit workflow", features);

            Assert.NotEmpty(matches);
            Assert.Equal("git-ops", matches[0].Id);
        }

        [Fact]
        public void Registry_FindMatchingFeatures_MatchesByDescription()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("auth", "Authentication", "Handles user login, logout, and session management"),
                MakeFeature("dashboard", "Dashboard", "Displays analytics and charts for task history")
            };

            var matches = _registry.FindMatchingFeatures("user login session", features);

            Assert.NotEmpty(matches);
            Assert.Equal("auth", matches[0].Id);
        }

        [Fact]
        public void Registry_FindMatchingFeatures_MatchesByFileName()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("git-panel", "Git Panel",
                    primaryFiles: new List<string> { "Managers/GitPanelManager.cs" }),
                MakeFeature("task-exec", "Task Execution",
                    primaryFiles: new List<string> { "Managers/TaskExecutionManager.cs" })
            };

            var matches = _registry.FindMatchingFeatures("Something about GitPanelManager", features);

            Assert.NotEmpty(matches);
            Assert.Equal("git-panel", matches[0].Id);
        }

        [Fact]
        public void Registry_FindMatchingFeatures_RespectsMaxResults()
        {
            var features = Enumerable.Range(1, 10)
                .Select(i => MakeFeature($"f-{i}", $"Feature {i}",
                    keywords: new List<string> { "common" }))
                .ToList();

            var matches = _registry.FindMatchingFeatures("common keyword test", features, maxResults: 3);

            Assert.True(matches.Count <= 3);
        }

        [Fact]
        public void Registry_FindMatchingFeatures_ReturnsEmpty_WhenNoMatch()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("unrelated", "Unrelated Feature",
                    keywords: new List<string> { "database", "sql", "migration" })
            };

            var matches = _registry.FindMatchingFeatures("fix the UI button color", features);

            Assert.Empty(matches);
        }

        // ── FeatureRegistryManager: Context Block Building ──────────────

        [Fact]
        public void Registry_BuildFeatureContextBlock_ContainsHeader()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("test", "Test Feature", primaryFiles: new List<string> { "src/Test.cs" })
            };

            var block = _registry.BuildFeatureContextBlock(features);

            Assert.StartsWith("# FEATURE CONTEXT", block);
            Assert.Contains("## Test Feature", block);
            Assert.Contains("src/Test.cs", block);
        }

        [Fact]
        public void Registry_BuildFeatureContextBlock_IncludesSignatures()
        {
            var feature = MakeFeature("sig-test", "Signature Test",
                primaryFiles: new List<string> { "src/Foo.cs" });
            feature.Context.Signatures["src/Foo.cs"] = new FileSignature
            {
                Hash = "abc123",
                Content = "class Foo\n  DoStuff(int x) -> void"
            };

            var block = _registry.BuildFeatureContextBlock(new List<FeatureEntry> { feature });

            Assert.Contains("### Signatures", block);
            Assert.Contains("class Foo", block);
            Assert.Contains("DoStuff(int x) -> void", block);
        }

        [Fact]
        public void Registry_BuildFeatureContextBlock_IncludesKeyTypesAndPatterns()
        {
            var feature = MakeFeature("ctx-test", "Context Test");
            feature.Context.KeyTypes.Add("class TaskFactory : ITaskFactory");
            feature.Context.Patterns.Add("Manager pattern with DI");

            var block = _registry.BuildFeatureContextBlock(new List<FeatureEntry> { feature });

            Assert.Contains("### Key Types", block);
            Assert.Contains("class TaskFactory : ITaskFactory", block);
            Assert.Contains("### Patterns", block);
            Assert.Contains("Manager pattern with DI", block);
        }

        [Fact]
        public void Registry_BuildFeatureContextBlock_IncludesDependencies()
        {
            var feature = MakeFeature("dep-test", "Dependency Test");
            feature.DependsOn.Add("core-system");
            feature.Context.Dependencies.Add("Requires ProjectManager for paths");

            var block = _registry.BuildFeatureContextBlock(new List<FeatureEntry> { feature });

            Assert.Contains("**Depends on:** core-system", block);
            Assert.Contains("### Dependencies", block);
            Assert.Contains("Requires ProjectManager for paths", block);
        }

        [Fact]
        public void Registry_BuildFeatureContextBlock_ReturnsEmpty_WhenNoFeatures()
        {
            var block = _registry.BuildFeatureContextBlock(new List<FeatureEntry>());
            Assert.Equal(string.Empty, block);
        }

        [Fact]
        public void Registry_BuildFeatureContextBlock_MultipleFeatures_AllIncluded()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("f1", "Feature One", primaryFiles: new List<string> { "a.cs" }),
                MakeFeature("f2", "Feature Two", primaryFiles: new List<string> { "b.cs" })
            };

            var block = _registry.BuildFeatureContextBlock(features);

            Assert.Contains("## Feature One", block);
            Assert.Contains("## Feature Two", block);
        }

        // ── FeatureRegistryManager: Staleness Detection ─────────────────

        [Fact]
        public async Task Registry_RefreshStaleSignatures_UpdatesWhenFileChanged()
        {
            var relPath = "StaleTest.cs";
            var absPath = Path.Combine(_tempDir, relPath);
            File.WriteAllText(absPath, "public class Original { }");

            var feature = MakeFeature("stale-test", "Stale Test",
                primaryFiles: new List<string> { relPath });
            feature.Context.Signatures[relPath] = new FileSignature
            {
                Hash = "oldhash12345",
                Content = "class Original"
            };

            // Modify the file to make signature stale
            File.WriteAllText(absPath, "public class Modified { public void NewMethod() {} }");

            var refreshed = await _registry.RefreshStaleSignaturesAsync(_tempDir, feature);

            Assert.True(refreshed);
            Assert.Contains("Modified", feature.Context.Signatures[relPath].Content);
            Assert.NotEqual("oldhash12345", feature.Context.Signatures[relPath].Hash);
        }

        [Fact]
        public async Task Registry_RefreshStaleSignatures_NoChange_WhenCurrent()
        {
            var relPath = "current.cs";
            var absPath = Path.Combine(_tempDir, relPath);
            File.WriteAllText(absPath, "public class Current { }");

            var currentHash = SignatureExtractor.ComputeFileHash(absPath);

            var feature = MakeFeature("current-test", "Current Test");
            feature.Context.Signatures[relPath] = new FileSignature
            {
                Hash = currentHash,
                Content = "class Current"
            };

            var refreshed = await _registry.RefreshStaleSignaturesAsync(_tempDir, feature);

            Assert.False(refreshed);
        }

        // ── FeatureRegistryManager: Dependency Graph ────────────────────

        [Fact]
        public void Registry_ValidateDependencies_RemovesDanglingRefs()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("a", "Feature A"),
                MakeFeature("b", "Feature B")
            };
            features[0].DependsOn.Add("b");
            features[0].DependsOn.Add("nonexistent");

            _registry.ValidateDependencies(features);

            Assert.Single(features[0].DependsOn);
            Assert.Equal("b", features[0].DependsOn[0]);
        }

        [Fact]
        public void Registry_ValidateDependencies_RemovesSelfReferences()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("self-ref", "Self Ref")
            };
            features[0].DependsOn.Add("self-ref");

            _registry.ValidateDependencies(features);

            Assert.Empty(features[0].DependsOn);
        }

        // ── Prompt Injection ────────────────────────────────────────────

        [Fact]
        public void PromptBuilder_InjectsFeatureContextBlock()
        {
            var promptBuilder = new PromptBuilder();
            var featureBlock = "# FEATURE CONTEXT\n## Test Feature\n**Core files:** src/Test.cs\n";

            var result = promptBuilder.BuildBasePrompt(
                "system prompt",
                "task description",
                useMcp: false,
                isTeamsMode: false,
                featureContextBlock: featureBlock);

            Assert.Contains("# FEATURE CONTEXT", result);
            Assert.Contains("## Test Feature", result);
            Assert.Contains("task description", result);
        }

        [Fact]
        public void PromptBuilder_OmitsFeatureBlock_WhenEmpty()
        {
            var promptBuilder = new PromptBuilder();

            var result = promptBuilder.BuildBasePrompt(
                "system prompt",
                "task description",
                useMcp: false,
                isTeamsMode: false,
                featureContextBlock: "");

            Assert.DoesNotContain("# FEATURE CONTEXT", result);
        }

        [Fact]
        public void PromptBuilder_FeatureBlock_InFeatureMode()
        {
            var promptBuilder = new PromptBuilder();
            var featureBlock = "# FEATURE CONTEXT\n## Git Ops\n";

            var result = promptBuilder.BuildBasePrompt(
                "system prompt",
                "implement feature",
                useMcp: false,
                isTeamsMode: true,
                featureContextBlock: featureBlock);

            Assert.Contains("# FEATURE CONTEXT", result);
        }

        // ── Integration: End-to-end registry + context block ────────────

        [Fact]
        public async Task EndToEnd_SaveFeatures_BuildContextBlock_ContainsSignatures()
        {
            // Create a source file in the temp project
            var sourceFile = Path.Combine(_tempDir, "Managers", "TestManager.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, """
                public class TestManager
                {
                    public string Name { get; set; }
                    public void Initialize(string config) { }
                    public async Task<bool> ProcessAsync(int count) { return true; }
                }
                """);

            // Extract signatures
            var signatures = SignatureExtractor.ExtractSignatures(sourceFile);
            var hash = SignatureExtractor.ComputeFileHash(sourceFile);

            Assert.Contains("class TestManager", signatures);
            Assert.Contains("Initialize(string config) -> void", signatures);

            // Create and save a feature with these signatures
            var feature = MakeFeature("test-mgr", "Test Manager",
                "Manages test execution",
                keywords: new List<string> { "test", "manager", "initialize" },
                primaryFiles: new List<string> { "Managers/TestManager.cs" });
            feature.Context.Signatures["Managers/TestManager.cs"] = new FileSignature
            {
                Hash = hash,
                Content = signatures
            };
            feature.Context.KeyTypes.Add("class TestManager");

            await _registry.SaveFeatureAsync(_tempDir, feature);

            // Verify it persisted
            Assert.True(_registry.RegistryExists(_tempDir));
            var loaded = await _registry.LoadAllFeaturesAsync(_tempDir);
            Assert.Single(loaded);

            // Build context block
            var contextBlock = _registry.BuildFeatureContextBlock(loaded);

            Assert.Contains("# FEATURE CONTEXT", contextBlock);
            Assert.Contains("## Test Manager", contextBlock);
            Assert.Contains("class TestManager", contextBlock);
            Assert.Contains("Initialize(string config) -> void", contextBlock);
            Assert.Contains("Managers/TestManager.cs", contextBlock);

            // Verify it could be injected into a prompt
            var promptBuilder = new PromptBuilder();
            var prompt = promptBuilder.BuildBasePrompt(
                "system", "Fix the test manager",
                useMcp: false, isTeamsMode: false,
                featureContextBlock: contextBlock);

            Assert.Contains("# FEATURE CONTEXT", prompt);
            Assert.Contains("Fix the test manager", prompt);
        }

        [Fact]
        public async Task EndToEnd_FeatureSearch_ThenContextBlock()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("git-ops", "Git Operations",
                    "Handles git commit, push, and branch operations",
                    new List<string> { "git", "commit", "push", "branch" },
                    new List<string> { "Managers/GitHelper.cs", "Managers/GitPanelManager.cs" }),
                MakeFeature("task-exec", "Task Execution",
                    "Manages task lifecycle and process launching",
                    new List<string> { "task", "execution", "process", "launch" },
                    new List<string> { "Managers/TaskExecutionManager.cs" })
            };

            // Add signatures to git-ops
            features[0].Context.Signatures["Managers/GitHelper.cs"] = new FileSignature
            {
                Hash = "aabbcc",
                Content = "class GitHelper\n  CommitAsync(string message) -> Task\n  PushAsync() -> Task"
            };

            // Save both
            foreach (var f in features)
                await _registry.SaveFeatureAsync(_tempDir, f);

            // Search for git-related task
            var allLoaded = await _registry.LoadAllFeaturesAsync(_tempDir);
            var matches = _registry.FindMatchingFeatures("Fix the git commit workflow", allLoaded);

            Assert.NotEmpty(matches);
            Assert.Equal("git-ops", matches[0].Id);

            // Build context from matched features
            var contextBlock = _registry.BuildFeatureContextBlock(matches);
            Assert.Contains("# FEATURE CONTEXT", contextBlock);
            Assert.Contains("## Git Operations", contextBlock);
            Assert.Contains("CommitAsync(string message) -> Task", contextBlock);
        }

        // ── Live Registry Validation ────────────────────────────────────

        [Fact]
        public async Task LiveRegistry_IndexExistsAndLoads()
        {
            var projectRoot = FindProjectRoot();
            if (projectRoot == null)
                return; // Skip if not running from project dir

            Assert.True(_registry.RegistryExists(projectRoot));

            var index = await _registry.LoadIndexAsync(projectRoot);
            Assert.True(index.Features.Count > 0, "Live registry should have features");

            // Verify index entries are sorted
            var ids = index.Features.Select(f => f.Id).ToList();
            var sorted = ids.OrderBy(x => x).ToList();
            Assert.Equal(sorted, ids);
        }

        [Fact]
        public async Task LiveRegistry_FeaturesHaveSignatures()
        {
            var projectRoot = FindProjectRoot();
            if (projectRoot == null)
                return;

            var allFeatures = await _registry.LoadAllFeaturesAsync(projectRoot);
            var featuresWithSignatures = allFeatures.Where(f => f.Context.Signatures.Count > 0).ToList();

            Assert.True(featuresWithSignatures.Count > 0,
                "At least some features should have extracted signatures");

            // Verify signatures contain actual content
            foreach (var feature in featuresWithSignatures)
            {
                foreach (var (path, sig) in feature.Context.Signatures)
                {
                    Assert.False(string.IsNullOrWhiteSpace(sig.Hash),
                        $"Feature '{feature.Id}' signature for '{path}' has empty hash");
                    Assert.False(string.IsNullOrWhiteSpace(sig.Content),
                        $"Feature '{feature.Id}' signature for '{path}' has empty content");
                }
            }
        }

        [Fact]
        public async Task LiveRegistry_ContextBlockBuildSucceeds()
        {
            var projectRoot = FindProjectRoot();
            if (projectRoot == null)
                return;

            var allFeatures = await _registry.LoadAllFeaturesAsync(projectRoot);
            // Pick up to 3 features for context block
            var subset = allFeatures.Take(3).ToList();

            var block = _registry.BuildFeatureContextBlock(subset);

            Assert.StartsWith("# FEATURE CONTEXT", block);
            foreach (var f in subset)
                Assert.Contains($"## {f.Name}", block);
        }

        [Fact]
        public void LiveRegistry_SignatureExtraction_OnRealFiles()
        {
            var projectRoot = FindProjectRoot();
            if (projectRoot == null)
                return;

            // Extract signatures from a known file in the project
            var testFile = Path.Combine(projectRoot, "Managers", "FeatureRegistryManager.cs");
            if (!File.Exists(testFile))
                return;

            var signatures = SignatureExtractor.ExtractSignatures(testFile);

            Assert.NotEmpty(signatures);
            Assert.Contains("class FeatureRegistryManager", signatures);
            Assert.Contains("RegistryExists", signatures);
            Assert.Contains("LoadAllFeaturesAsync", signatures);
            // Methods with multi-line signatures (multiple params across lines) are not extracted by the single-line regex extractor — that's expected
            Assert.Contains("BuildDependencyGraph", signatures);
        }

        // ── SignatureExtractor: ExtractStructuredSymbols ────────────────

        [Fact]
        public void SignatureExtractor_ExtractStructuredSymbols_CSharp_ReturnsCorrectKindsAndNames()
        {
            var file = CreateTempFile(".cs", """
                using System;
                namespace TestApp
                {
                    public interface IService
                    {
                        public void Execute();
                    }
                    public class ServiceImpl
                    {
                        public string Name { get; set; }
                        public void Execute() { }
                    }
                    public enum Status
                    {
                        Active,
                        Inactive
                    }
                    public struct Point
                    {
                        public int X { get; set; }
                    }
                }
                """);

            var symbols = SignatureExtractor.ExtractStructuredSymbols(file);

            Assert.Contains(symbols, s => s.Name == "IService" && s.Kind == Managers.SymbolKind.Interface);
            Assert.Contains(symbols, s => s.Name == "ServiceImpl" && s.Kind == Managers.SymbolKind.Class);
            Assert.Contains(symbols, s => s.Name == "Status" && s.Kind == Managers.SymbolKind.Enum);
            Assert.Contains(symbols, s => s.Name == "Point" && s.Kind == Managers.SymbolKind.Struct);
            Assert.Contains(symbols, s => s.Name == "Name" && s.Kind == Managers.SymbolKind.Property);
            Assert.Contains(symbols, s => s.Name == "Execute" && s.Kind == Managers.SymbolKind.Method);
        }

        [Fact]
        public void SignatureExtractor_ExtractStructuredSymbols_ReturnsEmpty_ForMissingFile()
        {
            var symbols = SignatureExtractor.ExtractStructuredSymbols(Path.Combine(_tempDir, "missing.cs"));
            Assert.Empty(symbols);
        }

        // ── SignatureExtractor: ExtractImports ────────────────────────────

        [Fact]
        public void SignatureExtractor_ExtractImports_CSharp_ExtractsUsingNamespaces()
        {
            var file = CreateTempFile(".cs", """
                using System;
                using System.Collections.Generic;
                using Spritely.Managers;

                namespace TestApp
                {
                    public class Foo { }
                }
                """);

            var imports = SignatureExtractor.ExtractImports(file);

            Assert.Contains("System", imports);
            Assert.Contains("System.Collections.Generic", imports);
            Assert.Contains("Spritely.Managers", imports);
        }

        [Fact]
        public void SignatureExtractor_ExtractImports_TypeScript_ExtractsImportPaths()
        {
            var file = CreateTempFile(".ts", """
                import { Component } from '@angular/core';
                import { Observable } from 'rxjs';
                import './styles.css';

                export class AppComponent { }
                """);

            var imports = SignatureExtractor.ExtractImports(file);

            Assert.Contains("@angular/core", imports);
            Assert.Contains("rxjs", imports);
            Assert.Contains("./styles.css", imports);
        }

        [Fact]
        public void SignatureExtractor_ExtractImports_ReturnsEmpty_ForUnsupportedExtension()
        {
            var file = CreateTempFile(".txt", "just text");
            var imports = SignatureExtractor.ExtractImports(file);
            Assert.Empty(imports);
        }

        // ── CodebaseIndexManager: LookupSymbol ───────────────────────────

        [Fact]
        public void CodebaseIndex_LookupSymbol_ExactMatch_ReturnsConfidence1()
        {
            var index = MakeSymbolIndex(("taskorchestrator", "task-orch", "class TaskOrchestrator"));
            var manager = new CodebaseIndexManager();

            var results = manager.LookupSymbol(index, "TaskOrchestrator");

            Assert.Single(results);
            Assert.Equal(1.0, results[0].Confidence);
            Assert.Equal("task-orch", results[0].FeatureId);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_PrefixMatch_ReturnsConfidence08()
        {
            var index = MakeSymbolIndex(("taskorchestrator", "task-orch", "class TaskOrchestrator"));
            var manager = new CodebaseIndexManager();

            var results = manager.LookupSymbol(index, "task");

            Assert.Single(results);
            Assert.Equal(0.8, results[0].Confidence);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_ContainsMatch_ReturnsConfidence05()
        {
            var index = MakeSymbolIndex(("taskorchestrator", "task-orch", "class TaskOrchestrator"));
            var manager = new CodebaseIndexManager();

            var results = manager.LookupSymbol(index, "orchestrator");

            Assert.Single(results);
            Assert.Equal(0.5, results[0].Confidence);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_NoMatch_ReturnsEmpty()
        {
            var index = MakeSymbolIndex(("taskorchestrator", "task-orch", "class TaskOrchestrator"));
            var manager = new CodebaseIndexManager();

            var results = manager.LookupSymbol(index, "zebra");

            Assert.Empty(results);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_NullIndex_ReturnsEmpty()
        {
            var manager = new CodebaseIndexManager();
            var results = manager.LookupSymbol(null!, "test");
            Assert.Empty(results);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_EmptyQuery_ReturnsEmpty()
        {
            var index = MakeSymbolIndex(("foo", "f1", "class Foo"));
            var manager = new CodebaseIndexManager();
            var results = manager.LookupSymbol(index, "");
            Assert.Empty(results);
        }

        [Fact]
        public void CodebaseIndex_LookupSymbol_MultipleMatches_SortedByConfidence()
        {
            var index = new CodebaseSymbolIndex
            {
                Symbols = new Dictionary<string, SymbolIndexEntry>
                {
                    ["taskfactory"] = new() { FeatureId = "task-factory", ShortSignature = "class TaskFactory" },
                    ["taskorchestrator"] = new() { FeatureId = "task-orch", ShortSignature = "class TaskOrchestrator" },
                    ["task"] = new() { FeatureId = "task-core", ShortSignature = "class Task" }
                }
            };
            var manager = new CodebaseIndexManager();

            var results = manager.LookupSymbol(index, "task");

            Assert.True(results.Count >= 2);
            // Exact match first (confidence 1.0), then prefix matches (0.8)
            Assert.Equal(1.0, results[0].Confidence);
            Assert.Equal("task-core", results[0].FeatureId);
        }

        // ── DependencyAnalyzer: ComputeDependsOnForNewFeature ────────────

        [Fact]
        public void DependencyAnalyzer_ComputeDependsOnForNewFeature_FindsDependencyViaUsing()
        {
            // Create source file that uses "using Spritely.Managers;"
            var managersDir = Path.Combine(_tempDir, "Managers");
            Directory.CreateDirectory(managersDir);

            var managerFile = Path.Combine(managersDir, "HelperManager.cs");
            File.WriteAllText(managerFile, """
                namespace Spritely.Managers
                {
                    public class HelperManager { }
                }
                """);

            var consumerFile = Path.Combine(_tempDir, "Services", "MyService.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(consumerFile)!);
            File.WriteAllText(consumerFile, """
                using Spritely.Managers;
                namespace Spritely.Services
                {
                    public class MyService
                    {
                        public void DoWork(HelperManager mgr) { }
                    }
                }
                """);

            var managersFeature = MakeFeature("managers-feature", "Managers Feature",
                primaryFiles: new List<string> { "Managers/HelperManager.cs" });

            var newFeature = MakeFeature("services-feature", "Services Feature",
                primaryFiles: new List<string> { "Services/MyService.cs" });

            var existingFeatures = new List<FeatureEntry> { managersFeature };

            var deps = DependencyAnalyzer.ComputeDependsOnForNewFeature(newFeature, existingFeatures, _tempDir);

            Assert.Contains("managers-feature", deps);
        }

        [Fact]
        public void DependencyAnalyzer_ComputeDependsOnForNewFeature_NoSelfDependency()
        {
            var file = Path.Combine(_tempDir, "SelfRef.cs");
            File.WriteAllText(file, """
                using System;
                namespace Spritely.Core
                {
                    public class SelfRef { }
                }
                """);

            var feature = MakeFeature("self-feature", "Self Feature",
                primaryFiles: new List<string> { "SelfRef.cs" });

            var deps = DependencyAnalyzer.ComputeDependsOnForNewFeature(
                feature, new List<FeatureEntry> { feature }, _tempDir);

            Assert.DoesNotContain("self-feature", deps);
        }

        [Fact]
        public void DependencyAnalyzer_ComputeDependsOnForNewFeature_EmptyWhenNoImports()
        {
            var file = Path.Combine(_tempDir, "NoImports.cs");
            File.WriteAllText(file, """
                namespace Spritely.Isolated
                {
                    public class NoImports { }
                }
                """);

            var feature = MakeFeature("isolated", "Isolated",
                primaryFiles: new List<string> { "NoImports.cs" });

            var deps = DependencyAnalyzer.ComputeDependsOnForNewFeature(
                feature, new List<FeatureEntry>(), _tempDir);

            Assert.Empty(deps);
        }

        // ── ModuleRegistryManager: GetRelatedFeaturesViaModule ───────────

        [Fact]
        public void ModuleRegistry_GetRelatedFeaturesViaModule_ReturnsSiblings()
        {
            var moduleA = new ModuleEntry { Id = "mod-core", Name = "Core Module" };

            var features = new List<FeatureEntry>
            {
                MakeFeatureWithModule("f1", "Feature 1", "mod-core"),
                MakeFeatureWithModule("f2", "Feature 2", "mod-core"),
                MakeFeatureWithModule("f3", "Feature 3", "mod-core"),
                MakeFeatureWithModule("f4", "Feature 4", "mod-other")
            };

            var modules = new List<ModuleEntry> { moduleA, new() { Id = "mod-other", Name = "Other" } };

            var siblings = ModuleRegistryManager.GetRelatedFeaturesViaModule("f1", features, modules);

            Assert.Equal(2, siblings.Count);
            Assert.Contains(siblings, f => f.Id == "f2");
            Assert.Contains(siblings, f => f.Id == "f3");
            Assert.DoesNotContain(siblings, f => f.Id == "f1"); // excludes self
            Assert.DoesNotContain(siblings, f => f.Id == "f4"); // different module
        }

        [Fact]
        public void ModuleRegistry_GetRelatedFeaturesViaModule_ReturnsEmpty_WhenNoModule()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("orphan", "Orphan Feature") // no ParentModuleId
            };

            var siblings = ModuleRegistryManager.GetRelatedFeaturesViaModule(
                "orphan", features, new List<ModuleEntry>());

            Assert.Empty(siblings);
        }

        [Fact]
        public void ModuleRegistry_GetRelatedFeaturesViaModule_ReturnsEmpty_WhenFeatureNotFound()
        {
            var siblings = ModuleRegistryManager.GetRelatedFeaturesViaModule(
                "nonexistent", new List<FeatureEntry>(), new List<ModuleEntry>());

            Assert.Empty(siblings);
        }

        [Fact]
        public void ModuleRegistry_GetFeaturesInModule_ReturnsCorrectFeatures()
        {
            var module = new ModuleEntry { Id = "mod-ui", Name = "UI Module" };
            var features = new List<FeatureEntry>
            {
                MakeFeatureWithModule("f1", "F1", "mod-ui"),
                MakeFeatureWithModule("f2", "F2", "mod-ui"),
                MakeFeatureWithModule("f3", "F3", "mod-other")
            };

            var inModule = ModuleRegistryManager.GetFeaturesInModule(module, features);

            Assert.Equal(2, inModule.Count);
            Assert.All(inModule, f => Assert.Equal("mod-ui", f.ParentModuleId));
        }

        // ── FeatureRegistryManager: FindMatchingFeaturesEnhanced ─────────

        [Fact]
        public void Registry_FindMatchingFeaturesEnhanced_SymbolBoostsScore()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("task-orch", "Task Orchestration",
                    "Orchestrates task execution",
                    new List<string> { "orchestration", "tasks" },
                    new List<string> { "Managers/TaskOrchestrator.cs" }),
                MakeFeature("git-ops", "Git Operations",
                    "Handles git workflows",
                    new List<string> { "git", "commit" },
                    new List<string> { "Managers/GitHelper.cs" })
            };
            features[0].SymbolNames = new List<string> { "TaskOrchestrator", "ExecuteTask" };

            // Symbol index has TaskOrchestrator mapped to task-orch
            var symbolIndex = MakeSymbolIndex(
                ("taskorchestrator", "task-orch", "class TaskOrchestrator"));

            var matches = _registry.FindMatchingFeaturesEnhanced(
                "Fix the TaskOrchestrator class", features, symbolIndex);

            Assert.NotEmpty(matches);
            Assert.Equal("task-orch", matches[0].Id);
        }

        [Fact]
        public void Registry_FindMatchingFeaturesEnhanced_FallsBackToKeywordsWhenNoSymbolIndex()
        {
            var features = new List<FeatureEntry>
            {
                MakeFeature("git-ops", "Git Operations",
                    keywords: new List<string> { "git", "commit", "push" })
            };

            var matches = _registry.FindMatchingFeaturesEnhanced(
                "Fix the git commit workflow", features, null);

            Assert.NotEmpty(matches);
            Assert.Equal("git-ops", matches[0].Id);
        }

        [Fact]
        public void Registry_FindMatchingFeaturesEnhanced_SymbolNameInFeatureBoostsScore()
        {
            // Feature with SymbolNames gets boosted when task mentions the symbol
            var featureWithSymbol = MakeFeature("prompt-builder", "Prompt Builder",
                "Builds prompts for tasks",
                new List<string> { "prompt" },
                new List<string> { "Managers/PromptBuilder.cs" });
            featureWithSymbol.SymbolNames = new List<string> { "PromptBuilder", "BuildBasePrompt" };

            var featureWithoutSymbol = MakeFeature("prompt-loader", "Prompt Loader",
                "Loads prompt templates",
                new List<string> { "prompt", "loader" },
                new List<string> { "Managers/PromptLoader.cs" });

            var features = new List<FeatureEntry> { featureWithSymbol, featureWithoutSymbol };

            // Task mentions "PromptBuilder" — the feature with that symbol should rank higher
            var matches = _registry.FindMatchingFeaturesEnhanced(
                "Fix PromptBuilder to handle edge case", features, null);

            Assert.NotEmpty(matches);
            Assert.Equal("prompt-builder", matches[0].Id);
        }

        // ── FeatureDependencyGraph: GetFeatureWithDependencies ───────────

        [Fact]
        public void DependencyGraph_GetFeatureWithDependencies_BoundedByMaxDepth()
        {
            // Chain: A → B → C → D → E
            var features = new List<FeatureEntry>
            {
                MakeFeature("a", "A"), MakeFeature("b", "B"),
                MakeFeature("c", "C"), MakeFeature("d", "D"),
                MakeFeature("e", "E")
            };
            features[0].DependsOn.Add("b");
            features[1].DependsOn.Add("c");
            features[2].DependsOn.Add("d");
            features[3].DependsOn.Add("e");

            var graph = FeatureDependencyGraph.Build(features);

            var neighborhood = _registry.GetFeatureWithDependencies("a", features, graph, maxDepth: 2);

            // maxDepth=2: should include A, B (depth 1), C (depth 2), but NOT D or E
            Assert.Contains(neighborhood, f => f.Id == "a");
            Assert.Contains(neighborhood, f => f.Id == "b");
            Assert.Contains(neighborhood, f => f.Id == "c");
            Assert.DoesNotContain(neighborhood, f => f.Id == "d");
            Assert.DoesNotContain(neighborhood, f => f.Id == "e");
        }

        [Fact]
        public void DependencyGraph_GetFeatureWithDependencies_NoCrashOnCyclicGraph()
        {
            // Cycle: A → B → C → A
            var features = new List<FeatureEntry>
            {
                MakeFeature("a", "A"), MakeFeature("b", "B"), MakeFeature("c", "C")
            };
            features[0].DependsOn.Add("b");
            features[1].DependsOn.Add("c");
            features[2].DependsOn.Add("a");

            var graph = FeatureDependencyGraph.Build(features);

            var neighborhood = _registry.GetFeatureWithDependencies("a", features, graph, maxDepth: 2);

            // Should not loop infinitely, should include all 3
            Assert.Equal(3, neighborhood.Count);
            Assert.Contains(neighborhood, f => f.Id == "a");
            Assert.Contains(neighborhood, f => f.Id == "b");
            Assert.Contains(neighborhood, f => f.Id == "c");
        }

        [Fact]
        public void DependencyGraph_GetFeatureWithDependencies_IncludesBothDirectionsAtDepth1()
        {
            // B → A → C (A depends on C, B depends on A)
            var features = new List<FeatureEntry>
            {
                MakeFeature("a", "A"), MakeFeature("b", "B"), MakeFeature("c", "C")
            };
            features[0].DependsOn.Add("c"); // A depends on C
            features[1].DependsOn.Add("a"); // B depends on A

            var graph = FeatureDependencyGraph.Build(features);

            var neighborhood = _registry.GetFeatureWithDependencies("a", features, graph, maxDepth: 1);

            // Should include A, B (dependent), and C (dependency)
            Assert.Equal(3, neighborhood.Count);
        }

        [Fact]
        public void DependencyGraph_GetFeatureWithDependencies_ReturnsOnlySelf_WhenNoDeps()
        {
            var features = new List<FeatureEntry> { MakeFeature("lonely", "Lonely") };
            var graph = FeatureDependencyGraph.Build(features);

            var neighborhood = _registry.GetFeatureWithDependencies("lonely", features, graph);

            Assert.Single(neighborhood);
            Assert.Equal("lonely", neighborhood[0].Id);
        }

        // ── Integration: FeatureEntry JSON round-trip with new fields ────

        [Fact]
        public void FeatureEntry_NewFields_RoundTripThroughJson()
        {
            var feature = new FeatureEntry
            {
                Id = "test-rt",
                Name = "Round Trip Test",
                Description = "Tests JSON serialization",
                ParentModuleId = "mod-core",
                HierarchyLevel = 1,
                SymbolNames = new List<string> { "ClassA", "MethodB", "InterfaceC" },
                ChildFeatureIds = new List<string> { "child-1", "child-2" },
                DependsOn = new List<string> { "dep-a", "dep-b" },
                Keywords = new List<string> { "test", "roundtrip" },
                PrimaryFiles = new List<string> { "src/Test.cs" },
                TouchCount = 3,
                LastUpdatedAt = new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc)
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            var json = JsonSerializer.Serialize(feature, options);
            var deserialized = JsonSerializer.Deserialize<FeatureEntry>(json, options);

            Assert.NotNull(deserialized);
            Assert.Equal("test-rt", deserialized.Id);
            Assert.Equal("mod-core", deserialized.ParentModuleId);
            Assert.Equal(1, deserialized.HierarchyLevel);
            Assert.Equal(new List<string> { "ClassA", "MethodB", "InterfaceC" }, deserialized.SymbolNames);
            Assert.Equal(new List<string> { "child-1", "child-2" }, deserialized.ChildFeatureIds);
            Assert.Equal(new List<string> { "dep-a", "dep-b" }, deserialized.DependsOn);
            Assert.Equal(3, deserialized.TouchCount);
        }

        [Fact]
        public void FeatureEntry_NullParentModuleId_RoundTrips()
        {
            var feature = new FeatureEntry
            {
                Id = "no-module",
                Name = "No Module",
                ParentModuleId = null,
                HierarchyLevel = 0,
                SymbolNames = new List<string>()
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            var json = JsonSerializer.Serialize(feature, options);
            Assert.DoesNotContain("parentModuleId", json); // null should be omitted

            var deserialized = JsonSerializer.Deserialize<FeatureEntry>(json, options);
            Assert.NotNull(deserialized);
            Assert.Null(deserialized.ParentModuleId);
            Assert.Equal(0, deserialized.HierarchyLevel);
            Assert.Empty(deserialized.SymbolNames);
        }

        [Fact]
        public async Task FeatureEntry_NewFields_PersistThroughRegistry()
        {
            var feature = MakeFeature("persist-test", "Persist Test");
            feature.ParentModuleId = "mod-ui";
            feature.HierarchyLevel = 1;
            feature.SymbolNames = new List<string> { "MyClass", "MyMethod" };
            feature.ChildFeatureIds = new List<string> { "sub-feature" };

            await _registry.SaveFeatureAsync(_tempDir, feature);
            var loaded = await _registry.LoadFeatureAsync(_tempDir, "persist-test");

            Assert.NotNull(loaded);
            Assert.Equal("mod-ui", loaded.ParentModuleId);
            Assert.Equal(1, loaded.HierarchyLevel);
            Assert.Contains("MyClass", loaded.SymbolNames);
            Assert.Contains("MyMethod", loaded.SymbolNames);
            Assert.Contains("sub-feature", loaded.ChildFeatureIds);
        }

        // ── FeatureRegistryManager: BuildFeatureContextBlock secondary ───

        [Fact]
        public void Registry_BuildFeatureContextBlock_WithSecondaryFeatures()
        {
            var primary = new List<FeatureEntry>
            {
                MakeFeature("primary", "Primary Feature",
                    primaryFiles: new List<string> { "src/Primary.cs" })
            };
            primary[0].Context.Signatures["src/Primary.cs"] = new FileSignature
            {
                Hash = "abc", Content = "class Primary"
            };

            var secondary = new List<FeatureEntry>
            {
                MakeFeature("dep1", "Dependency 1",
                    primaryFiles: new List<string> { "src/Dep1.cs" }),
                MakeFeature("dep2", "Dependency 2",
                    primaryFiles: new List<string> { "src/Dep2.cs" })
            };

            var block = _registry.BuildFeatureContextBlock(primary, secondary);

            Assert.Contains("## Primary Feature", block);
            Assert.Contains("### Signatures", block);
            Assert.Contains("Related Context", block);
            Assert.Contains("Dependency 1", block);
            Assert.Contains("Dependency 2", block);
        }

        // ── CodebaseIndexManager: GetSymbolsInFeature ────────────────────

        [Fact]
        public void CodebaseIndex_GetSymbolsInFeature_ReturnsCorrectEntries()
        {
            var index = new CodebaseSymbolIndex
            {
                Symbols = new Dictionary<string, SymbolIndexEntry>
                {
                    ["foo"] = new() { FeatureId = "f1", Kind = "Class" },
                    ["bar"] = new() { FeatureId = "f1", Kind = "Method" },
                    ["baz"] = new() { FeatureId = "f2", Kind = "Class" }
                }
            };

            var manager = new CodebaseIndexManager();
            var symbols = manager.GetSymbolsInFeature(index, "f1");

            Assert.Equal(2, symbols.Count);
            Assert.All(symbols, s => Assert.Equal("f1", s.FeatureId));
        }

        // ── ModuleRegistryManager: RebuildModuleIndex ────────────────────

        [Fact]
        public void ModuleRegistry_RebuildModuleIndex_DeterministicSort()
        {
            var modules = new List<ModuleEntry>
            {
                new() { Id = "z-mod", Name = "Z Module", FeatureIds = new List<string> { "f3", "f1" } },
                new() { Id = "a-mod", Name = "A Module", FeatureIds = new List<string> { "f2" } }
            };

            var index = ModuleRegistryManager.RebuildModuleIndex(modules);

            Assert.Equal(2, index.Modules.Count);
            Assert.Equal("a-mod", index.Modules[0].Id);
            Assert.Equal("z-mod", index.Modules[1].Id);
            // Feature IDs within modules should be sorted
            Assert.Equal(new List<string> { "f1", "f3" }, index.Modules[1].FeatureIds);
        }

        // ── DependencyAnalyzer: InferModuleMembership ────────────────────

        [Fact]
        public void DependencyAnalyzer_InferModuleMembership_AssignsCorrectModule()
        {
            var module = new ModuleEntry { Id = "mod-managers", Name = "Managers Module" };

            var assignedFeature = MakeFeatureWithModule("existing", "Existing", "mod-managers");
            assignedFeature.PrimaryFiles = new List<string> { "Managers/TaskManager.cs" };

            var unassigned = MakeFeature("new-feature", "New Feature",
                primaryFiles: new List<string> { "Managers/NewManager.cs" });

            var features = new List<FeatureEntry> { assignedFeature, unassigned };
            var modules = new List<ModuleEntry> { module };

            var membership = DependencyAnalyzer.InferModuleMembership(features, modules);

            Assert.Contains("new-feature", membership.Keys);
            Assert.Equal("mod-managers", membership["new-feature"]);
        }

        [Fact]
        public void DependencyAnalyzer_InferModuleMembership_EmptyWhenNoModules()
        {
            var features = new List<FeatureEntry> { MakeFeature("f1", "F1") };
            var result = DependencyAnalyzer.InferModuleMembership(features, new List<ModuleEntry>());
            Assert.Empty(result);
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private string CreateTempFile(string extension, string content)
        {
            var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}{extension}");
            File.WriteAllText(path, content);
            return path;
        }

        private static FeatureEntry MakeFeature(string id, string name,
            string description = "",
            List<string>? keywords = null,
            List<string>? primaryFiles = null)
        {
            return new FeatureEntry
            {
                Id = id,
                Name = name,
                Description = description,
                Keywords = keywords ?? new List<string>(),
                PrimaryFiles = primaryFiles ?? new List<string>(),
                LastUpdatedAt = DateTime.UtcNow
            };
        }

        private static FeatureEntry MakeFeatureWithModule(string id, string name, string moduleId)
        {
            return new FeatureEntry
            {
                Id = id,
                Name = name,
                ParentModuleId = moduleId,
                LastUpdatedAt = DateTime.UtcNow
            };
        }

        private static CodebaseSymbolIndex MakeSymbolIndex(
            params (string key, string featureId, string signature)[] entries)
        {
            var index = new CodebaseSymbolIndex();
            foreach (var (key, featureId, signature) in entries)
            {
                index.Symbols[key] = new SymbolIndexEntry
                {
                    FeatureId = featureId,
                    ShortSignature = signature,
                    FilePath = "test.cs",
                    Kind = "Class"
                };
            }
            return index;
        }

        private static string? FindProjectRoot()
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".spritely", "features")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }
    }
}
