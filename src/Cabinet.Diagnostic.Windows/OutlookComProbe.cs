using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Cabinet.Diagnostic.Core;
using Microsoft.Win32;

namespace Cabinet.Diagnostic.Windows;

/// <summary>
/// Interroge Outlook classique par COM, en lecture seule. Ne démarre jamais Outlook,
/// ne crée, ne modifie, ne déplace et n'envoie rien. Aucune valeur lue n'est conservée.
/// </summary>
public sealed class OutlookComProbe : IOutlookProbe
{
    private const int FolderInbox = 6;
    private const int FolderCalendar = 9;
    private const int MailItemClass = 43;
    private const int MaxCalendarEvents = 500;
    private const int MaxItemsExamined = 20;

    public OutlookPresence DetectInstallation()
    {
        using var clsid = Registry.ClassesRoot.OpenSubKey(@"Outlook.Application\CLSID");
        return new OutlookPresence(clsid is not null, ReadBitness(), GuardPolicyDecoder.FromRegistryValue(ReadGuardValue()));
    }

    public OutlookFacts ReadFacts()
    {
        if (!IsOutlookRunning())
        {
            return new OutlookFacts(OutlookConnection.NotRunning, SafeText.Empty, null, null);
        }

        dynamic? app = null, ns = null, inbox = null, inboxItems = null;
        try
        {
            app = Connect();
            if (app is null)
            {
                return new OutlookFacts(OutlookConnection.Failed, SafeText.Empty, null, null);
            }

            SafeText version = SafeText.From((string)app.Version);
            ns = app.GetNamespace("MAPI");
            inbox = ns.GetDefaultFolder(FolderInbox);
            inboxItems = inbox.Items;
            int inboxCount = inboxItems.Count;

            int? upcoming = CountUpcomingEvents((object)ns);
            return new OutlookFacts(OutlookConnection.Connected, version, inboxCount, upcoming);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return new OutlookFacts(OutlookConnection.Failed, SafeText.Empty, null, null);
        }
        finally
        {
            Release((object?)inboxItems, (object?)inbox, (object?)ns, (object?)app);
        }
    }

    public GuardedAccessOutcome TryGuardedRead()
    {
        if (!IsOutlookRunning())
        {
            return GuardedAccessOutcome.Failed;
        }

        dynamic? app = null, ns = null, inbox = null, items = null;
        try
        {
            app = Connect();
            if (app is null)
            {
                return GuardedAccessOutcome.Failed;
            }

            ns = app.GetNamespace("MAPI");
            inbox = ns.GetDefaultFolder(FolderInbox);
            items = inbox.Items;
            items.Sort("[ReceivedTime]", true);

            var examined = 0;
            dynamic? item = items.GetFirst();
            while (item is not null && examined < MaxItemsExamined)
            {
                examined++;
                try
                {
                    if ((int)item.Class == MailItemClass)
                    {
                        int? length = ReadSenderAddressLength((object)item);
                        return length switch
                        {
                            null => GuardedAccessOutcome.Denied,
                            > 0 => GuardedAccessOutcome.Succeeded,
                            _ => GuardedAccessOutcome.Failed,
                        };
                    }
                }
                finally
                {
                    Release((object?)item);
                }

                item = items.GetNext();
            }

            return GuardedAccessOutcome.NoMailToTest;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return GuardedAccessOutcome.Failed;
        }
        finally
        {
            Release((object?)items, (object?)inbox, (object?)ns, (object?)app);
        }
    }

    /// <summary>
    /// Lit l'adresse de l'expéditeur (propriété protégée par Outlook) et n'en garde que la longueur.
    /// Renvoie null si Outlook refuse l'accès.
    /// </summary>
    private static int? ReadSenderAddressLength(object mail)
    {
        try
        {
            string? address = ((dynamic)mail).SenderEmailAddress;
            return address?.Length ?? 0;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static int? CountUpcomingEvents(object mapiNamespace)
    {
        dynamic? calendar = null, items = null, restricted = null;
        try
        {
            calendar = ((dynamic)mapiNamespace).GetDefaultFolder(FolderCalendar);
            items = calendar.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");

            var now = DateTime.Now;
            var culture = CultureInfo.CurrentCulture;
            var filter = $"[Start] >= '{now.ToString("g", culture)}' AND [Start] < '{now.AddDays(7).ToString("g", culture)}'";
            restricted = items.Restrict(filter);

            var count = 0;
            dynamic? item = restricted.GetFirst();
            while (item is not null && count < MaxCalendarEvents)
            {
                count++;
                Release((object?)item);
                item = restricted.GetNext();
            }

            Release((object?)item);
            return count;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Release((object?)restricted, (object?)items, (object?)calendar);
        }
    }

    private static dynamic? Connect()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application");
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static bool IsOutlookRunning()
    {
        var processes = Process.GetProcessesByName("OUTLOOK");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    private static void Release(params object?[] comObjects)
    {
        foreach (var comObject in comObjects)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }

    private static OfficeBitness ReadBitness()
    {
        var value = ReadLocalMachineString(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "Platform")
                    ?? ReadLocalMachineString(@"SOFTWARE\Microsoft\Office\16.0\Outlook", "Bitness");
        return value?.ToLowerInvariant() switch
        {
            "x64" => OfficeBitness.X64,
            "x86" => OfficeBitness.X86,
            _ => OfficeBitness.Unknown,
        };
    }

    private static int? ReadGuardValue()
    {
        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Office\16.0\Outlook\Security",
                     @"SOFTWARE\Microsoft\Office\ClickToRun\REGISTRY\MACHINE\Software\Microsoft\Office\16.0\Outlook\Security",
                 })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(path);
                if (key?.GetValue("ObjectModelGuard") is int value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadLocalMachineString(string path, string name)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(path);
            if (key?.GetValue(name) is string value)
            {
                return value;
            }
        }

        return null;
    }
}
