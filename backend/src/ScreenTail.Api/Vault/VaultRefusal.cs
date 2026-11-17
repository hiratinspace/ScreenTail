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
    public const string Todo = "Ask your ScreenTail owner to connect it again in the web admin.";

    /// <summary>How a technician's sentence asks the owner, as the backend says it now and as rows stored before 2026-11-16 said it.</summary>
    private static readonly string[] AskTheOwner = ["Ask your ScreenTail owner to ", "Ask your owner to "];

    /// <summary>Where a technician's sentence sends the owner.</summary>
    private const string InTheWebAdmin = " in the web admin";

    /// <summary>Where a check stored before 2026-11-15 sent the technician to fix the credential.</summary>
    private const string InSettings = " in Settings → Integrations";

    /// <summary>
    /// Where the owner reads these sentences: on the provider's card on the web admin's Integrations
    /// page, whose Replace button takes a new site and key.
    /// </summary>
    private const string OnTheCard = " with Replace on this card";

    public static ProviderError Error { get; } = new(ProviderErrorKind.Unauthenticated, What, Todo);

    /// <summary>
    /// A sentence written for a technician, as the owner should read it on the provider's card: "Ask your
    /// ScreenTail owner to check the site address in the web admin." becomes "Check the site address with
    /// Replace on this card." A row stored before credentials became the owner's ("Update it in Settings →
    /// Integrations.") is pointed at the card too. Anything else is returned as it is.
    /// </summary>
    public static string? ForOwner(string? message)
    {
        if (message is null)
        {
            return null;
        }

        // Only the credential check's words are stored and shown to the owner, so a pointer to Settings
        // in a stored row is about the credential, never about a company mapping.
        message = message.Replace(InSettings, OnTheCard, StringComparison.Ordinal);
        foreach (var ask in AskTheOwner)
        {
            var at = message.IndexOf(ask, StringComparison.Ordinal);
            if (at >= 0 && message.Length > at + ask.Length)
            {
                var rest = message[(at + ask.Length)..];
                var where = rest.IndexOf(InTheWebAdmin, StringComparison.Ordinal);
                if (where >= 0)
                {
                    rest = string.Concat(rest.AsSpan(0, where), OnTheCard, rest.AsSpan(where + InTheWebAdmin.Length));
                }

                return string.Concat(message.AsSpan(0, at), char.ToUpper(rest[0], CultureInfo.InvariantCulture).ToString(), rest.AsSpan(1));
            }
        }

        return message;
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
