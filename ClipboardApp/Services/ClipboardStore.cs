using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ClipboardApp.Models;

namespace ClipboardApp.Services;

public sealed class ClipboardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ClipboardStore()
    {
        RootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardApp");
        ImagesPath = Path.Combine(RootPath, "images");
        IndexPath = Path.Combine(RootPath, "history.json");

        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ImagesPath);
    }

    public string RootPath { get; }
    public string ImagesPath { get; }
    public string IndexPath { get; }

    public async Task<List<ClipboardEntry>> LoadAsync()
    {
        if (!File.Exists(IndexPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(IndexPath);
        var entries = await JsonSerializer.DeserializeAsync<List<ClipboardEntry>>(stream, JsonOptions);
        return entries ?? [];
    }

    public async Task SaveAsync(IEnumerable<ClipboardEntry> entries)
    {
        await using var stream = File.Create(IndexPath);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions);
    }

    public async Task<List<ClipboardEntry>> PruneExpiredAsync(List<ClipboardEntry> entries, TimeSpan maxAge)
    {
        var threshold = DateTime.Now.Subtract(maxAge);
        var expired = entries.Where(entry => entry.CreatedAt < threshold).ToList();
        foreach (var entry in expired)
        {
            DeleteImageFile(entry);
        }

        var kept = entries.Except(expired).OrderByDescending(entry => entry.CreatedAt).ToList();
        await SaveAsync(kept);
        return kept;
    }

    public string SaveImage(BitmapSource source, string id)
    {
        var path = Path.Combine(ImagesPath, $"{id}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public BitmapImage? LoadImage(string? path, int decodeWidth = 140)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void DeleteEntryAssets(ClipboardEntry entry)
    {
        DeleteImageFile(entry);
    }

    public void DeleteAllAssets(IEnumerable<ClipboardEntry> entries)
    {
        foreach (var entry in entries)
        {
            DeleteImageFile(entry);
        }
    }

    private static void DeleteImageFile(ClipboardEntry entry)
    {
        if (entry.Type != ClipboardEntryType.Image || string.IsNullOrWhiteSpace(entry.ImagePath))
        {
            return;
        }

        try
        {
            if (File.Exists(entry.ImagePath))
            {
                File.Delete(entry.ImagePath);
            }
        }
        catch
        {
            // Best effort cleanup; the entry can still be removed from the index.
        }
    }
}
