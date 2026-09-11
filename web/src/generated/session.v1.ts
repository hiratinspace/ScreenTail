/* Generated from shared/schema/session.v1.json by shared/schema/codegen/generate.mjs. Do not edit by hand: change the schema and run `npm run codegen` in shared/schema. */

/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "RemoteToolKind".
 */
export type RemoteToolKind = 'screenconnect' | 'rdp' | 'splashtop' | 'teamviewer' | 'anydesk' | 'browser' | 'other';
/**
 * One timeline event, discriminated by "type".
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "SessionEvent".
 */
export type SessionEvent =
  | ClickEvent
  | TypingBurstEvent
  | ShortcutEvent
  | EnterEvent
  | FocusEvent
  | CaptureStateEvent
  | MarkerEvent
  | NarrationEvent;
/**
 * Monotonic milliseconds since the session started.
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "TsMs".
 */
export type TsMs = number;
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "MouseButton".
 */
export type MouseButton = 'left' | 'right' | 'middle';
/**
 * Whether a window is captured (INV-5).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "CaptureScope".
 */
export type CaptureScope = 'remote_tool' | 'admin_tool' | 'out_of_scope' | 'excluded';
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "CaptureState".
 */
export type CaptureState = 'recording' | 'paused' | 'suppressed' | 'finalizing';
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "CaptureStateReason".
 */
export type CaptureStateReason =
  'user' | 'password_field' | 'excluded_app' | 'elevated_window' | 'sensitive_context' | 'out_of_scope';
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "FrameTrigger".
 */
export type FrameTrigger = 'click' | 'scene_change' | 'marker';
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "MaskKind".
 */
export type MaskKind =
  'ssn' | 'card' | 'api_key' | 'password' | 'email' | 'custom_pattern' | 'sensitive_context' | 'user_blur';
/**
 * Only the technician's microphone is captured in v1 (INV-9). end_user is reserved for the v1.2 consent workflow (ST-123).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "Speaker".
 */
export type Speaker = 'tech' | 'end_user';
/**
 * low: inferred from the screen only; Review shows the ⚠ marker (Spec S3).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "StepConfidence".
 */
export type StepConfidence = 'high' | 'low';
/**
 * local: drafted on the device; Review shows the Local draft banner.
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "DraftSource".
 */
export type DraftSource = 'cloud' | 'local';

/**
 * One captured remote-support session: timeline events, screenshots, transcript and, once drafted, the note. Used by the client store export, the bundle builder, fixtures and Review. Ordering uses ts_ms: monotonic milliseconds since the session started. Optional values are omitted rather than written as null.
 */
export interface Session {
  schema_version: 'session.v1';
  session_id: string;
  /**
   * Wall-clock start with offset, for display only.
   */
  started_at: string;
  /**
   * Active duration; absent while recording.
   */
  duration_ms?: number;
  remote_tool: RemoteTool;
  /**
   * Capture started late or was interrupted (Spec S3 banner).
   */
  partial_capture: boolean;
  /**
   * Frames deleted at finalize because redaction didn't finish in time (INV-1).
   */
  frames_purged_unredacted: number;
  /**
   * Local-only mode was on for this session (INV-8).
   */
  local_only: boolean;
  /**
   * Tenant policy version in force (ST-047).
   */
  policy_version?: string;
  events: SessionEvent[];
  frames: Frame[];
  transcript: TranscriptSegment[];
  draft?: DraftNote;
}
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "RemoteTool".
 */
export interface RemoteTool {
  kind: RemoteToolKind;
  /**
   * Remote-tool client version (ST-023).
   */
  client_version?: string;
}
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "ClickEvent".
 */
export interface ClickEvent {
  type: 'click';
  ts_ms: TsMs;
  /**
   * Physical screen pixels; negative on monitors left of or above the primary.
   */
  x: number;
  y: number;
  button: MouseButton;
  /**
   * Screenshot taken for this click; absent when debounced or out of scope.
   */
  frame_id?: string;
}
/**
 * A run of typing. Counts only: no field can hold a key or character (INV-2).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "TypingBurstEvent".
 */
export interface TypingBurstEvent {
  type: 'typing_burst';
  ts_ms: TsMs;
  char_count: number;
}
/**
 * A modifier chord such as Ctrl+C. Which keys is never recorded (INV-2).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "ShortcutEvent".
 */
export interface ShortcutEvent {
  type: 'shortcut';
  ts_ms: TsMs;
}
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "EnterEvent".
 */
export interface EnterEvent {
  type: 'enter';
  ts_ms: TsMs;
}
/**
 * Foreground window changed (ST-022). Window titles are content and not stored here.
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "FocusEvent".
 */
export interface FocusEvent {
  type: 'focus';
  ts_ms: TsMs;
  process: string;
  scope: CaptureScope;
}
/**
 * Capture state changed (ST-020). Suppressed and paused intervals keep no frames or typing (INV-6).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "CaptureStateEvent".
 */
export interface CaptureStateEvent {
  type: 'capture_state';
  ts_ms: TsMs;
  state: CaptureState;
  reason?: CaptureStateReason;
}
/**
 * The technician marked a moment (Ctrl+Alt+M, ST-029).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "MarkerEvent".
 */
export interface MarkerEvent {
  type: 'marker';
  ts_ms: TsMs;
  frame_id?: string;
}
/**
 * Speech with no screenshot within 8 seconds, kept as standalone narration (ST-028).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "NarrationEvent".
 */
export interface NarrationEvent {
  type: 'narration';
  ts_ms: TsMs;
  segment_id: string;
}
/**
 * A screenshot. It is stored with redaction_pending true, and only the redaction worker clears it; nothing reads a pending frame (INV-1). A pending frame has no OCR text and no redaction time; a redacted one always has a redaction time.
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "Frame".
 */
export interface Frame {
  id: string;
  ts_ms: TsMs;
  trigger: FrameTrigger;
  /**
   * Image path relative to the session bundle, e.g. frames/f-0002.jpg.
   */
  image: string;
  width: number;
  height: number;
  cursor?: Point;
  redaction_pending: boolean;
  redacted_at?: string;
  masked_regions: MaskedRegion[];
  /**
   * Login-screen heuristic fired; the frame is never sent or attached (ST-041, ST-060).
   */
  sensitive_context: boolean;
  /**
   * Redacted OCR text, e.g. with [CARD]. Only present once redacted.
   */
  ocr_text?: string;
  /**
   * Excluded in Review; never attached on publish (ST-075).
   */
  excluded_by_user: boolean;
}
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "Point".
 */
export interface Point {
  x: number;
  y: number;
}
/**
 * A masked rectangle in stored-image pixels.
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "MaskedRegion".
 */
export interface MaskedRegion {
  x: number;
  y: number;
  width: number;
  height: number;
  kind: MaskKind;
}
/**
 * Transcribed speech, already scrubbed (e.g. [REDACTED]).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "TranscriptSegment".
 */
export interface TranscriptSegment {
  id: string;
  ts_ms: TsMs;
  end_ms: TsMs;
  speaker: Speaker;
  text: string;
  /**
   * Nearest preceding screenshot within 8 seconds (ST-028).
   */
  frame_id?: string;
  confidence?: number;
}
/**
 * The drafted ticket note (ST-061), as shown and edited in Review (Spec S3).
 *
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "DraftNote".
 */
export interface DraftNote {
  problem: string;
  steps: DraftStep[];
  result: string;
  follow_ups: string[];
  suggested_title: string;
  suggested_time_minutes: number;
  kb_candidate: boolean;
  /**
   * Shown next to the KB toggle, e.g. "Not a KB candidate: one-off fix".
   */
  kb_reason: string;
  source: DraftSource;
  prompt_version: string;
}
/**
 * This interface was referenced by `Session`'s JSON-Schema
 * via the `definition` "DraftStep".
 */
export interface DraftStep {
  text: string;
  confidence: StepConfidence;
  frame_refs: string[];
  transcript_refs?: string[];
}
