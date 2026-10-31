using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// The real ConnectWise provider, as the service builds it, with one thing added: it says when the note
/// has been answered. A test on a <see cref="HeldClock"/> waits for that before it lets the publish
/// deadline fire, so the note is answered first however long a busy machine took over it. The PSA's
/// stand-in cannot say so itself: it hands back its answer before the provider has read it, and a
/// deadline in between would still cut the note off.
/// </summary>
internal sealed class WatchedPsa
{
    private readonly TaskCompletionSource _noteAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Done once a note call has returned its answer to the publisher.</summary>
    public Task NoteAnswered => _noteAnswered.Task;

    /// <summary>Puts the watched factory in place of the one Program.cs registers.</summary>
    public void Install(IServiceCollection services)
    {
        services.TryAddScoped<ConnectWiseProviderFactory>();
        _ = services.Replace(ServiceDescriptor.Scoped<IPsaProviderFactory>(provider => new Factory(provider.GetRequiredService<ConnectWiseProviderFactory>(), this)));
    }

    private sealed class Factory(IPsaProviderFactory inner, WatchedPsa watch) : IPsaProviderFactory
    {
        public async Task<IPsaProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            await inner.ForTenantAsync(tenantId, ct).ConfigureAwait(false) is { } psa ? new Watched(psa, watch) : null;
    }

    private sealed class Watched(IPsaProvider psa, WatchedPsa watch) : IPsaProvider
    {
        public string Name => psa.Name;

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) => psa.CheckAsync(ct);

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default) => psa.SearchTicketsAsync(query, ct);

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) => psa.RecentTicketsAsync(ct);

        public async Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default)
        {
            var answer = await psa.AddNoteAsync(note, ct).ConfigureAwait(false);
            _ = watch._noteAnswered.TrySetResult();
            return answer;
        }

        public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default) => psa.AddTimeEntryAsync(entry, ct);
    }
}
