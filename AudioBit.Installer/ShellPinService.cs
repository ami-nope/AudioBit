using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioBit.Installer;

internal static class ShellPinService
{
    private static readonly string[] PinToStartVerbs = ["pintostart", "startpin", "pintostartscreen"];
    private static readonly string[] UnpinFromStartVerbs = ["unpinfromstart", "startunpin", "unpinfromstartscreen"];
    private static readonly string[] PinToTaskbarVerbs = ["pintotaskbar", "taskbarpin"];
    private static readonly string[] UnpinFromTaskbarVerbs = ["unpinfromtaskbar", "taskbarunpin"];

    public static bool TryPinToStart(string itemPath) => TryInvokeShellVerb(itemPath, PinToStartVerbs);

    public static bool TryUnpinFromStart(string itemPath) => TryInvokeShellVerb(itemPath, UnpinFromStartVerbs);

    public static bool TryPinToTaskbar(string itemPath) => TryInvokeShellVerb(itemPath, PinToTaskbarVerbs);

    public static bool TryUnpinFromTaskbar(string itemPath) => TryInvokeShellVerb(itemPath, UnpinFromTaskbarVerbs);

    private static bool TryInvokeShellVerb(string itemPath, IReadOnlyList<string> candidateVerbs)
    {
        if (string.IsNullOrWhiteSpace(itemPath) || !File.Exists(itemPath))
        {
            return false;
        }

        var directoryPath = Path.GetDirectoryName(itemPath);
        var fileName = Path.GetFileName(itemPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return false;
        }

        object? shell = null;
        object? folder = null;
        object? item = null;
        object? verbs = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, [directoryPath]);
            if (folder is null)
            {
                return false;
            }

            var folderType = folder.GetType();
            item = folderType.InvokeMember("ParseName", BindingFlags.InvokeMethod, null, folder, [fileName]);
            if (item is null)
            {
                return false;
            }

            var normalizedCandidates = candidateVerbs
                .Select(NormalizeShellVerbName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var itemType = item.GetType();
            verbs = itemType.InvokeMember("Verbs", BindingFlags.InvokeMethod, null, item, null);
            if (verbs is not null && TryInvokeMatchingVerb(verbs, normalizedCandidates))
            {
                return true;
            }

            foreach (var candidateVerb in candidateVerbs)
            {
                try
                {
                    itemType.InvokeMember("InvokeVerb", BindingFlags.InvokeMethod, null, item, [candidateVerb]);
                    return true;
                }
                catch
                {
                    // Fall through to the next candidate.
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(verbs);
            ReleaseComObject(item);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static bool TryInvokeMatchingVerb(object verbs, HashSet<string> candidateVerbs)
    {
        var verbsType = verbs.GetType();
        var countValue = verbsType.InvokeMember("Count", BindingFlags.GetProperty, null, verbs, null);
        if (countValue is not int count || count <= 0)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            object? verb = null;

            try
            {
                verb = verbsType.InvokeMember("Item", BindingFlags.InvokeMethod, null, verbs, [index]);
                if (verb is null)
                {
                    continue;
                }

                var verbType = verb.GetType();
                var displayName = verbType.InvokeMember("Name", BindingFlags.GetProperty, null, verb, null) as string;
                if (!candidateVerbs.Contains(NormalizeShellVerbName(displayName)))
                {
                    continue;
                }

                verbType.InvokeMember("DoIt", BindingFlags.InvokeMethod, null, verb, null);
                return true;
            }
            catch
            {
                // Ignore a single verb failure and keep scanning.
            }
            finally
            {
                ReleaseComObject(verb);
            }
        }

        return false;
    }

    private static string NormalizeShellVerbName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
