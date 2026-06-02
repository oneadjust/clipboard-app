using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ClipboardApp.Models;
using Microsoft.Data.Sqlite;

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
        DatabasePath = Path.Combine(RootPath, "history.db");

        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ImagesPath);
    }

    public string RootPath { get; }
    public string ImagesPath { get; }
    public string IndexPath { get; }
    public string DatabasePath { get; }

    public async Task<List<ClipboardEntry>> LoadAsync()
    {
        await EnsureDatabaseAsync();
        await MigrateJsonIfNeededAsync();

        var entries = new List<ClipboardEntry>();
        await using var connection = OpenConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, ContentHash, Text, ImagePath, CreatedAtTicks
            FROM ClipboardEntries
            ORDER BY CreatedAtTicks DESC
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new ClipboardEntry
            {
                Id = reader.GetString(0),
                Type = (ClipboardEntryType)reader.GetInt32(1),
                ContentHash = reader.GetString(2),
                Text = reader.IsDBNull(3) ? null : reader.GetString(3),
                ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = new DateTime(reader.GetInt64(5), DateTimeKind.Local)
            });
        }

        return entries;
    }

    public async Task SaveAsync(IEnumerable<ClipboardEntry> entries)
    {
        await EnsureDatabaseAsync();
        await SaveEntriesToDatabaseAsync(entries);
    }

    private async Task SaveEntriesToDatabaseAsync(IEnumerable<ClipboardEntry> entries)
    {
        var snapshot = entries
            .Select(EnsureHash)
            .GroupBy(entry => entry.ContentHash, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(entry => entry.CreatedAt).First())
            .OrderByDescending(entry => entry.CreatedAt)
            .ToList();

        await using var connection = OpenConnection();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM ClipboardEntries";
            await deleteCommand.ExecuteNonQueryAsync();
        }

        foreach (var entry in snapshot)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO ClipboardEntries (Id, Type, ContentHash, Text, ImagePath, CreatedAtTicks)
                VALUES ($id, $type, $hash, $text, $imagePath, $createdAtTicks)
                """;
            insertCommand.Parameters.AddWithValue("$id", entry.Id);
            insertCommand.Parameters.AddWithValue("$type", (int)entry.Type);
            insertCommand.Parameters.AddWithValue("$hash", entry.ContentHash);
            insertCommand.Parameters.AddWithValue("$text", (object?)entry.Text ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$imagePath", (object?)entry.ImagePath ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$createdAtTicks", entry.CreatedAt.Ticks);
            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
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

    public string CreateTextHash(string text)
    {
        return $"text:{CreateHash(Encoding.UTF8.GetBytes(text))}";
    }

    public string CreateImageHash(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return $"image:missing:{path}";
        }

        return $"image:{CreateHash(File.ReadAllBytes(path))}";
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

    private async Task EnsureDatabaseAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();

        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS ClipboardEntries (
                Id TEXT PRIMARY KEY,
                Type INTEGER NOT NULL,
                ContentHash TEXT NOT NULL UNIQUE,
                Text TEXT NULL,
                ImagePath TEXT NULL,
                CreatedAtTicks INTEGER NOT NULL
            );
            """;
            await tableCommand.ExecuteNonQueryAsync();
        }

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_ClipboardEntries_CreatedAtTicks
            ON ClipboardEntries (CreatedAtTicks DESC);
            """;
            await indexCommand.ExecuteNonQueryAsync();
        }
    }

    private async Task MigrateJsonIfNeededAsync()
    {
        if (!File.Exists(IndexPath))
        {
            return;
        }

        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM ClipboardEntries";
        var existingCount = (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
        if (existingCount > 0)
        {
            return;
        }

        var entries = await LoadJsonEntriesAsync();
        if (entries.Count == 0)
        {
            return;
        }

        await SaveEntriesToDatabaseAsync(entries);
        BackupMigratedJson();
    }

    private async Task<List<ClipboardEntry>> LoadJsonEntriesAsync()
    {
        try
        {
            await using var stream = File.OpenRead(IndexPath);
            if (stream.Length == 0)
            {
                return [];
            }

            var entries = await JsonSerializer.DeserializeAsync<List<ClipboardEntry>>(stream, JsonOptions);
            return (entries ?? []).Select(EnsureHash).ToList();
        }
        catch (JsonException)
        {
            BackupCorruptIndex();
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private ClipboardEntry EnsureHash(ClipboardEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ContentHash))
        {
            return entry;
        }

        entry.ContentHash = entry.Type == ClipboardEntryType.Text
            ? CreateTextHash(entry.Text ?? string.Empty)
            : CreateImageHash(entry.ImagePath ?? string.Empty);
        return entry;
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath
        };
        return new SqliteConnection(builder.ToString());
    }

    private static string CreateHash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
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

    private void BackupMigratedJson()
    {
        try
        {
            var backupPath = Path.Combine(
                RootPath,
                $"history.migrated.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(IndexPath, backupPath, true);
        }
        catch
        {
            // Migration already succeeded; keeping the old JSON is harmless.
        }
    }

    private void BackupCorruptIndex()
    {
        try
        {
            var backupPath = Path.Combine(
                RootPath,
                $"history.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(IndexPath, backupPath, true);
        }
        catch
        {
            // If backup fails, start with an empty in-memory history anyway.
        }
    }
}
