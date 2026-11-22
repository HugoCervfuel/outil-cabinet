using System.Runtime.ExceptionServices;

namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Exécute un travail sur un thread dédié avec un délai maximal, pour qu'un Outlook bloqué ne fige jamais l'outil.
/// </summary>
public static class TimedCall
{
    public static T Run<T>(Func<T> work, TimeSpan timeout, T onTimeout, bool sta)
    {
        T result = onTimeout;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Diagnostic",
        };

        if (sta && OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        if (!thread.Join(timeout))
        {
            return onTimeout;
        }

        failure?.Throw();
        return result;
    }
}
