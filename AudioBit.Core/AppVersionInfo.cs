using System.Reflection;
using System.Text;

namespace AudioBit.Core;

public static class AppVersionInfo
{
    public static string GetCurrentVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim()
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    public static string GetDisplayVersion(Assembly assembly)
    {
        return NormalizeForDisplay(GetCurrentVersion(assembly));
    }

    public static string NormalizeForDisplay(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var trimmed = version.Trim();
        var numericBuilder = new StringBuilder(trimmed.Length);

        foreach (var character in trimmed)
        {
            if (char.IsDigit(character) || character == '.')
            {
                numericBuilder.Append(character);
                continue;
            }

            break;
        }

        var candidate = numericBuilder.ToString().Trim('.');
        if (!Version.TryParse(candidate, out var parsed))
        {
            return trimmed;
        }

        if (parsed.Revision > 0)
        {
            return $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}.{parsed.Revision}";
        }

        return $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}";
    }
}
