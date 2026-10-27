namespace ScreenTail.Core.Install;

/// <summary>
/// What an installed ScreenTail puts on a machine outside its own folder, and how the uninstall finds it
/// again (ST-112, ADR-0007).
/// </summary>
public static class InstalledFootprint
{
    public const string ServiceExecutable = "ScreenTail.Service.exe";

    public const string UiExecutable = "ScreenTail.UI.exe";

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ServiceRunValue = "ScreenTail.Service";

    public const string UninstallFlag = "--uninstall-cleanup";

    public static IReadOnlyList<string> DataDirectories(IEnumerable<string?> profileDirectories) => throw new NotImplementedException();

    public static string? ExecutableOf(string? command) => throw new NotImplementedException();

    public static bool IsInside(string? path, string installDirectory) => throw new NotImplementedException();

    public static IReadOnlyList<string> RunValuesToRemove(IEnumerable<KeyValuePair<string, string?>> values, string installDirectory) => throw new NotImplementedException();
}
