using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HappyEngine.Managers
{
    public class ImageAttachmentManager
    {
        private readonly List<string> _attachedImages = new();
        private readonly string _imageDir;
        private readonly TextBlock _imageIndicator;
        private readonly Button _clearImagesBtn;

        public ImageAttachmentManager(string appDataDir, TextBlock imageIndicator, Button clearImagesBtn)
        {
            _imageDir = Path.Combine(appDataDir, "images");
            Directory.CreateDirectory(_imageDir);
            _imageIndicator = imageIndicator;
            _clearImagesBtn = clearImagesBtn;
        }

        public int Count => _attachedImages.Count;

        public bool HandlePasteImage()
        {
            if (Clipboard.ContainsImage())
            {
                try
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        SaveClipboardImage(image);
                        return true;
                    }
                }
                catch (Exception ex) { AppLogger.Warn("ImageAttachment", "Failed to paste image from clipboard", ex); }
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var added = false;
                foreach (string? file in files)
                {
                    if (file != null && TaskLauncher.IsImageFile(file))
                    {
                        _attachedImages.Add(file);
                        added = true;
                    }
                }
                if (added)
                {
                    UpdateImageIndicator();
                    return true;
                }
            }

            return false;
        }

        public bool HandleDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Any(TaskLauncher.IsImageFile))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                    return true;
                }
            }
            return false;
        }

        public bool HandleDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    var added = false;
                    foreach (var file in files)
                    {
                        if (TaskLauncher.IsImageFile(file))
                        {
                            _attachedImages.Add(file);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        UpdateImageIndicator();
                        e.Handled = true;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Clears all attached images. Returns the number of files that could not be deleted.
        /// </summary>
        public int ClearImages()
        {
            int failures = 0;
            foreach (var path in _attachedImages)
            {
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    failures++;
                    AppLogger.Warn("ImageAttachment", $"Failed to delete image {Path.GetFileName(path)}: {ex.Message}");
                }
            }
            _attachedImages.Clear();
            UpdateImageIndicator();
            return failures;
        }

        public List<string>? DetachImages()
        {
            if (_attachedImages.Count == 0) return null;
            var result = new List<string>(_attachedImages);
            _attachedImages.Clear();
            UpdateImageIndicator();
            return result;
        }

        private void SaveClipboardImage(BitmapSource image)
        {
            var fileName = $"paste_{DateTime.Now:yyyyMMdd_HHmmss}_{_attachedImages.Count}.png";
            var filePath = Path.Combine(_imageDir, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            _attachedImages.Add(filePath);
            UpdateImageIndicator();
        }

        private void UpdateImageIndicator()
        {
            var count = _attachedImages.Count;
            _imageIndicator.Text = count > 0 ? $"[{count} image(s) attached]" : "";
            _imageIndicator.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _clearImagesBtn.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
