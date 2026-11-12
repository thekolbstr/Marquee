using System.Reflection;

namespace MediaServer.Api.Maintenance;

/// <summary>The version this build reports: the APP_VERSION environment variable if set (the Docker image does), else the version stamped at build time.</summary>
public static class AppVersion
{
    public static string Current { get; } =
        Environment.GetEnvironmentVariable("APP_VERSION") is { Length: > 0 and <= 40 } v ? v
        : Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
}
