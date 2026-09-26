using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Settings;

/// <param name="Standing">One sentence: activated with whom, how long since the backend answered, or that access was revoked.</param>
public sealed record DeviceStanding(bool Activated, string? TenantName, int DaysOffline, bool BeyondGrace, bool Revoked, string Standing);

/// <param name="TenantName">Set when activation succeeded.</param>
/// <param name="Refusal">The backend's own sentence when it did not.</param>
public sealed record ActivationAnswer(string? TenantName, string? Refusal);

/// <summary>What the activation card asks the service (ST-010). Over the pipe in the running application; a fake in tests.</summary>
public interface IDeviceGateway
{
    /// <summary>Null when the service did not answer.</summary>
    Task<DeviceStanding?> StandingAsync(CancellationToken ct = default);

    Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default);
}

/// <summary>
/// The activation card's rules (ST-010, Spec §5 S8 step 2, in Settings until the onboarding wizard
/// exists). The code is checked for shape before anything is sent, the machine's name goes with it,
/// the tenant's name comes back, and a refusal is shown in the backend's words with the code left in
/// place to correct. The refresh token never reaches this side of the pipe.
/// </summary>
public sealed class ActivationPanel(IDeviceGateway gateway, string deviceName)
{
    public const string CodeLength = "A code is ten letters and digits, like KX7PM-4R2WQ.";

    private readonly IDeviceGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly string _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "this device" : deviceName;
    private string? _refusal;
    private bool _busy;

    public bool Activated { get; private set; }

    public string? TenantName { get; private set; }

    public string Standing { get; private set; } = "Asking the capture service…";

    public bool Revoked { get; private set; }

    public string Code
    {
        get;
        set
        {
            field = value ?? string.Empty;
            _refusal = null;
        }
    } = string.Empty;

    /// <summary>Why Activate is disabled, or the backend's refusal after a try; null when the code can be sent.</summary>
    public string? Problem => _refusal ?? (Activated ? null : Validate());

    public bool CanActivate => !_busy && !Activated && Problem is null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var standing = await _gateway.StandingAsync(ct).ConfigureAwait(false);
        if (standing is null)
        {
            Activated = false;
            Standing = "The capture service did not answer, so this device's standing is unknown.";
            return;
        }

        Apply(standing);
    }

    /// <summary>Sends the code. True when the device is now activated; false leaves the code and puts the reason in <see cref="Problem"/>.</summary>
    public async Task<bool> ActivateAsync(CancellationToken ct = default)
    {
        if (!CanActivate)
        {
            return false;
        }

        _busy = true;
        try
        {
            var answer = await _gateway.ActivateAsync(Code.Trim(), _deviceName, ct).ConfigureAwait(false);
            if (answer.Refusal is { } refusal)
            {
                _refusal = refusal;
                return false;
            }

            Code = string.Empty;
            var standing = await _gateway.StandingAsync(ct).ConfigureAwait(false);
            Apply(standing ?? new DeviceStanding(true, answer.TenantName, 0, false, false, $"Activated with {answer.TenantName}."));
            return true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void Apply(DeviceStanding standing)
    {
        Activated = standing.Activated;
        TenantName = standing.TenantName;
        Revoked = standing.Revoked;
        Standing = standing.Standing;
    }

    private string? Validate()
    {
        var letters = Code.Count(char.IsLetterOrDigit);
        if (letters == 0)
        {
            return "Enter the code from your invite.";
        }

        return letters == 10 ? null : CodeLength;
    }
}

/// <summary>The activation card's gateway, over the pipe (ST-010).</summary>
public sealed class PipeDevice(CaptureConnection connection) : IDeviceGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<DeviceStanding?> StandingAsync(CancellationToken ct = default)
    {
        var reported = await _connection.RequestAsync<DeviceReported>(id => new GetDeviceCommand { RequestId = id }, ct).ConfigureAwait(false);
        return reported is null ? null : new DeviceStanding(reported.Activated, reported.TenantName, reported.DaysOffline, reported.BeyondGrace, reported.Revoked, reported.Standing);
    }

    public async Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default)
    {
        var activated = await _connection.RequestAsync<DeviceActivated>(id => new ActivateDeviceCommand { RequestId = id, Code = code, DeviceName = deviceName }, ct).ConfigureAwait(false);
        if (activated is not null)
        {
            return new ActivationAnswer(activated.TenantName, null);
        }

        // Refused: the plain send returns the failed result with the backend's words. The backend
        // refuses a used code the second time, so the retry cannot activate twice.
        var result = await _connection.SendAsync(id => new ActivateDeviceCommand { RequestId = id, Code = code, DeviceName = deviceName }, ct).ConfigureAwait(false);
        return new ActivationAnswer(null, result.Error ?? "The capture service could not activate this device.");
    }
}
