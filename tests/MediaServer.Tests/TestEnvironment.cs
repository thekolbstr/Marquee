using System.Runtime.CompilerServices;

namespace MediaServer.Tests;

/// <summary>
/// Most tests exercise the API directly, so sign-in is off for them. Tests of sign-in itself turn it back on explicitly
/// (see AuthTestSupport), which overrides this environment default.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("Auth__Enabled", "false");
        Environment.SetEnvironmentVariable("Library__AllowFolderEditing", "true");
    }
}
