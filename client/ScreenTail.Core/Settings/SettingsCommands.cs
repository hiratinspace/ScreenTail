using ScreenTail.Core.Audit;
using ScreenTail.Core.Net;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>
/// The service's side of Settings → Privacy (ST-081): the technician's settings on disk, applied in place
/// when they change, reported with which fields the admin's policy locked; and the audit log exported
/// with its verification. Same two entry points as the other command classes.
/// </summary>
public sealed class SettingsCommands(
    ClientSettingsStore store,
    Func<TenantPolicy> policy,
    Action<ClientSettings> apply,
    Func<CancellationToken, Task<(IReadOnlyList<AuditRecord> Records, AuditVerification Verification)>> audit)
{
    private readonly ClientSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<TenantPolicy> _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly Action<ClientSettings> _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    private readonly Func<CancellationToken, Task<(IReadOnlyList<AuditRecord> Records, AuditVerification Verification)>> _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case GetSettingsCommand:
                {
                    var tenant = _policy();
                    var applied = PolicyApplication.Resolve(tenant, _store.Load());
                    return new SettingsReported
                    {
                        RequestId = command.RequestId,
                        Settings = _store.Load(),
                        LocalOnlyLocked = applied.Enforced,
                        RetentionLocked = applied.RetentionEnforced,
                        PolicyVersion = tenant.Version,
                    };
                }

            case ExportAuditCommand:
                {
                    var (records, verification) = await _audit(ct).ConfigureAwait(false);
                    return new AuditExported { RequestId = command.RequestId, Json = AuditExport.ToJson(records, verification) };
                }

            default:
                return null;
        }
    }

    public Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case SetSettingsCommand set:
                {
                    var problems = set.Settings.Problems();
                    if (problems.Count > 0)
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = problems[0] });
                    }

                    if (!_store.Save(set.Settings))
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The settings could not be written to disk." });
                    }

                    _apply(set.Settings);
                    return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = true });
                }

            case GetSettingsCommand or ExportAuditCommand:
                return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The settings could not be read." });

            default:
                return Task.FromResult<CommandResult?>(null);
        }
    }
}
