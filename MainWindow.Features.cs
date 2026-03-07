using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        private readonly ObservableCollection<FeatureEntry> _featureEntries = new();
        private ICollectionView? _featuresView;
        private FeatureEntry? _selectedFeature;
        private List<FeatureEntry>? _allFeaturesCache;

        private void InitializeFeaturesTab()
        {
            FeaturesListBox.ItemsSource = _featureEntries;
            _featuresView = CollectionViewSource.GetDefaultView(_featureEntries);
            _featuresView.Filter = FilterFeature;
        }

        private bool FilterFeature(object obj)
        {
            if (obj is not FeatureEntry feature) return false;
            var query = FeatureSearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return true;

            return feature.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || feature.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || feature.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || feature.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        private async System.Threading.Tasks.Task LoadFeaturesAsync()
        {
            if (!_projectManager.HasProjects) return;

            var projectPath = _projectManager.ProjectPath;
            var registry = new FeatureRegistryManager();

            if (!registry.RegistryExists(projectPath))
            {
                _featureEntries.Clear();
                _allFeaturesCache = null;
                UpdateFeatureDetail(null);
                UpdateFeaturesTabCount();
                return;
            }

            try
            {
                var features = await registry.LoadAllFeaturesAsync(projectPath);
                _allFeaturesCache = features;
                _featureEntries.Clear();
                foreach (var f in features.OrderBy(f => f.Category).ThenBy(f => f.Name))
                    _featureEntries.Add(f);
                UpdateFeaturesTabCount();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeaturesTab", $"Failed to load features: {ex.Message}", ex);
            }
        }

        private void FeatureSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _featuresView?.Refresh();
            UpdateFeaturesTabCount();
        }

        private void FeaturesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedFeature = FeaturesListBox.SelectedItem as FeatureEntry;
            UpdateFeatureDetail(_selectedFeature);
        }

        private void UpdateFeatureDetail(FeatureEntry? feature)
        {
            if (feature == null)
            {
                FeatureDetailPanel.Visibility = Visibility.Collapsed;
                return;
            }

            FeatureDetailPanel.Visibility = Visibility.Visible;
            FeatureDetailName.Text = feature.Name;
            FeatureDetailCategory.Text = feature.Category;
            FeatureDetailDescription.Text = feature.Description;

            // Keywords
            FeatureDetailKeywords.Text = feature.Keywords.Count > 0
                ? string.Join(", ", feature.Keywords)
                : "None";

            // Primary files
            FeatureDetailPrimaryFiles.Text = feature.PrimaryFiles.Count > 0
                ? string.Join("\n", feature.PrimaryFiles)
                : "None";

            // Secondary files
            FeatureDetailSecondaryFiles.Text = feature.SecondaryFiles.Count > 0
                ? string.Join("\n", feature.SecondaryFiles)
                : "None";

            // Related features (dependents/dependencies)
            var relatedNames = new List<string>();
            if (_allFeaturesCache != null)
            {
                foreach (var relId in feature.RelatedFeatureIds)
                {
                    var rel = _allFeaturesCache.FirstOrDefault(f => f.Id == relId);
                    relatedNames.Add(rel != null ? rel.Name : relId);
                }
            }
            else
            {
                relatedNames.AddRange(feature.RelatedFeatureIds);
            }
            FeatureDetailRelated.Text = relatedNames.Count > 0
                ? string.Join(", ", relatedNames)
                : "None";

            // DependsOn (directional feature dependencies)
            var dependsOnNames = new List<string>();
            if (_allFeaturesCache != null)
            {
                foreach (var depId in feature.DependsOn)
                {
                    var dep = _allFeaturesCache.FirstOrDefault(f => f.Id == depId);
                    dependsOnNames.Add(dep != null ? dep.Name : depId);
                }
            }
            else
            {
                dependsOnNames.AddRange(feature.DependsOn);
            }
            FeatureDetailDependsOn.Text = dependsOnNames.Count > 0
                ? string.Join(", ", dependsOnNames)
                : "None";

            // Dependencies (from Context)
            FeatureDetailDependencies.Text = feature.Context.Dependencies.Count > 0
                ? string.Join("\n", feature.Context.Dependencies)
                : "None";

            // Key types
            FeatureDetailKeyTypes.Text = feature.Context.KeyTypes.Count > 0
                ? string.Join("\n", feature.Context.KeyTypes)
                : "None";

            // Patterns
            FeatureDetailPatterns.Text = feature.Context.Patterns.Count > 0
                ? string.Join("\n", feature.Context.Patterns)
                : "None";

            // Signatures summary
            var sigSummary = new List<string>();
            foreach (var kvp in feature.Context.Signatures)
            {
                var lines = kvp.Value.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var preview = lines.Length > 0 ? lines[0].Trim() : "(empty)";
                sigSummary.Add($"{kvp.Key}: {preview}");
            }
            FeatureDetailSignatures.Text = sigSummary.Count > 0
                ? string.Join("\n", sigSummary)
                : "None";

            // Metadata
            FeatureDetailMeta.Text = $"Touched {feature.TouchCount} time(s) · Last updated {feature.LastUpdatedAt:yyyy-MM-dd HH:mm}";
        }

        private void UpdateFeaturesTabCount()
        {
            if (FeaturesTabCount != null)
            {
                var visibleCount = _featuresView?.Cast<object>().Count() ?? _featureEntries.Count;
                FeaturesTabCount.Text = $" ({visibleCount})";
            }
        }

        private bool _featuresPanelExpanded = true;

        private void ToggleFeaturesPanel_Click(object sender, RoutedEventArgs e)
        {
            _featuresPanelExpanded = !_featuresPanelExpanded;
            FeaturesPanelContent.Visibility = _featuresPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
            FeaturesListBox.Visibility = _featuresPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
            FeatureDetailPanel.Visibility = _featuresPanelExpanded && _selectedFeature != null ? Visibility.Visible : Visibility.Collapsed;
            // E70D = chevron down, E70E = chevron up
            FeaturesPanelToggleBtn.Content = _featuresPanelExpanded ? "\uE70D" : "\uE70E";
        }

        private void ToggleFeaturesPanel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleFeaturesPanel_Click(sender, (RoutedEventArgs)e);
        }

        private void CloseFeatureDetail_Click(object sender, RoutedEventArgs e)
        {
            FeaturesListBox.SelectedItem = null;
            UpdateFeatureDetail(null);
        }

        private void RefreshFeatures_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadFeaturesAsync();
        }

        private async void UpdateFeature_Click(object sender, RoutedEventArgs e)
        {
            var feature = GetFeatureFromContextMenu(sender);
            if (feature == null || !_projectManager.HasProjects) return;

            var projectPath = _projectManager.ProjectPath;
            var registry = new FeatureRegistryManager();

            try
            {
                var refreshed = await registry.RefreshStaleSignaturesAsync(projectPath, feature);
                var status = refreshed ? "Signatures updated" : "Signatures already up to date";
                AppLogger.Info("FeaturesTab", $"{status} for '{feature.Name}'");
                await LoadFeaturesAsync();
                UpdateFeatureDetail(feature);
            }
            catch (Exception ex)
            {
                AppLogger.Error("FeaturesTab", $"Failed to update feature '{feature.Name}': {ex.Message}", ex);
            }
        }

        private async void FullUpdateFeature_Click(object sender, RoutedEventArgs e)
        {
            var feature = GetFeatureFromContextMenu(sender);
            if (feature == null || !_projectManager.HasProjects) return;

            var projectPath = _projectManager.ProjectPath;
            var registry = new FeatureRegistryManager();

            try
            {
                // Re-extract signatures for ALL primary files (not just stale)
                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = System.IO.Path.Combine(projectPath, relPath);
                    if (!System.IO.File.Exists(absPath)) continue;

                    var content = SignatureExtractor.ExtractSignatures(absPath);
                    feature.Context.Signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = content
                    };
                }

                // Remove signatures for files that no longer exist
                var staleKeys = feature.Context.Signatures.Keys
                    .Where(k => !System.IO.File.Exists(System.IO.Path.Combine(projectPath, k)))
                    .ToList();
                foreach (var key in staleKeys)
                    feature.Context.Signatures.Remove(key);

                // Re-extract symbol names from primary files
                var symbolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = System.IO.Path.Combine(projectPath, relPath);
                    if (!System.IO.File.Exists(absPath)) continue;
                    foreach (var sym in SignatureExtractor.GetSymbolNames(absPath))
                        symbolNames.Add(sym);
                }
                feature.SymbolNames = symbolNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

                // Rebuild key types from extracted structured symbols
                var keyTypes = new List<string>();
                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = System.IO.Path.Combine(projectPath, relPath);
                    if (!System.IO.File.Exists(absPath)) continue;
                    var symbols = SignatureExtractor.ExtractStructuredSymbols(absPath);
                    foreach (var sym in symbols)
                    {
                        if (sym.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum
                            or SymbolKind.Struct or SymbolKind.Record)
                        {
                            var label = $"{sym.Kind.ToString().ToLowerInvariant()} {sym.Name}";
                            if (!keyTypes.Contains(label, StringComparer.OrdinalIgnoreCase))
                                keyTypes.Add(label);
                        }
                    }
                }
                feature.Context.KeyTypes = keyTypes;

                // Recompute dependencies
                if (_allFeaturesCache != null)
                {
                    var deps = DependencyAnalyzer.ComputeDependsOnForNewFeature(feature, _allFeaturesCache, projectPath);
                    feature.DependsOn = deps.OrderBy(d => d, StringComparer.Ordinal).ToList();
                }

                feature.TouchCount++;
                feature.LastUpdatedAt = DateTime.UtcNow;

                await registry.SaveFeatureAsync(projectPath, feature);
                AppLogger.Info("FeaturesTab", $"Full update completed for '{feature.Name}'");

                await LoadFeaturesAsync();

                // Re-select the updated feature in the list
                var updated = _featureEntries.FirstOrDefault(f => f.Id == feature.Id);
                if (updated != null)
                {
                    FeaturesListBox.SelectedItem = updated;
                    UpdateFeatureDetail(updated);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("FeaturesTab", $"Full update failed for '{feature.Name}': {ex.Message}", ex);
            }
        }

        private void CopyFeatureId_Click(object sender, RoutedEventArgs e)
        {
            var feature = GetFeatureFromContextMenu(sender);
            if (feature == null) return;

            Clipboard.SetText(feature.Id);
        }

        private async void RemoveFeature_Click(object sender, RoutedEventArgs e)
        {
            var feature = GetFeatureFromContextMenu(sender);
            if (feature == null || !_projectManager.HasProjects) return;

            var result = MessageBox.Show(
                $"Remove feature '{feature.Name}' from the registry?",
                "Remove Feature", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var projectPath = _projectManager.ProjectPath;
            var registry = new FeatureRegistryManager();

            try
            {
                await registry.RemoveFeatureAsync(projectPath, feature.Id);
                await LoadFeaturesAsync();
                UpdateFeatureDetail(null);
            }
            catch (Exception ex)
            {
                AppLogger.Error("FeaturesTab", $"Failed to remove feature '{feature.Name}': {ex.Message}", ex);
            }
        }

        private static FeatureEntry? GetFeatureFromContextMenu(object sender)
        {
            if (sender is not MenuItem menuItem) return null;
            if (menuItem.Parent is not ContextMenu contextMenu) return null;
            if (contextMenu.PlacementTarget is not Border border) return null;
            return border.DataContext as FeatureEntry;
        }
    }
}
