namespace ScreenTail.Api.Summarize;

/// <summary>Scaffold: not yet limiting anything.</summary>
public sealed class DraftingConcurrency
{
    public const int PerDevice = 2;

    public const int PerTenant = 10;

    public int Tracked => 0;

    public IDisposable? TryEnter(Guid tenantId, Guid deviceId, out string? refusal)
    {
        _ = tenantId;
        _ = deviceId;
        refusal = null;
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
