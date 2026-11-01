namespace ScreenTail.Api.Hosting;

/// <summary>
/// Secrets read from files rather than the environment (ST-007). A deployment mounts one file per
/// secret in a directory, named after its configuration key with <c>__</c> for the colon
/// (<c>Jwt__SigningKey</c>, <c>ConnectionStrings__Postgres</c>), and names the directory in
/// <c>SCREENTAIL_SECRETS_DIR</c>; Docker's secrets land in <c>/run/secrets</c> this way.
///
/// Why files: an environment variable is shown by <c>docker inspect</c>, kept in the container's
/// definition and inherited by every child process, and a file is none of those. Added last, so a
/// value in a file wins over the same key in the environment. Unset, nothing changes. Set, the
/// directory must exist: a deployment that named one and lost it would otherwise start without its
/// keys and fail somewhere less obvious.
/// </summary>
public static class SecretFiles
{
    public const string DirectoryVariable = "SCREENTAIL_SECRETS_DIR";

    public static IConfigurationBuilder AddSecretFiles(this IConfigurationBuilder configuration, string? directory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return configuration;
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new InvalidOperationException($"{DirectoryVariable} must be an absolute path.");
        }

        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"{DirectoryVariable} names a directory that does not exist.");
        }

        // The provider trims one trailing newline, which is how a secret written by an editor or `echo`
        // arrives. "__" is its default section delimiter, as in environment variables.
        return configuration.AddKeyPerFile(directory, optional: false, reloadOnChange: false);
    }
}
