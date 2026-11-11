using System.Runtime.InteropServices;
using Remoter.App.Features;
using Remoter.App.OpenCode;
using Remoter.App.Storage;
using Remoter.App.Telegram;

namespace Remoter.App;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        var selfTest = args.Contains("--selftest");

        // Self-test не должен молча выходить, если бот уже запущен: иначе он
        // рапортует «успех», ничего не проверив.
        if (!selfTest)
        {
            _mutex = new Mutex(true, @"Global\Remoter.SingleInstance", out var created);
            if (!created) return 0;
        }

        if (selfTest || args.Contains("--console")) ConsoleHelper.Attach();

        var config = AppConfig.Load(args);
        Log.Init(config.LogDirectory, config.TelegramBotToken);
        EnsureEnvTemplate(config);

        // Держим ПК активным, иначе во сне бот не получит сообщения и не выполнит задачу.
        if (config.PreventSleep) SleepGuard.Enable();

        if (args.Contains("--install-autostart"))
        {
            var ok = AutoStart.Enable();
            Console.WriteLine(ok ? "Автозапуск включён." : "Не удалось включить автозапуск (см. лог).");
            return ok ? 0 : 1;
        }

        if (args.Contains("--uninstall-autostart"))
        {
            var ok = AutoStart.Disable();
            Console.WriteLine(ok ? "Автозапуск выключен." : "Не удалось выключить автозапуск (см. лог).");
            return ok ? 0 : 1;
        }

        if (selfTest) return SelfTest.RunAsync(config).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ui = SynchronizationContext.Current!;

        using var cts = new CancellationTokenSource();
        var state = new StateStore(config.StatePath);
        var client = new OpenCodeClient(config);

        void Shutdown() => ui.Post(_ => Application.Exit(), null);

        var tray = new TrayApplicationContext(config, Shutdown);
        var bot = new BotService(config, state, client, Shutdown);

        tray.SetStatus(config.IsConfigured ? "Remoter — бот запущен" : "Remoter — не настроен");

        if (!config.IsConfigured)
        {
            var hint = string.IsNullOrWhiteSpace(config.TelegramBotToken)
                ? "Нет токена бота. Добавь TELEGRAM_BOT_TOKEN в .env рядом с Remoter.exe и перезапусти."
                : "Нет разрешённых ID. Напиши боту /start, он пришлёт твой ID, добавь его в ALLOWED_USER_IDS.";

            Log.Warn(hint);
            tray.Balloon("Remoter не настроен", hint, ToolTipIcon.Warning);
        }

        if (!string.IsNullOrWhiteSpace(config.TelegramBotToken))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.RunAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Error("Бот остановлен", ex);
                    tray.SetStatus("Remoter — ошибка");
                    tray.Balloon("Remoter", ex.Message, ToolTipIcon.Error);
                }
            });
        }

        Application.Run(tray);
        cts.Cancel();
        SleepGuard.Disable();
        return 0;
    }

    private static void EnsureEnvTemplate(AppConfig config)
    {
        if (config.LoadedEnvFile is not null) return;

        var path = Path.Combine(AppContext.BaseDirectory, ".env");
        if (File.Exists(path)) return;

        try
        {
            File.WriteAllText(path, EnvTemplate, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            config.LoadedEnvFile = path;
            Log.Info($"Создан шаблон настроек: {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось создать .env: {ex.Message}");
        }
    }

    private const string EnvTemplate = """
        # Токен от @BotFather. Обязателен.
        TELEGRAM_BOT_TOKEN=

        # Твой Telegram user ID. Бот пришлёт его после первого /start.
        # Можно несколько через запятую.
        ALLOWED_USER_IDS=

        # Необязательные переопределения
        # DEFAULT_DIRECTORY=C:\work\demo
        # MODEL_ID=deepseek-v4.1-flash-free
        # MODEL_PROVIDER=opencode
        # AGENT=build
        # APPROVAL_TIMEOUT_SECONDS=300
        # AUTO_START_OPENCODE=true

        # Не давать ПК уходить в сон, пока работает Remoter.
        # Экран при этом может гаснуть. Включай, если бот нужен круглосуточно.
        # PREVENT_SLEEP=false
        """;
}

internal static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static void Attach()
    {
        try
        {
            AttachConsole(AttachParentProcess);

            // Без этого кириллица в окне консоли превращается в мохнатые символы.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch
        {
            // консоли нет — просто пишем в лог
        }
    }
}
