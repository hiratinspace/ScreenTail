using System.Globalization;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Vault;

/// <summary>
/// What a caller is told when the vault will not open a stored credential (2026-11-15 review): a row
/// bound to another tenant, provider or site, a legacy row while legacy rows are refused, or a row
/// that does not open at all. A provider built from such a row answers every call with this error, so
/// publish, ticket search, company mappings and Test connection each say it in words instead of failing
/// with a bare 500. Not retryable: trying again opens the same row.
/// </summary>
public static class VaultRefusal
{
    public const string What = "The stored credential could not be opened, so it was not used.";

    /// <summary>For a technician's device. Only the owner connects a credential.</summary>
    public const string Todo = "Ask your owner to connect it again in the web admin.";

    private const string AskTheOwner = "Ask your owner to ";

    public static ProviderError Error { get; } = new(ProviderErrorKind.Unauthenticated, What, Todo);

    /// <summary>
    /// A sentence written for a technician, as the owner should read it: "Ask your owner to check the
    /// site address in the web admin." becomes "Check the site address in the web admin." Anything else
    /// is returned as it is.
    /// </summary>
    public static string? ForOwner(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var at = message.IndexOf(AskTheOwner, StringComparison.Ordinal);
        if (at < 0)
        {
            return message;
        }

        var rest = message[(at + AskTheOwner.Length)..];
        return rest.Length == 0
            ? message
            : string.Concat(message.AsSpan(0, at), char.ToUpper(rest[0], CultureInfo.InvariantCulture).ToString(), rest.AsSpan(1));
    }
}

/// <summary>A PSA provider for a tenant whose stored credential the vault refused: every call answers <see cref="VaultRefusal.Error"/>.</summary>
public sealed class RefusedPsaProvider(string name) : IPsaProvider
{
    public string Name => name;

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<bool>(VaultRefusal.Error));

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(VaultRefusal.Error));

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(VaultRefusal.Error));

    public Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<PublishedNote>(VaultRefusal.Error));

    public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<PublishedNote>(VaultRefusal.Error));
}

/// <summary>A documentation provider for a tenant whose stored credential the vault refused: every call answers <see cref="VaultRefusal.Error"/>.</summary>
public sealed class RefusedDocProvider(string name) : IDocProvider
{
    public string Name => name;

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<bool>(VaultRefusal.Error));

    public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<IReadOnlyList<CompanyRef>>(VaultRefusal.Error));

    public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Failure<PublishedArticle>(VaultRefusal.Error));
}
