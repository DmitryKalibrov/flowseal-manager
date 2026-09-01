using System.Reflection;

namespace FlowsealManager.App;

internal static class ApplicationVersion
{
    private static readonly Assembly Assembly = typeof(ApplicationVersion).Assembly;

    public static string Release => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => string.Equals(attribute.Key, "ReleaseVersion", StringComparison.Ordinal))
        .Value ?? throw new InvalidOperationException("Release version metadata is missing.");

    public static string ReleaseTag => $"v{Release}";

    public static string Build => Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("Build version metadata is missing.");
}
