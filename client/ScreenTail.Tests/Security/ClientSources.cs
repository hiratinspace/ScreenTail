namespace ScreenTail.Tests.Security;

/// <summary>
/// The client's source files, for tests that look at real files rather than at what a build produced.
///
/// Walks up from the test binary to the repository, so it works from a checkout on any machine and
/// returns nothing, rather than a wrong answer, from anywhere else. The tests that use it assert the
/// list is not empty first, so a moved checkout fails loudly.
/// </summary>
internal static class ClientSources
{
    public static IReadOnlyList<string> Of(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", project);
            if (Directory.Exists(candidate))
            {
                return
                [
                    .. Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories)
                        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
                ];
            }

            directory = directory.Parent;
        }

        return [];
    }
}
