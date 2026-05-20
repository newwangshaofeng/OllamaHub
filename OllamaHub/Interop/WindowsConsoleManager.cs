using System.Runtime.InteropServices;

namespace OllamaHub.Interop;

internal static class WindowsConsoleManager
{
    public static bool ShouldEnableConsole(Microsoft.Extensions.Logging.LogLevel minLogLevel) =>
        OperatingSystem.IsWindows() && minLogLevel != Microsoft.Extensions.Logging.LogLevel.None;

    public static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return;
        }

        const int AttachParentProcess = -1;
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();
}