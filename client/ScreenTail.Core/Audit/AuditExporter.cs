using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenTail.Core.Audit;

/// <summary>
/// What exporting the audit log came to: the file and its verdict, or why there is no file.
/// </summary>
/// <param name="Path">The file written, or null when refused.</param>
/// <param name="Rows">How many rows the file carries.</param>
/// <param name="Verification">What verifying the chain found, in the same pass that wrote the rows.</param>
/// <param name="Refusal">Why nothing was written, or null when the file is there.</param>
public sealed record AuditExportResult(string? Path, long Rows, AuditVerification? Verification, string? Refusal)
{
    public static AuditExportResult Refused(string reason) => new(null, 0, null, reason);
}

/// <summary>
/// Writes the audit export to a file the service owns, and lets the UI take it from there (ST-045;
/// 2026-10-12 review, E-2).
///
/// The export used to be one string inside one pipe message. The audit log is never pruned and gains a
/// row for every frame, so within a few weeks that message passed the pipe's 1 MiB limit, the write
/// threw, and the UI lost its connection on every attempt from then on. Now the rows go from the store
/// to a file one at a time, verified in the same pass under one hold of the store's gate, and the pipe
/// carries only where the file is and what the verification said.
///
/// The service picks the place, never the UI. Taking a path off the pipe would give anything holding the
/// session token a way to have the service write wherever it liked. The UI asks the technician where to
/// keep the file, copies it there and deletes the service's copy; anything left behind by a UI that
/// died in between is swept by the next export, and "delete everything" takes the folder too (INV-12).
/// </summary>
public sealed partial class AuditExporter
{
    /// <summary>The folder under the data directory the service writes exports into.</summary>
    public const string DirectoryName = "exports";

    /// <summary>How long a file is left for the UI to collect before the next export sweeps it.</summary>
    public static readonly TimeSpan CollectWithin = TimeSpan.FromMinutes(10);

    private const int BufferBytes = 64 * 1024;

    private readonly Func<Func<AuditRecord, CancellationToken, ValueTask>, CancellationToken, Task<AuditVerification>> _read;
    private readonly string _directory;
    private readonly TimeProvider _time;

    /// <param name="read">
    /// Hands every row to the callback in order and returns the verdict on them, all under one hold of
    /// the store's gate. <c>SqliteSessionStore.ReadAuditAsync</c> in the service.
    /// </param>
    /// <param name="directory">Where the files go: <see cref="DirectoryName"/> under the data directory.</param>
    public AuditExporter(
        Func<Func<AuditRecord, CancellationToken, ValueTask>, CancellationToken, Task<AuditVerification>> read,
        string directory,
        TimeProvider? time = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Where the service writes exports, seen from the UI: the same user's data directory.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail",
        DirectoryName);

    /// <summary>
    /// A name the service could have given an export, and nothing else: no separators, no stream names,
    /// nothing that could point the UI's copy-and-delete at another file.
    /// </summary>
    [GeneratedRegex(@"^audit-\d{8}-\d{6}-[0-9a-f]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ExportName { get; }

    /// <summary>
    /// Writes the log and its verdict to a new file. Never throws for a disk or store failure: the
    /// answer goes back over the pipe, and a refusal with a reason is one the UI can show.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; the partial file is removed first.</exception>
    public async Task<AuditExportResult> ExportAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        string? path = null;
        try
        {
            _ = Directory.CreateDirectory(_directory);
            Sweep(now);

            path = Path.Combine(
                _directory,
                string.Create(CultureInfo.InvariantCulture, $"audit-{now.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));

            AuditVerification verdict;
            long rows;

            // CreateNew: a name is never reused, so a file already there is not ours to overwrite.
            await using (var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = BufferBytes,
            }))
            {
                await using var writer = new AuditJsonWriter(file, now);
                verdict = await _read(writer.WriteAsync, ct).ConfigureAwait(false);
                await writer.CompleteAsync(verdict, ct).ConfigureAwait(false);
                rows = writer.Rows;
            }

            return new AuditExportResult(path, rows, verdict, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException)
        {
            Discard(path);
            return AuditExportResult.Refused(ex is DbException
                ? "The audit log could not be read from the store."
                : "The audit log could not be written to disk.");
        }
        catch (OperationCanceledException)
        {
            Discard(path);
            throw;
        }
    }

    /// <summary>
    /// The UI's half: copies an export to where the technician chose, then deletes the service's copy.
    /// </summary>
    /// <param name="exported">The path the service replied with. Trusted only as far as it names one of its exports.</param>
    /// <param name="directory">Where the service writes exports: <see cref="DefaultDirectory"/> in the UI.</param>
    /// <returns>Null when the file is where the technician asked for it; otherwise why not.</returns>
    public static async Task<string?> CollectAsync(string exported, string destination, string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!IsExport(exported, directory))
        {
            // Not deleted either: a path that is not one of ours is not ours to remove.
            return "The capture service named a file outside its exports folder, so nothing was copied.";
        }

        try
        {
            if (!Path.IsPathFullyQualified(destination) || IsInside(destination, directory))
            {
                // Inside the folder the next export sweeps, the technician's copy would be deleted.
                return "Choose somewhere outside ScreenTail's own folder to keep the audit log.";
            }

            FileStream source;
            try
            {
                source = new FileStream(exported, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "The capture service's export was gone before it could be copied. Export it again.";
            }

            await using (source)
            {
                try
                {
                    await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Half a log is worse than none: it reads as complete, with a verdict missing.
                    Discard(destination);
                    throw;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The audit log could not be saved there.";
        }
        finally
        {
            Discard(exported);
        }
    }

    /// <summary>Whether a path names a file the service could have written into <paramref name="directory"/>.</summary>
    public static bool IsExport(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        return ExportName.IsMatch(Path.GetFileName(full))
            && string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsInside(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, PathComparison);
    }

    private static void Discard(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open by something else. The next export's sweep, or "delete everything", gets it.
        }
    }

    /// <summary>
    /// Removes exports nobody collected: a UI that died between the reply and the copy. Only files old
    /// enough that no UI could still be copying them, so two exports close together leave each other be.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.json"))
            {
                if (now - File.GetLastWriteTimeUtc(file) > CollectWithin)
                {
                    Discard(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tidying up is not the export. A folder that cannot be listed now gets swept next time.
        }
    }
}
