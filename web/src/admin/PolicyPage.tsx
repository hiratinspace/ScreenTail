import { useEffect, useState, type FormEvent } from 'react';
import type { AdminApi, Policy, PolicyChange } from '../api';
import { messageOf } from './format';

interface Props {
  api: AdminApi;
}

/**
 * The policy editor (Spec §5 S9): the fields the tenant policy has today. The client enforces every one
 * of them (INV-11); capturing every window is opt-in and confirmed (INV-5).
 */
export function PolicyPage({ api }: Props) {
  const [saved, setSaved] = useState<Policy>();
  const [draft, setDraft] = useState<PolicyChange>();
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    api
      .policy()
      .then((policy) => {
        if (live) {
          setSaved(policy);
          setDraft(changeFrom(policy));
        }
      })
      .catch((e: unknown) => live && setError(messageOf(e)));
    return () => {
      live = false;
    };
  }, [api]);

  if (!draft || !saved) {
    return error ? (
      <div className="banner danger" role="alert">
        <p>{error}</p>
      </div>
    ) : (
      <p role="status">Loading the policy</p>
    );
  }

  const turningOnAllWindows = draft.captureAllWindows && !saved.captureAllWindows;
  const retentionValid = Number.isInteger(draft.retentionDays) && draft.retentionDays >= 1 && draft.retentionDays <= 365;
  const hoursValid = Number.isInteger(draft.updateCheckHours) && draft.updateCheckHours >= 0 && draft.updateCheckHours <= 168;

  function set<K extends keyof PolicyChange>(key: K, value: PolicyChange[K]) {
    setDraft((d) => (d ? { ...d, [key]: value } : d));
    setStatus('');
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!draft || !retentionValid || !hoursValid || (turningOnAllWindows && !draft.confirmAllWindows)) {
      setError('Fix the fields marked below, then save again.');
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      const policy = await api.setPolicy(draft);
      setSaved(policy);
      setDraft(changeFrom(policy));
      setStatus(`Policy saved as version ${policy.version}. Devices pick it up within the hour.`);
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      <p className="secondary">
        Current version <code>{saved.version === 'default' ? 'default (never set)' : saved.version}</code>. Locked
        settings show on each PC as "Set by your admin" and cannot be changed there.
      </p>

      <fieldset className="card">
        <legend className="section-title">Retention</legend>
        <div className="field">
          <label htmlFor="retention">Keep raw session data for (days)</label>
          <input
            id="retention"
            type="number"
            inputMode="numeric"
            min={1}
            max={365}
            step={1}
            className="short"
            value={Number.isNaN(draft.retentionDays) ? '' : draft.retentionDays}
            aria-invalid={!retentionValid}
            aria-describedby="retention-hint"
            onChange={(e) => set('retentionDays', e.target.valueAsNumber)}
          />
          <p id="retention-hint" className={retentionValid ? 'hint' : 'field-error'}>
            {retentionValid
              ? 'Screenshots, transcript and events are deleted from each PC after this many days. 1 to 365.'
              : 'Enter a whole number of days from 1 to 365.'}
          </p>
        </div>
      </fieldset>

      <fieldset className="card">
        <legend className="section-title">Local-only</legend>
        <Check
          id="local-only"
          label="Local-only: drafts are made on the PC and nothing is sent to ScreenTail's servers"
          hint="Publishing to your PSA and documentation still works when a technician clicks Publish."
          checked={draft.localOnly}
          onChange={(v) => set('localOnly', v)}
        />
        <Check
          id="local-only-locked"
          label="Lock this setting so technicians cannot change it"
          checked={draft.localOnlyLocked}
          onChange={(v) => set('localOnlyLocked', v)}
        />
      </fieldset>

      <fieldset className="card">
        <legend className="section-title">Capture scope</legend>
        <div className="radio">
          <input
            id="scope-remote"
            type="radio"
            name="scope"
            checked={!draft.captureAllWindows}
            onChange={() => {
              set('captureAllWindows', false);
              set('confirmAllWindows', false);
            }}
          />
          <label htmlFor="scope-remote">Remote-support windows and the admin-tool allowlist (recommended)</label>
        </div>
        <div className="radio">
          <input
            id="scope-all"
            type="radio"
            name="scope"
            checked={draft.captureAllWindows}
            onChange={() => set('captureAllWindows', true)}
          />
          <label htmlFor="scope-all">Every window during a session</label>
        </div>
        {turningOnAllWindows && (
          <div className="banner warning">
            <Check
              id="confirm-all-windows"
              label="I understand that every window on a technician's screen can be captured during a session"
              checked={draft.confirmAllWindows}
              invalid={!draft.confirmAllWindows}
              hint="Password fields, excluded apps and elevated windows are still never captured."
              onChange={(v) => set('confirmAllWindows', v)}
            />
          </div>
        )}
      </fieldset>

      <fieldset className="card">
        <legend className="section-title">Updates</legend>
        <div className="field">
          <label htmlFor="channel">Update channel</label>
          <select
            id="channel"
            className="short"
            value={draft.updateChannel}
            onChange={(e) => set('updateChannel', e.target.value === 'pilot' ? 'pilot' : 'stable')}
          >
            <option value="stable">Stable</option>
            <option value="pilot">Pilot (early builds)</option>
          </select>
        </div>
        <Check
          id="channel-locked"
          label="Lock the channel so technicians cannot change it"
          checked={draft.updateChannelLocked}
          onChange={(v) => set('updateChannelLocked', v)}
        />
        <div className="field">
          <label htmlFor="update-hours">Check for updates every (hours)</label>
          <input
            id="update-hours"
            type="number"
            inputMode="numeric"
            min={0}
            max={168}
            step={1}
            className="short"
            value={Number.isNaN(draft.updateCheckHours) ? '' : draft.updateCheckHours}
            aria-invalid={!hoursValid}
            aria-describedby="update-hours-hint"
            onChange={(e) => set('updateCheckHours', e.target.valueAsNumber)}
          />
          <p id="update-hours-hint" className={hoursValid ? 'hint' : 'field-error'}>
            {hoursValid ? '0 turns update checks off. Up to 168 (a week).' : 'Enter a whole number of hours from 0 to 168.'}
          </p>
        </div>
        <Check
          id="updates-local-only"
          label="Allow update checks while local-only is on and locked here"
          hint="A local-only a technician turned on themselves still lets no update check out."
          checked={draft.updateChecksUnderLocalOnly}
          onChange={(v) => set('updateChecksUnderLocalOnly', v)}
        />
      </fieldset>

      <p className="hint">
        Exclusion defaults, redaction patterns and telemetry are not policy fields yet. They are set on each PC for now.
      </p>

      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      <div className="actions">
        <button type="submit" className="button primary" disabled={busy}>
          {busy ? 'Saving policy' : 'Save policy'}
        </button>
        <p role="status" className="secondary">
          {status}
        </p>
      </div>
    </form>
  );
}

function changeFrom(policy: Policy): PolicyChange {
  return {
    retentionDays: policy.retentionDays,
    localOnly: policy.localOnly,
    localOnlyLocked: policy.localOnlyLocked,
    captureAllWindows: policy.captureAllWindows,
    updateChannel: policy.updateChannel,
    updateChannelLocked: policy.updateChannelLocked,
    updateCheckHours: policy.updateCheckHours,
    updateChecksUnderLocalOnly: policy.updateChecksUnderLocalOnly,
    confirmAllWindows: false,
  };
}

function Check({
  id,
  label,
  hint,
  checked,
  invalid,
  onChange,
}: {
  id: string;
  label: string;
  hint?: string;
  checked: boolean;
  invalid?: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <div className="check">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        aria-invalid={invalid || undefined}
        aria-describedby={hint ? `${id}-hint` : undefined}
        onChange={(e) => onChange(e.target.checked)}
      />
      <div>
        <label htmlFor={id}>{label}</label>
        {hint && (
          <p id={`${id}-hint`} className="hint">
            {hint}
          </p>
        )}
      </div>
    </div>
  );
}
