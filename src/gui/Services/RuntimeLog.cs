using System;

namespace GameTranslator.Gui.Services;

internal static class RuntimeLog
{
    public static void Write(string message)
    {
#if DEBUG
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
#endif
    }
}
