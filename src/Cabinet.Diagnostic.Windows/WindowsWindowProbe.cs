using System.Diagnostics;
using System.Runtime.InteropServices;
using Cabinet.Diagnostic.Core;

namespace Cabinet.Diagnostic.Windows;

/// <summary>Observe la fenêtre au premier plan et la durée d'inactivité, sans lire aucun titre.</summary>
public sealed class WindowsWindowProbe : IWindowProbe
{
    public WindowSample? SampleForeground()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == 0)
        {
            return null;
        }

        var titleLength = Math.Max(0, NativeMethods.GetWindowTextLength(handle));
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);

        var processNameRead = false;
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processNameRead = process.ProcessName.Length > 0;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                processNameRead = false;
            }
        }

        return new WindowSample(processNameRead, titleLength);
    }

    public TimeSpan? IdleTime()
    {
        var info = new NativeMethods.LastInputInfo { CbSize = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return null;
        }

        // Arithmétique 32 bits non signée : reste juste quand le compteur repasse par zéro (tous les 49,7 jours).
        var idleMilliseconds = unchecked((uint)Environment.TickCount64 - info.DwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }
}
