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
    private readonly StartupService _startupService = new();
    private ICollectionView? _view;
    private ClipboardWatcher? _watcher;
    private bool _isInternalClipboardUpdate;
    private bool _isUpdatingStartupCheck;
    private ClipboardEntry? _armedEntry;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _entries;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    public event EventHandler<bool>? StartupChanged;

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
        SetStartupCheck(_startupService.IsEnabled());
    }

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        PositionNearTray();
        Show();
        Activate();
        SearchBox.Focus();
    }

    public void SetStartupCheck(bool enabled)
    {
        _isUpdatingStartupCheck = true;
        StartupCheckBox.IsChecked = enabled;
        _isUpdatingStartupCheck = false;
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
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdrop.Enable(this);
        _watcher = new ClipboardWatcher(new WindowInteropHelper(this));
        _watcher.ClipboardChanged += ClipboardWatcher_ClipboardChanged;
    }

    private async void ClipboardWatcher_ClipboardChanged(object? sender, EventArgs e)
    {
        if (_isInternalClipboardUpdate)
        {
            _isInternalClipboardUpdate = false;
            return;
        }

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
                    entry.Thumbnail = _store.LoadImage(entry.ImagePath);
                }
            }

            if (entry is null || IsDuplicateOfLatest(entry))
            {
                return;
            }

            _entries.Insert(0, entry);
            await PersistAsync();
            RefreshView();
        }
        catch
        {
            // Clipboard can be temporarily locked by another process; ignore this update.
        }
    }

    private bool IsDuplicateOfLatest(ClipboardEntry entry)
    {
        var latest = _entries.FirstOrDefault();
        if (latest is null || latest.Type != entry.Type)
        {
            return false;
        }

        if (entry.Type == ClipboardEntryType.Text)
        {
            return string.Equals(latest.Text, entry.Text, StringComparison.Ordinal);
        }

        return false;
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
        await _store.SaveAsync(_entries);
        UpdateCountText();
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
            _isInternalClipboardUpdate = true;

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
            _isInternalClipboardUpdate = false;
        }
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

        _store.DeleteEntryAssets(entry);
        _entries.Remove(entry);
        ArmEntry(null);
        await PersistAsync();
        RefreshView();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
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

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearHistoryWithConfirmation();
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

        _store.DeleteAllAssets(_entries);
        _entries.Clear();
        ArmEntry(null);
        await PersistAsync();
        RefreshView();
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingStartupCheck)
        {
            return;
        }

        var enabled = StartupCheckBox.IsChecked == true;
        _startupService.SetEnabled(enabled);
        StartupChanged?.Invoke(this, enabled);
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

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
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

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
