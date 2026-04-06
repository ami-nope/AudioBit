namespace AudioBit.App.Infrastructure;

internal static class GoogleSheetsEndpointResolver
{
    public static string Resolve(string builtInEndpoint)
    {
        return ResolveCandidate(builtInEndpoint) ?? string.Empty;
    }

    private static string? ResolveCandidate(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
    }
}
