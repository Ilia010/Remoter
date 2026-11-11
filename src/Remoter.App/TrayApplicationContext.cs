using System.Diagnostics;
using Remoter.App.Features;

namespace Remoter.App;

/// <summary>Иконка в трее и меню управления.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _sleepItem;
    private readonly Action _shutdown;

    public TrayApplicationContext(AppConfig config, Action shutdown)
    {
        _shutdown = shutdown;

        var menu = new ContextMenuStrip();

        var openLogs = new ToolStripMenuItem("Открыть папку с логами");
        openLogs.Click += (_, _) => OpenFolder(config.LogDirectory);
        menu.Items.Add(openLogs);

        var openConfig = new ToolStripMenuItem("Открыть .env");
        openConfig.Click += (_, _) => OpenFile(config.LoadedEnvFile);
        menu.Items.Add(openConfig);

        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("Автозапуск при входе")
        {
            Checked = AutoStart.IsEnabled(),
            CheckOnClick = false,
        };
        _autoStartItem.Click += (_, _) =>
        {
            AutoStart.Toggle();
            _autoStartItem.Checked = AutoStart.IsEnabled();
        };
        menu.Items.Add(_autoStartItem);

        _sleepItem = new ToolStripMenuItem("Не давать ПК засыпать")
        {
            Checked = SleepGuard.IsActive,
            CheckOnClick = false,
            ToolTipText = "ПК не уйдёт в сон, пока работает Remoter. Экран может гаснуть.",
        };
        _sleepItem.Click += (_, _) =>
        {
            SleepGuard.Toggle();
            _sleepItem.Checked = SleepGuard.IsActive;
        };
        menu.Items.Add(_sleepItem);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Выход");
        exit.Click += (_, _) => _shutdown();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Remoter",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => OpenFolder(config.LogDirectory);
    }

    public void SetStatus(string text)
    {
        try
        {
            _icon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch
        {
            // NotifyIcon иногда капризничает
        }
    }

    public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _icon.ShowBalloonTip(4000, title, text, icon);
        }
        catch
        {
            // игнорируем
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть папку: {ex.Message}");
        }
    }

    private static void OpenFile(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                OpenFolder(Path.Combine(AppContext.BaseDirectory));
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть файл: {ex.Message}");
        }
    }
}
