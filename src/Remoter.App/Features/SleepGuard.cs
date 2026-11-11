using System.Runtime.InteropServices;

namespace Remoter.App.Features;

/// <summary>
/// Не даёт Windows уйти в сон, пока работает Remoter.
/// Выключение экрана при этом не блокируется: гаснуть монитору можно.
/// </summary>
public static class SleepGuard
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    public static bool IsActive { get; private set; }

    public static bool Enable()
    {
        try
        {
            // ES_CONTINUOUS + ES_SYSTEM_REQUIRED: система не засыпает, экран — можно.
            IsActive = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
            if (IsActive) Log.Info("Блокировка сна включена: ПК не заснёт, пока работает Remoter");
            return IsActive;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось запретить сон: {ex.Message}");
            return false;
        }
    }

    public static void Disable()
    {
        try
        {
            SetThreadExecutionState(EsContinuous);
            IsActive = false;
            Log.Info("Блокировка сна выключена");
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось снять блокировку сна: {ex.Message}");
        }
    }

    public static bool Toggle()
    {
        if (IsActive)
        {
            Disable();
            return false;
        }

        return Enable();
    }
}
