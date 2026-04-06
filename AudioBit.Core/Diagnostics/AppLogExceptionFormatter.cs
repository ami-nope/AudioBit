using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioBit.Core.Diagnostics;

public static class AppLogExceptionFormatter
{
    public static string Format(
        string category,
        string summary,
        Exception exception,
        string? messageDetails = null,
        IEnumerable<KeyValuePair<string, object?>>? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder(2048);
        AppendField(builder, "FormatVersion", "2");
        AppendField(builder, "Category", category);
        AppendField(builder, "Summary", summary);
        AppendBlock(builder, "MessageDetails", messageDetails);
        AppendField(builder, "ObservedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        AppendField(builder, "ThreadId", Environment.CurrentManagedThreadId);
        AppendField(builder, "ProcessId", Environment.ProcessId);
        AppendField(builder, "ProcessPath", Environment.ProcessPath);
        AppendField(builder, "MachineName", Environment.MachineName);
        AppendField(builder, "UserInteractive", Environment.UserInteractive);
        AppendField(builder, "BaseDirectory", AppContext.BaseDirectory);
        AppendField(builder, "FrameworkDescription", RuntimeInformation.FrameworkDescription);
        AppendField(builder, "RuntimeIdentifier", RuntimeInformation.RuntimeIdentifier);
        AppendField(builder, "OsDescription", RuntimeInformation.OSDescription);
        AppendField(builder, "OsArchitecture", RuntimeInformation.OSArchitecture);
        AppendField(builder, "ProcessArchitecture", RuntimeInformation.ProcessArchitecture);
        AppendField(builder, "Is64BitProcess", Environment.Is64BitProcess);

        if (context is not null)
        {
            foreach (var field in context.OrderBy(field => field.Key, StringComparer.Ordinal))
            {
                AppendField(builder, $"Context.{field.Key}", field.Value);
            }
        }

        AppendException(builder, "Exception", exception);
        return builder.ToString().TrimEnd();
    }

    private static void AppendException(StringBuilder builder, string prefix, Exception exception)
    {
        AppendField(builder, $"{prefix}.Type", exception.GetType().FullName ?? exception.GetType().Name);
        AppendField(builder, $"{prefix}.Message", exception.Message);
        AppendField(builder, $"{prefix}.Source", exception.Source);
        AppendField(builder, $"{prefix}.TargetSite", exception.TargetSite?.ToString());
        AppendField(builder, $"{prefix}.HelpLink", exception.HelpLink);
        AppendField(builder, $"{prefix}.HResult", $"0x{exception.HResult:X8}");
        AppendData(builder, prefix, exception.Data);
        AppendBlock(builder, $"{prefix}.StackTrace", exception.StackTrace);
        AppendBlock(builder, $"{prefix}.ToString", exception.ToString());

        var inner = exception.InnerException;
        var index = 0;
        while (inner is not null)
        {
            AppendException(builder, $"{prefix}.Inner[{index}]", inner);
            inner = inner.InnerException;
            index++;
        }
    }

    private static void AppendData(StringBuilder builder, string prefix, IDictionary data)
    {
        if (data.Count == 0)
        {
            return;
        }

        var rows = new List<(string Key, object? Value)>(data.Count);
        foreach (DictionaryEntry entry in data)
        {
            rows.Add((entry.Key?.ToString() ?? "(null)", entry.Value));
        }

        foreach (var row in rows.OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            AppendField(builder, $"{prefix}.Data.{row.Key}", row.Value);
        }
    }

    private static void AppendField(StringBuilder builder, string name, object? value)
    {
        builder.Append(name)
            .Append(": ")
            .AppendLine(FormatValue(value));
    }

    private static void AppendBlock(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(name).Append(':').AppendLine();
        using var reader = new StringReader(value.Trim());
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            builder.Append("  ").AppendLine(line);
        }
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();
        }

        if (value is DateTimeOffset dto)
        {
            return dto.ToString("O");
        }

        if (value is DateTime dt)
        {
            return dt.ToString("O");
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                parts.Add(FormatValue(item));
            }

            return parts.Count == 0 ? "[]" : $"[{string.Join(", ", parts)}]";
        }

        return value.ToString() ?? "(null)";
    }
}
