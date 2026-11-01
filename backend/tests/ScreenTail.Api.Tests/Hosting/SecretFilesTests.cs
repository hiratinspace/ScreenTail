using Microsoft.Extensions.Configuration;
using ScreenTail.Api.Hosting;

namespace ScreenTail.Api.Tests.Hosting;

/// <summary>
/// A deployment's secrets as one file per key (ST-007, Docker secrets in <c>/run/secrets</c>): each file
/// is the key its name spells with <c>__</c> for the colon, the trailing newline an editor leaves is not
/// part of the value, and a file wins over the same key in the environment.
/// </summary>
public sealed class SecretFilesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("screentail-secret-files-").FullName;

    // Assembled here so nothing secret-shaped sits in the source.
    private static readonly string Key = string.Concat("file-", Guid.NewGuid().ToString("N"), "-key");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void EachFileIsTheKeyItsNameSpellsAndOverridesTheEnvironment()
    {
        File.WriteAllText(Path.Combine(_directory, "Jwt__SigningKey"), Key + "\n");
        File.WriteAllText(Path.Combine(_directory, "ConnectionStrings__Postgres"), "Host=db;Database=screentail");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = "from-the-environment" })
            .AddSecretFiles(_directory)
            .Build();

        Assert.Equal(Key, configuration["Jwt:SigningKey"]);
        Assert.Equal("Host=db;Database=screentail", configuration.GetConnectionString("Postgres"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnsetChangesNothing(string? unset)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = Key })
            .AddSecretFiles(unset)
            .Build();

        Assert.Equal(Key, configuration["Jwt:SigningKey"]);
    }

    [Fact]
    public void ADirectoryThatIsNotThereOrIsRelativeIsRefused()
    {
        var missing = Path.Combine(_directory, "not-there");

        var gone = Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().AddSecretFiles(missing));
        var relative = Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().AddSecretFiles("secrets"));

        Assert.Contains(SecretFiles.DirectoryVariable, gone.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, gone.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", relative.Message, StringComparison.Ordinal);
    }
}
