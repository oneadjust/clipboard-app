using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using ClipboardApp.Services;

namespace ClipboardApp;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _window;
    private StartupService? _startupService;
    private Forms.ToolStripMenuItem? _startupMenuItem;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _startupService = new StartupService();
        _window = new MainWindow();
        _ = new WindowInteropHelper(_window).EnsureHandle();
        await _window.InitializeAsync();

        CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _window?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        if (_window is null || _startupService is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        _startupMenuItem = new Forms.ToolStripMenuItem("开机自启")
        {
            Checked = _startupService.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.CheckedChanged += (_, _) =>
        {
            _startupService.SetEnabled(_startupMenuItem.Checked);
            _window.SetStartupCheck(_startupMenuItem.Checked);
        };

        var clearItem = new Forms.ToolStripMenuItem("清空历史");
        clearItem.Click += (_, _) => _window.ConfirmAndClearHistory();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "剪切板",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                _window.ToggleNearTray();
            }
        };

        _window.StartupChanged += (_, enabled) =>
        {
            if (_startupMenuItem is not null && _startupMenuItem.Checked != enabled)
            {
                _startupMenuItem.Checked = enabled;
            }
        };
    }
}
