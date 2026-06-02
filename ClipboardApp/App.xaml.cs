using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using ClipboardApp.Services;

namespace ClipboardApp;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\ClipboardApp.SingleInstance";
    private const string ActivationEventName = @"Local\ClipboardApp.Activate";

    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _window;
    private StartupService? _startupService;
    private Forms.ToolStripMenuItem? _showHideMenuItem;
    private Forms.ToolStripMenuItem? _startupMenuItem;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsSingleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowNearTray()),
            null,
            -1,
            false);

        base.OnStartup(e);

        _startupService = new StartupService();
        _window = new MainWindow();
        _ = new WindowInteropHelper(_window).EnsureHandle();
        try
        {
            await _window.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"初始化失败：{ex.Message}",
                "剪切板",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _notifyIcon?.Dispose();
        _window?.Dispose();
        if (_ownsSingleInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void CreateTrayIcon()
    {
        if (_window is null || _startupService is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        _showHideMenuItem = new Forms.ToolStripMenuItem("显示窗口");
        _showHideMenuItem.Click += (_, _) => _window.ToggleNearTray();
        menu.Opening += (_, _) =>
        {
            if (_showHideMenuItem is not null)
            {
                _showHideMenuItem.Text = _window.IsVisible ? "隐藏窗口" : "显示窗口";
            }
        };

        _startupMenuItem = new Forms.ToolStripMenuItem("开机自启")
        {
            Checked = _startupService.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.CheckedChanged += (_, _) =>
        {
            _startupService.SetEnabled(_startupMenuItem.Checked);
        };

        var clearItem = new Forms.ToolStripMenuItem("清空历史");
        clearItem.Click += (_, _) => _window.ConfirmAndClearHistory();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(_showHideMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "剪切板",
            Icon = LoadTrayIcon(),
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
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }
}
