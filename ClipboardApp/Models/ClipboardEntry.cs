using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace ClipboardApp.Models;

public enum ClipboardEntryType
{
    Text,
    Image
}

public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private bool _isDeleteArmed;
    private BitmapImage? _thumbnail;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipboardEntryType Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Text { get; set; }
    public string? ImagePath { get; set; }

    public bool IsDeleteArmed
    {
        get => _isDeleteArmed;
        set
        {
            if (_isDeleteArmed == value)
            {
                return;
            }

            _isDeleteArmed = value;
            OnPropertyChanged();
        }
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public string PreviewText
    {
        get
        {
            if (Type == ClipboardEntryType.Image)
            {
                return "图片";
            }

            var text = Text ?? string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 180 ? text[..180] + "..." : text;
        }
    }

    public string TypeLabel => Type == ClipboardEntryType.Text ? "文字" : "图片";
    public string TimeLabel => CreatedAt.ToString("MM-dd HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
