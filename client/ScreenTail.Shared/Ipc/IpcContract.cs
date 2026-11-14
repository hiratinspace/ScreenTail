namespace ScreenTail.Shared.Ipc;

/// <summary>
/// Version of the service-to-UI named-pipe contract (Guide §5). Bump it on any breaking message change;
/// ST-004 defines the messages.
///
/// 2: ST-072 adds <c>reason</c> and <c>scope_process</c> to the capture state, so the HUD can say why
/// capture stopped. Additive, but the framing rejects unknown members on purpose, so an older reader
/// would fail on a newer snapshot rather than ignore the field. The handshake compares versions and
/// refuses a mismatched pair, which is the behaviour that makes this safe to change at all.
///
/// 3: ST-081 adds <c>effective_local_only</c> and <c>effective_retention_days</c> to <c>settings</c>, so
/// Privacy shows a locked field as the admin set it rather than the technician's own value. Same reason
/// as 2: an older reader would fail on the new members.
///
/// 4: the <c>audit</c> reply carries where the export was written and its verdict instead of the whole
/// log as one string, which outgrew the frame limit within weeks (2026-10-12 review, E-2). The reply
/// changes shape, so an older reader would fail on it.
///
/// 5: ST-013 adds the <c>draft_pending</c> state (a session over, its draft on its way to the backend) and
/// <c>draft_pending</c> on the <c>session</c> reply, so Review says the note is being drafted rather than
/// that drafting failed. An older UI would show the new state as "unknown, may be recording".
///
/// ST-113's <c>get_update</c>, <c>install_update</c>, <c>set_update_channel</c> and the <c>update</c>
/// reply are new commands, and <c>update</c> is only ever sent in reply to <c>get_update</c>, so an older
/// UI never receives it: not a breaking change, and the version is not bumped for them (as with
/// <c>get_frame_thumbnail</c>).
///
/// 6: Review's Discard and History's bulk discard name their sessions. <c>request_confirmation</c> gains
/// <c>session_ids</c>, the token it issues is bound to exactly those ids, and <c>discard_sessions</c>
/// carries them back. A new field on a command both sides already exchange, which an older service would
/// refuse as an unknown member, so the version moves and the handshake goes on refusing a mismatched pair.
///
/// 7: ST-080 AC3, choosing the microphone. <c>capture_settings</c> lists <c>microphones[]</c> as rows with
/// an id, a name and the default marked, where it had names alone, and the capture settings document gains
/// <c>microphone_id</c>. A changed reply and a new member on a document both sides exchange, so an older
/// reader would fail on either. The level meter's <c>get_microphone_level</c>, <c>stop_microphone_level</c>
/// and <c>microphone_level</c> are new messages and would not have moved it on their own.
///
/// 8: ST-031, the <c>diagnostics</c> reply gains <c>name_hook_refusals</c>, how many times Windows refused
/// the foreground watcher's narrow title-change hook, so the panel can say so. A count only, absent when
/// the service cannot know it. A new member on a reply, which an older UI would fail on, so the version
/// moves and the handshake goes on refusing a mismatched pair.
///
/// 9: ST-093, <c>publish_session</c> gains <c>publish_again</c>, optional and false when absent. The service
/// refuses a session already marked published without it, so a lost answer or a restarted UI cannot add
/// the note to the ticket twice. A new field on a command both sides exchange, which an older service
/// would refuse as an unknown member, so the version moves.
///
/// 10: ST-093, a publish that may have landed is said outside Review. A <c>sessions</c> row gains
/// <c>publish_unconfirmed</c> and the state snapshot <c>publishes_unconfirmed</c>, so History and the
/// tray can say "not confirmed". New members on replies both sides exchange, which an older UI would
/// fail on, so the version moves. <c>mark_published</c> is a new command and would not have moved it
/// on its own.
///
/// 11: Spec v0.6.9 §5 S4, the inbox rail's rows say the company, the note's title and the ticket. A
/// <c>sessions</c> row gains <c>title</c>, <c>ticket</c> and <c>company</c>, each optional and left off
/// when unknown, so a row without them is the v10 row and is drawn as before. Additive, but the framing
/// rejects unknown members, so a v10 UI would fail on a v11 row: the version moves and the handshake turns
/// a mismatched pair away instead of leaving the list unreadable.
/// </summary>
public static class IpcContract
{
    public const int Version = 11;
}
