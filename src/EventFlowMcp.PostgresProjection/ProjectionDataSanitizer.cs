namespace EventFlowMcp.PostgresProjection;

/// <summary>Defence in depth for data written to the shared operational store.</summary>
internal static class ProjectionDataSanitizer
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "authorization", "cookie", "secret", "password", "token", "api-key", "apikey"
    ];

    internal static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
            return new Dictionary<string, string>();

        var sanitized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            sanitized[pair.Key] = IsSensitive(pair.Key) ? "[redacted]" : Truncate(pair.Value, 4_096);
        }

        return sanitized;
    }

    private static bool IsSensitive(string key)
        => SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";
}
