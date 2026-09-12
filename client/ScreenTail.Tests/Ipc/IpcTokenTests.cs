using ScreenTail.Core.Ipc;

namespace ScreenTail.Tests.Ipc;

public class IpcTokenTests
{
    [Fact]
    public void GeneratedTokensAreFreshAndMatchThemselves()
    {
        var a = IpcToken.Generate();
        var b = IpcToken.Generate();

        Assert.Equal(IpcToken.Length, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(IpcToken.Matches(a, IpcToken.Encode(a)));
        Assert.False(IpcToken.Matches(a, IpcToken.Encode(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("0011")]
    public void GarbageNeverMatches(string presented)
    {
        Assert.False(IpcToken.Matches(IpcToken.Generate(), presented));
    }

    [Fact]
    public void TokenFileRoundTripsAndIsPrivate()
    {
        var path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}", "ipc.token");
        var token = IpcToken.Generate();
        try
        {
            IpcTokenFile.Write(path, token);

            Assert.Equal(token, IpcTokenFile.Read(path));
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void PipeNamesAreStablePerUserAndSafe()
    {
        var name = IpcPipeNames.ForUser("S-1-5-21-1-2-3-1001");

        Assert.Equal(name, IpcPipeNames.ForUser("S-1-5-21-1-2-3-1001"));
        Assert.NotEqual(name, IpcPipeNames.ForUser("S-1-5-21-1-2-3-1002"));
        Assert.Matches("^ScreenTail\\.[0-9A-F]{16}$", name);
    }
}
