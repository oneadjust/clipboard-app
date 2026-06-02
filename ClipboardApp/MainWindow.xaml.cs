using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClipboardApp.Models;
using ClipboardApp.Services;
using WinClipboard = System.Windows.Clipboard;
using WinMessageBox = System.Windows.MessageBox;

namespace ClipboardApp;

public partial class MainWindow : Window, IDisposable
{
    private readonly ObservableCollection<ClipboardEntry> _entries = [];
    private readonly ClipboardStore _store = new();
    private ICollectionView? _view;
    private ClipboardWatcher? _watcher;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private string? _suppressedClipboardHash;
    private DateTime _suppressedClipboardHashExpiresAt;
    private ClipboardEntry? _armedEntry;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _entries;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    public async Task InitializeAsync()
    {
        var entries = await _store.LoadAsync();
        entries = await _store.PruneExpiredAsync(entries, TimeSpan.FromDays(3));

        foreach (var entry in entries.OrderByDescending(entry => entry.CreatedAt))
        {
            entry.Thumbnail = _store.LoadImage(entry.ImagePath);
            _entries.Add(entry);
        }

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = FilterEntry;
        UpdateCountText();
    }

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        ShowNearTray();
    }

    public void ShowNearTray()
    {
        PositionNearTray();
        Show();
        Activate();
        SearchBox.Focus();
    }

    public void ConfirmAndClearHistory()
    {
        Show();
        Activate();
        ClearHistoryWithConfirmation();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _persistLock.Dispose();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdrop.Enable(this);
        _watcher = new ClipboardWatcher(new WindowInteropHelper(this));
        _watcher.ClipboardChanged += ClipboardWatcher_ClipboardChanged;
    }

    private async void ClipboardWatcher_ClipboardChanged(object? sender, EventArgs e)
    {
        try
        {
            ClipboardEntry? entry = null;

            if (WinClipboard.ContainsText())
            {
                var text = WinClipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    entry = new ClipboardEntry
                    {
                        Type = ClipboardEntryType.Text,
                        ContentHash = _store.CreateTextHash(text),
                        Text = text,
                        CreatedAt = DateTime.Now
                    };
                }
            }
            else if (WinClipboard.ContainsImage())
            {
                var image = WinClipboard.GetImage();
                if (image is not null)
                {
                    entry = new ClipboardEntry
                    {
                        Type = ClipboardEntryType.Image,
                        CreatedAt = DateTime.Now
                    };
                    entry.ImagePath = _store.SaveImage(image, entry.Id);
                    entry.ContentHash = _store.CreateImageHash(entry.ImagePath);
                    entry.Thumbnail = _store.LoadImage(entry.ImagePath);
                }
            }

            if (entry is null)
            {
                return;
            }

            if (ShouldSuppressClipboardEntry(entry))
            {
                _store.DeleteEntryAssets(entry);
                return;
            }

            UpsertEntry(entry);
            await PersistAsync();
            RefreshView();
        }
        catch
        {
            // Clipboard can be temporarily locked by another process; ignore this update.
        }
    }

    private void UpsertEntry(ClipboardEntry entry)
    {
        var existing = _entries.FirstOrDefault(item =>
            string.Equals(item.ContentHash, entry.ContentHash, StringComparison.Ordinal));
        if (existing is null)
        {
            _entries.Insert(0, entry);
            return;
        }

        existing.CreatedAt = entry.CreatedAt;
        if (existing.Type == ClipboardEntryType.Image)
        {
            _store.DeleteEntryAssets(entry);
        }

        var oldIndex = _entries.IndexOf(existing);
        if (oldIndex > 0)
        {
            _entries.Move(oldIndex, 0);
        }
    }

    private bool FilterEntry(object item)
    {
        if (item is not ClipboardEntry entry)
        {
            return false;
        }

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return entry.Type == ClipboardEntryType.Text
            && (entry.Text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private async Task PersistAsync()
    {
        await _persistLock.WaitAsync();
        try
        {
            await _store.SaveAsync(_entries);
            UpdateCountText();
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private void RefreshView()
    {
        _view?.Refresh();
        UpdateCountText();
    }

    private void UpdateCountText()
    {
        if (CountText is null)
        {
            return;
        }

        CountText.Text = $"{_entries.Count} 条历史，自动保留 3 天";
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 18);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 18);
    }

    private async void EntryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FindEntryFromEvent(e.OriginalSource) is not { } entry)
        {
            return;
        }

        ArmEntry(null);
        await CopyEntryToClipboardAsync(entry);
    }

    private void EntryList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindEntryFromEvent(e.OriginalSource) is { } entry)
        {
            ArmEntry(entry);
        }
    }

    private void EntryList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_armedEntry is null)
        {
            return;
        }

        var current = FindEntryFromEvent(e.OriginalSource);
        if (current is not null && current != _armedEntry)
        {
            ArmEntry(null);
        }
    }

    private async Task CopyEntryToClipboardAsync(ClipboardEntry entry)
    {
        try
        {
            SuppressNextClipboardEntry(entry.ContentHash);

            if (entry.Type == ClipboardEntryType.Text && entry.Text is not null)
            {
                WinClipboard.SetText(entry.Text);
            }
            else if (entry.Type == ClipboardEntryType.Image && File.Exists(entry.ImagePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(entry.ImagePath!, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                WinClipboard.SetImage(bitmap);
            }

            await Task.Delay(80);
        }
        catch
        {
            ClearSuppressedClipboardEntry(entry.ContentHash);
        }
    }

    private void SuppressNextClipboardEntry(string contentHash)
    {
        _suppressedClipboardHash = contentHash;
        _suppressedClipboardHashExpiresAt = DateTime.Now.AddSeconds(2);
    }

    private bool ShouldSuppressClipboardEntry(ClipboardEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_suppressedClipboardHash)
            || DateTime.Now > _suppressedClipboardHashExpiresAt)
        {
            ClearSuppressedClipboardEntry();
            return false;
        }

        if (!string.Equals(_suppressedClipboardHash, entry.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private void ClearSuppressedClipboardEntry(string? contentHash = null)
    {
        if (contentHash is not null
            && !string.Equals(_suppressedClipboardHash, contentHash, StringComparison.Ordinal))
        {
            return;
        }

        _suppressedClipboardHash = null;
        _suppressedClipboardHashExpiresAt = DateTime.MinValue;
    }

    private void ArmEntry(ClipboardEntry? entry)
    {
        if (_armedEntry is not null)
        {
            _armedEntry.IsDeleteArmed = false;
        }

        _armedEntry = entry;
        if (_armedEntry is not null)
        {
            _armedEntry.IsDeleteArmed = true;
        }
    }

    private async void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipboardEntry entry)
        {
            return;
        }

        try
        {
            _store.DeleteEntryAssets(entry);
            _entries.Remove(entry);
            ArmEntry(null);
            await PersistAsync();
            RefreshView();
        }
        catch (Exception ex)
        {
            WinMessageBox.Show(
                this,
                $"删除失败：{ex.Message}",
                "删除历史",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        ArmEntry(null);
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // DragMove can throw if the mouse is released before the drag starts.
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshView();
    }

    private async void ClearHistoryWithConfirmation()
    {
        var result = WinMessageBox.Show(
            this,
            "确定要清空所有剪贴板历史吗？图片文件也会一起删除。",
            "清空历史",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.DeleteAllAssets(_entries);
            _entries.Clear();
            ArmEntry(null);
            await PersistAsync();
            RefreshView();
        }
        catch (Exception ex)
        {
            WinMessageBox.Show(
                this,
                $"清空失败：{ex.Message}",
                "清空历史",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private ClipboardEntry? FindEntryFromEvent(object originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement
                && frameworkElement.DataContext is ClipboardEntry entry)
            {
                return entry;
            }

            element = GetParent(element);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is System.Windows.Media.Visual
            || current is System.Windows.Media.Media3D.Visual3D)
        {
            return System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        if (current is FrameworkElement frameworkElement)
        {
            return frameworkElement.Parent;
        }

        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        return null;
    }
}
