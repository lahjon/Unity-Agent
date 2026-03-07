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

        private void RefreshFeatures_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadFeaturesAsync();
        }
    }
}
