"""Regenerate the hand-crafted fixture bundles and the redaction seed (ST-006).

Everything under ``handcrafted/`` and ``redaction-seed/`` is produced here and committed, so the
Review UI, the prompt work and the redaction evaluation all run against the same bytes. Rendering is
deterministic: same source, same PNGs.

    python -m pip install -r tools/requirements.txt
    python tools/render_fixtures.py

These frames are drawn, not captured. They carry the chrome, layout and secret shapes the real thing
has, which is what the UI and the pattern engine need, but they are too clean to judge OCR quality on
— that is what the staged captures in ST-030 are for.
"""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
WIDTH, HEIGHT = 1600, 900

# Windows-ish flat palette. Not the ScreenTail token set: this is the captured desktop, not our UI.
DESKTOP = (32, 38, 46)
WINDOW = (243, 243, 243)
CHROME = (225, 225, 225)
TITLE = (250, 250, 250)
BORDER = (190, 190, 190)
TEXT = (28, 28, 28)
MUTED = (95, 95, 95)
ACCENT = (0, 103, 192)
DANGER = (196, 43, 28)
MASK = (10, 10, 10)
TOOLBAR = {"screenconnect": (24, 61, 99), "rdp": (0, 60, 120)}

FONTS = Path("/System/Library/Fonts/Supplemental")


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    name = "Arial Bold.ttf" if bold else "Arial.ttf"
    path = FONTS / name
    if path.exists():
        return ImageFont.truetype(str(path), size)
    return ImageFont.load_default(size)


@dataclass
class Secret:
    """A seeded secret: what is drawn, and the ground truth the redaction evaluation scores against."""

    kind: str
    text: str
    label: str = ""
    box: tuple[int, int, int, int] | None = None  # filled in once drawn


@dataclass
class Screen:
    """One frame: a window with a title bar, some lines, and optionally a dialog or a field."""

    frame_id: str
    ts_ms: int
    trigger: str
    tool: str
    title: str
    lines: list[str] = field(default_factory=list)
    highlight: int | None = None
    columns: tuple[str, ...] | None = ("Name", "Status", "Startup Type")
    dialog: tuple[str, list[str]] | None = None
    dialog_title: str = "Application Error"
    field_label: str | None = None
    field_value: str | None = None
    field_is_password: bool = False
    secrets: list[Secret] = field(default_factory=list)
    masked: bool = False  # draw the secrets already painted over (a post-redaction frame)
    ocr_text: str = ""


def draw_screen(screen: Screen) -> tuple[Image.Image, list[Secret]]:
    image = Image.new("RGB", (WIDTH, HEIGHT), DESKTOP)
    d = ImageDraw.Draw(image)

    # Remote-tool chrome: the band our scope detection keys on.
    bar = TOOLBAR.get(screen.tool)
    if bar:
        d.rectangle([0, 0, WIDTH, 34], fill=bar)
        label = (
            "ScreenConnect — Acme Dental / RECEPTION-02   Session 48213"
            if screen.tool == "screenconnect"
            else "RECEPTION-02 — Remote Desktop Connection"
        )
        d.text((16, 9), label, font=font(15), fill=(235, 235, 235))
        d.text((WIDTH - 150, 9), "— □ ✕", font=font(15), fill=(215, 215, 215))

    # The application window inside the remote session.
    top = 70
    d.rectangle([60, top, WIDTH - 60, HEIGHT - 70], fill=WINDOW, outline=BORDER, width=1)
    d.rectangle([60, top, WIDTH - 60, top + 38], fill=CHROME, outline=BORDER, width=1)
    d.text((78, top + 11), screen.title, font=font(16, bold=True), fill=TEXT)
    d.text((WIDTH - 140, top + 11), "—  □  ✕", font=font(15), fill=MUTED)

    # Column headings, then rows. A form or a prompt has no grid, so it passes columns=None.
    y = top + 64
    if screen.columns:
        for heading, x in zip(screen.columns, (84, 520, 680, 980), strict=False):
            d.text((x, y), heading, font=font(14, bold=True), fill=MUTED)
        d.line([84, y + 22, WIDTH - 84, y + 22], fill=BORDER)
        y += 36

    placed: list[Secret] = []
    for index, line in enumerate(screen.lines):
        if index == screen.highlight:
            d.rectangle([78, y - 4, WIDTH - 84, y + 24], fill=(215, 232, 250))
        columns = line.split("|")
        for column, x in zip(columns, (84, 520, 680, 980), strict=False):
            text = column.strip()
            secret = next((s for s in screen.secrets if s.text == text), None)
            box = (x, y, x + int(d.textlength(text, font=font(15))) + 6, y + 22)
            if secret is not None:
                secret.box = box
                placed.append(secret)
                if screen.masked:
                    d.rectangle(box, fill=MASK)
                else:
                    d.text((x, y), text, font=font(15), fill=TEXT)
            else:
                d.text((x, y), text, font=font(15), fill=TEXT)
        y += 30

    # A labelled field (login prompts, billing forms).
    if screen.field_label:
        fy = y + 30
        d.text((84, fy), screen.field_label, font=font(15), fill=TEXT)
        d.rectangle([84, fy + 26, 520, fy + 58], fill=(255, 255, 255), outline=(120, 120, 120))
        value = screen.field_value or ""
        shown = "●" * len(value) if screen.field_is_password else value
        secret = next((s for s in screen.secrets if s.text == value), None)
        box = (90, fy + 33, 90 + int(d.textlength(shown, font=font(15))) + 6, fy + 55)
        if secret is not None:
            secret.box = box
            placed.append(secret)
        if screen.masked and secret is not None:
            d.rectangle(box, fill=MASK)
        else:
            d.text((90, fy + 33), shown, font=font(15), fill=TEXT)
        d.rectangle([84, fy + 76, 200, fy + 110], fill=ACCENT)
        d.text((120, fy + 85), "Sign in", font=font(15, bold=True), fill=(255, 255, 255))

    # A modal dialog on top of everything.
    if screen.dialog:
        heading, body = screen.dialog
        dx, dy, dw, dh = 520, 320, 560, 220
        d.rectangle([dx + 6, dy + 6, dx + dw + 6, dy + dh + 6], fill=(20, 24, 30))
        d.rectangle([dx, dy, dx + dw, dy + dh], fill=WINDOW, outline=(120, 120, 120))
        d.rectangle([dx, dy, dx + dw, dy + 36], fill=CHROME)
        d.text((dx + 14, dy + 10), screen.dialog_title, font=font(15, bold=True), fill=TEXT)
        icon = DANGER if screen.dialog_title == "Application Error" else ACCENT
        d.ellipse([dx + 22, dy + 60, dx + 58, dy + 96], fill=icon)
        d.text(
            (dx + 36, dy + 68), "!" if icon == DANGER else "i", font=font(22, bold=True), fill=(255, 255, 255)
        )
        d.text((dx + 76, dy + 62), heading, font=font(16, bold=True), fill=TEXT)
        for i, body_line in enumerate(body):
            d.text((dx + 76, dy + 92 + i * 22), body_line, font=font(14), fill=TEXT)
        d.rectangle([dx + dw - 130, dy + dh - 52, dx + dw - 30, dy + dh - 18], fill=ACCENT)
        d.text((dx + dw - 108, dy + dh - 44), "Close", font=font(14, bold=True), fill=(255, 255, 255))

    return image, placed


def masked_regions(secrets: list[Secret]) -> list[dict[str, object]]:
    regions = []
    for secret in secrets:
        assert secret.box is not None
        x0, y0, x1, y1 = secret.box
        regions.append({"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0, "kind": secret.kind})
    return regions


# --------------------------------------------------------------------------------------------------
# The five bundles
# --------------------------------------------------------------------------------------------------

SERVICES = [
    "Print Spooler | Stopped | Automatic",
    "Remote Desktop Services | Running | Automatic",
    "Windows Update | Running | Manual",
    "Workstation | Running | Automatic",
]


def spooler_bundle() -> dict[str, object]:
    screens = [
        Screen(
            "f-0001",
            4_200,
            "click",
            "screenconnect",
            "Services",
            SERVICES,
            highlight=0,
            ocr_text="Name Status Startup Type Print Spooler Stopped Automatic",
        ),
        Screen(
            "f-0002",
            61_800,
            "click",
            "screenconnect",
            "Services",
            ["Print Spooler | Running | Automatic"] + SERVICES[1:],
            highlight=0,
            ocr_text="Name Status Startup Type Print Spooler Running Automatic",
        ),
        Screen(
            "f-0003",
            128_400,
            "scene_change",
            "screenconnect",
            "Devices and Printers",
            ["HP LaserJet M428 | Ready | Default", "Microsoft Print to PDF | Ready |"],
            ocr_text="HP LaserJet M428 Ready Default",
        ),
    ]
    return {
        "slug": "spooler-stopped-screenconnect",
        "summary": "The everyday case: a stopped print spooler, restarted, confirmed. ScreenConnect chrome.",
        "screens": screens,
        "session": {
            "session_id": "fx-spooler-0001",
            "started_at": "2026-09-08T14:02:11-05:00",
            "duration_ms": 613_000,
            "remote_tool": {"kind": "screenconnect", "client_version": "24.1.7"},
            "partial_capture": False,
            "frames_purged_unredacted": 0,
            "local_only": False,
            "policy_version": "policy-v14",
        },
        "events": [
            {"type": "capture_state", "ts_ms": 0, "state": "recording"},
            {
                "type": "focus",
                "ts_ms": 1_100,
                "process": "ScreenConnect.WindowsClient.exe",
                "scope": "remote_tool",
            },
            {"type": "click", "ts_ms": 4_200, "x": 214, "y": 176, "button": "right", "frame_id": "f-0001"},
            {"type": "narration", "ts_ms": 9_500, "segment_id": "t-0001"},
            {"type": "typing_burst", "ts_ms": 41_000, "char_count": 12},
            {"type": "click", "ts_ms": 61_800, "x": 268, "y": 176, "button": "left", "frame_id": "f-0002"},
            {"type": "narration", "ts_ms": 66_300, "segment_id": "t-0002"},
            {"type": "marker", "ts_ms": 128_400, "frame_id": "f-0003"},
            {"type": "narration", "ts_ms": 131_000, "segment_id": "t-0003"},
        ],
        "transcript": [
            ("t-0001", 9_500, 13_900, "tech", "Spooler service is stopped, that's why nothing is printing."),
            ("t-0002", 66_300, 71_200, "tech", "Started it again and set it to restart on failure."),
            ("t-0003", 131_000, 136_400, "tech", "Test page came out fine, so the queue is clear now."),
        ],
        "draft": {
            "problem": "Nothing would print from the reception workstation; jobs sat in the queue.",
            "steps": [
                {
                    "text": "Found the Print Spooler service stopped.",
                    "confidence": "high",
                    "frame_refs": ["f-0001"],
                    "transcript_refs": ["t-0001"],
                },
                {
                    "text": "Started the service and set it to restart automatically on failure.",
                    "confidence": "high",
                    "frame_refs": ["f-0002"],
                    "transcript_refs": ["t-0002"],
                },
                {
                    "text": "Cleared the stuck queue and printed a test page to confirm.",
                    "confidence": "high",
                    "frame_refs": ["f-0003"],
                    "transcript_refs": ["t-0003"],
                },
            ],
            "result": "Printing works again from the reception workstation.",
            "follow_ups": ["If the spooler stops again, check the HP driver version on this machine."],
            "suggested_title": "Printer offline — print spooler stopped",
            "suggested_time_minutes": 15,
            "kb_candidate": True,
            "kb_reason": "Common fix, clean steps, likely to recur across this client's front-desk machines.",
            "source": "cloud",
            "prompt_version": "note_v1",
        },
    }


def outlook_bundle() -> dict[str, object]:
    password = Secret("password", "Autumn2026!", "credential typed into a sign-in prompt")
    screens = [
        Screen(
            "f-0011",
            7_400,
            "click",
            "rdp",
            "Mail — Outlook",
            ["Profile: Outlook | Needs password |", "Account: sarah.j | Disconnected |"],
            columns=("Item", "State", ""),
            highlight=1,
            ocr_text="Profile: Outlook Needs password Account: sarah.j Disconnected",
        ),
        Screen(
            "f-0012",
            52_100,
            "scene_change",
            "rdp",
            "Windows Security",
            ["Microsoft Exchange | | "],
            columns=None,
            field_label="Password",
            field_value=password.text,
            field_is_password=True,
            secrets=[password],
            masked=True,
            ocr_text="Microsoft Exchange Password [REDACTED] Sign in",
        ),
        Screen(
            "f-0013",
            121_900,
            "click",
            "rdp",
            "Mail — Outlook",
            ["Profile: Outlook | Connected |", "Inbox | 4 new |"],
            columns=("Item", "State", ""),
            ocr_text="Profile: Outlook Connected Inbox 4 new",
        ),
    ]
    return {
        "slug": "outlook-profile-rdp",
        "summary": (
            "RDP chrome and a login prompt: capture suppresses itself while the password field has focus."
        ),
        "screens": screens,
        "session": {
            "session_id": "fx-outlook-0002",
            "started_at": "2026-09-09T09:41:03-05:00",
            "duration_ms": 402_000,
            "remote_tool": {"kind": "rdp", "client_version": "10.0.22621"},
            "partial_capture": False,
            "frames_purged_unredacted": 0,
            "local_only": False,
            "policy_version": "policy-v14",
        },
        "events": [
            {"type": "capture_state", "ts_ms": 0, "state": "recording"},
            {"type": "focus", "ts_ms": 900, "process": "mstsc.exe", "scope": "remote_tool"},
            {"type": "click", "ts_ms": 7_400, "x": 640, "y": 232, "button": "left", "frame_id": "f-0011"},
            {"type": "narration", "ts_ms": 11_200, "segment_id": "t-0011"},
            {"type": "capture_state", "ts_ms": 44_000, "state": "suppressed", "reason": "password_field"},
            {"type": "capture_state", "ts_ms": 51_500, "state": "recording"},
            {"type": "click", "ts_ms": 52_100, "x": 300, "y": 560, "button": "left", "frame_id": "f-0012"},
            {"type": "typing_burst", "ts_ms": 58_000, "char_count": 11},
            {"type": "marker", "ts_ms": 121_900, "frame_id": "f-0013"},
            {"type": "narration", "ts_ms": 124_500, "segment_id": "t-0012"},
        ],
        "transcript": [
            ("t-0011", 11_200, 16_800, "tech", "Outlook keeps asking for the password after the migration."),
            (
                "t-0012",
                124_500,
                131_000,
                "tech",
                "Rebuilt the profile and it connected, the password is [REDACTED] but she'll change it.",
            ),
        ],
        "draft": {
            "problem": "Outlook prompted for a password repeatedly after the mailbox migration.",
            "steps": [
                {
                    "text": "Confirmed the profile was stuck disconnected and prompting.",
                    "confidence": "high",
                    "frame_refs": ["f-0011"],
                    "transcript_refs": ["t-0011"],
                },
                {
                    "text": "Signed in again at the Windows Security prompt.",
                    "confidence": "low",
                    "frame_refs": ["f-0012"],
                    "transcript_refs": [],
                },
                {
                    "text": "Rebuilt the Outlook profile and confirmed mail flowed.",
                    "confidence": "high",
                    "frame_refs": ["f-0013"],
                    "transcript_refs": ["t-0012"],
                },
            ],
            "result": "Outlook connects without prompting and new mail arrives.",
            "follow_ups": ["Ask the user to change the mailbox password at next sign-in."],
            "suggested_title": "Outlook repeatedly prompting for password after migration",
            "suggested_time_minutes": 30,
            "kb_candidate": False,
            "kb_reason": "Specific to this migration; the generic profile rebuild is already documented.",
            "source": "cloud",
            "prompt_version": "note_v1",
        },
    }


def crash_bundle() -> dict[str, object]:
    screens = [
        Screen(
            "f-0021",
            5_600,
            "click",
            "screenconnect",
            "DentalSuite",
            ["Patient records | Loading… |", "Scheduler | Ready |"],
            columns=("Module", "State", ""),
            ocr_text="Patient records Loading Scheduler Ready",
        ),
        Screen(
            "f-0022",
            38_900,
            "scene_change",
            "screenconnect",
            "DentalSuite",
            ["Patient records | Not responding |"],
            columns=("Module", "State", ""),
            dialog=(
                "DentalSuite has stopped working",
                [
                    "Error 0x80070005: access is denied.",
                    "A problem caused the program to stop working",
                    "correctly. Windows will close the program.",
                ],
            ),
            ocr_text="Application Error DentalSuite has stopped working Error 0x80070005: access is denied.",
        ),
        Screen(
            "f-0023",
            214_500,
            "click",
            "screenconnect",
            "Local Security Policy",
            ["DentalSuite service account | Log on as a service | Granted"],
            columns=("Account", "Right", "State"),
            highlight=0,
            ocr_text="DentalSuite service account Log on as a service Granted",
        ),
    ]
    return {
        "slug": "app-crash-dialog-screenconnect",
        "summary": "An error dialog carries the whole diagnosis; the note must quote the error code.",
        "screens": screens,
        "session": {
            "session_id": "fx-crash-0003",
            "started_at": "2026-09-09T15:20:44-05:00",
            "duration_ms": 704_000,
            "remote_tool": {"kind": "screenconnect", "client_version": "24.1.7"},
            "partial_capture": False,
            "frames_purged_unredacted": 0,
            "local_only": False,
            "policy_version": "policy-v14",
        },
        "events": [
            {"type": "capture_state", "ts_ms": 0, "state": "recording"},
            {
                "type": "focus",
                "ts_ms": 1_400,
                "process": "ScreenConnect.WindowsClient.exe",
                "scope": "remote_tool",
            },
            {"type": "click", "ts_ms": 5_600, "x": 410, "y": 300, "button": "left", "frame_id": "f-0021"},
            {"type": "narration", "ts_ms": 8_100, "segment_id": "t-0021"},
            {"type": "marker", "ts_ms": 38_900, "frame_id": "f-0022"},
            {"type": "narration", "ts_ms": 42_700, "segment_id": "t-0022"},
            {"type": "focus", "ts_ms": 180_000, "process": "mmc.exe", "scope": "admin_tool"},
            {"type": "click", "ts_ms": 214_500, "x": 522, "y": 388, "button": "left", "frame_id": "f-0023"},
            {"type": "narration", "ts_ms": 218_000, "segment_id": "t-0023"},
        ],
        "transcript": [
            ("t-0021", 8_100, 12_600, "tech", "She says it crashes every time she opens patient records."),
            ("t-0022", 42_700, 48_900, "tech", "There it is, access denied, eighty oh seven double oh five."),
            (
                "t-0023",
                218_000,
                225_400,
                "tech",
                "The service account had lost its log on as a service right, so I granted it back.",
            ),
        ],
        "draft": {
            "problem": "DentalSuite crashed with error 0x80070005 whenever patient records were opened.",
            "steps": [
                {
                    "text": "Reproduced the crash by opening patient records.",
                    "confidence": "high",
                    "frame_refs": ["f-0021", "f-0022"],
                    "transcript_refs": ["t-0021", "t-0022"],
                },
                {
                    "text": "Read the error as access denied (0x80070005) rather than a data fault.",
                    "confidence": "high",
                    "frame_refs": ["f-0022"],
                    "transcript_refs": ["t-0022"],
                },
                {
                    "text": (
                        "Restored the 'Log on as a service' right for the DentalSuite "
                        "service account in Local Security Policy."
                    ),
                    "confidence": "high",
                    "frame_refs": ["f-0023"],
                    "transcript_refs": ["t-0023"],
                },
            ],
            "result": (
                "Patient records open without crashing; the service starts under its own account again."
            ),
            "follow_ups": [
                "Check whether a GPO is stripping the right; it may return after the next policy refresh."
            ],
            "suggested_title": "DentalSuite crash 0x80070005 — service account lost logon right",
            "suggested_time_minutes": 45,
            "kb_candidate": True,
            "kb_reason": "Non-obvious diagnosis with a clean fix; likely to recur wherever that GPO applies.",
            "source": "cloud",
            "prompt_version": "note_v1",
        },
    }


def billing_bundle() -> dict[str, object]:
    card = Secret("card", "4111 1111 1111 1111", "card number visible in a billing portal")
    screens = [
        Screen(
            "f-0031",
            9_900,
            "click",
            "rdp",
            "Acme Dental — Billing",
            ["Invoice 48213 | Overdue | ", "Card on file | 4111 1111 1111 1111 | expires 04/29"],
            columns=("Item", "Detail", "Note"),
            highlight=1,
            secrets=[card],
            masked=True,
            ocr_text="Invoice 48213 Overdue Card on file [CARD] expires 04/29",
        ),
        Screen(
            "f-0032",
            96_400,
            "scene_change",
            "rdp",
            "Network Connections",
            ["Ethernet | Network cable unplugged |", "VPN — Acme | Disconnected |"],
            columns=("Adapter", "Status", ""),
            highlight=1,
            ocr_text="Ethernet Network cable unplugged VPN Acme Disconnected",
        ),
        Screen(
            "f-0033",
            188_200,
            "click",
            "rdp",
            "Network Connections",
            ["Ethernet | Connected |", "VPN — Acme | Connected |"],
            columns=("Adapter", "Status", ""),
            highlight=1,
            ocr_text="Ethernet Connected VPN Acme Connected",
        ),
    ]
    return {
        "slug": "vpn-billing-portal-rdp",
        "summary": (
            "A card number on screen, already masked in the stored frame — what a redacted bundle looks like."
        ),
        "screens": screens,
        "session": {
            "session_id": "fx-billing-0004",
            "started_at": "2026-09-10T11:05:39-05:00",
            "duration_ms": 366_000,
            "remote_tool": {"kind": "rdp", "client_version": "10.0.22621"},
            "partial_capture": False,
            "frames_purged_unredacted": 0,
            "local_only": True,
            "policy_version": "policy-v14",
        },
        "events": [
            {"type": "capture_state", "ts_ms": 0, "state": "recording"},
            {"type": "focus", "ts_ms": 800, "process": "mstsc.exe", "scope": "remote_tool"},
            {"type": "click", "ts_ms": 9_900, "x": 700, "y": 206, "button": "left", "frame_id": "f-0031"},
            {"type": "narration", "ts_ms": 14_100, "segment_id": "t-0031"},
            {"type": "capture_state", "ts_ms": 20_000, "state": "suppressed", "reason": "sensitive_context"},
            {"type": "capture_state", "ts_ms": 74_000, "state": "recording"},
            {"type": "click", "ts_ms": 96_400, "x": 388, "y": 250, "button": "left", "frame_id": "f-0032"},
            {"type": "narration", "ts_ms": 101_500, "segment_id": "t-0032"},
            {"type": "marker", "ts_ms": 188_200, "frame_id": "f-0033"},
            {"type": "narration", "ts_ms": 191_000, "segment_id": "t-0033"},
        ],
        "transcript": [
            (
                "t-0031",
                14_100,
                19_800,
                "tech",
                "Their billing page was open, I moved off it before doing anything.",
            ),
            (
                "t-0032",
                101_500,
                107_200,
                "tech",
                "The VPN adapter was down because the cable was unplugged at the wall.",
            ),
            ("t-0033", 191_000, 196_600, "tech", "Reconnected and the tunnel came straight back up."),
        ],
        "draft": {
            "problem": "The practice could not reach the server over VPN from the back-office PC.",
            "steps": [
                {
                    "text": (
                        "Found the Ethernet adapter reporting a cable unplugged and the VPN disconnected."
                    ),
                    "confidence": "high",
                    "frame_refs": ["f-0032"],
                    "transcript_refs": ["t-0032"],
                },
                {
                    "text": "Reseated the wall cable and reconnected the VPN.",
                    "confidence": "high",
                    "frame_refs": ["f-0033"],
                    "transcript_refs": ["t-0033"],
                },
            ],
            "result": "VPN reconnects and the server shares are reachable again.",
            "follow_ups": ["The wall port is loose; suggest a cable tidy on the next site visit."],
            "suggested_title": "VPN down — Ethernet cable unplugged at the wall",
            "suggested_time_minutes": 15,
            "kb_candidate": False,
            "kb_reason": "Physical, site-specific cause; nothing generalisable to document.",
            "source": "local",
            "prompt_version": "note_v1",
        },
    }


def interrupted_bundle() -> dict[str, object]:
    screens = [
        Screen(
            "f-0041",
            3_100,
            "click",
            "screenconnect",
            "Task Manager",
            ["Backup Agent | Not responding | 41%", "Antivirus | Running | 12%"],
            columns=("Process", "Status", "CPU"),
            highlight=0,
            ocr_text="Backup Agent Not responding 41% Antivirus Running 12%",
        ),
    ]
    return {
        "slug": "interrupted-partial-capture",
        "summary": (
            "A session the service lost mid-flight: partial, frames purged before redaction, no draft."
        ),
        "screens": screens,
        "session": {
            "session_id": "fx-interrupted-0005",
            "started_at": "2026-09-10T16:48:02-05:00",
            "duration_ms": 74_000,
            "remote_tool": {"kind": "screenconnect", "client_version": "24.1.7"},
            "partial_capture": True,
            "frames_purged_unredacted": 3,
            "local_only": False,
            "policy_version": "policy-v14",
        },
        "events": [
            {"type": "capture_state", "ts_ms": 0, "state": "recording"},
            {
                "type": "focus",
                "ts_ms": 1_000,
                "process": "ScreenConnect.WindowsClient.exe",
                "scope": "remote_tool",
            },
            {"type": "click", "ts_ms": 3_100, "x": 340, "y": 190, "button": "left", "frame_id": "f-0041"},
            {"type": "narration", "ts_ms": 6_400, "segment_id": "t-0041"},
            {"type": "capture_state", "ts_ms": 74_000, "state": "finalizing"},
        ],
        "transcript": [
            (
                "t-0041",
                6_400,
                11_100,
                "tech",
                "Backup agent is pinned again, I'll kill it and see if it restarts clean.",
            ),
        ],
        "draft": None,
    }


BUNDLES = [spooler_bundle, outlook_bundle, crash_bundle, billing_bundle, interrupted_bundle]


def write_bundle(spec: dict[str, object]) -> dict[str, object]:
    slug = spec["slug"]
    directory = ROOT / "handcrafted" / slug
    frames_dir = directory / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)

    frames = []
    for screen in spec["screens"]:
        image, placed = draw_screen(screen)
        image.save(frames_dir / f"{screen.frame_id}.png", optimize=True)
        frames.append(
            {
                "id": screen.frame_id,
                "ts_ms": screen.ts_ms,
                "trigger": screen.trigger,
                "image": f"frames/{screen.frame_id}.png",
                "width": WIDTH,
                "height": HEIGHT,
                "redaction_pending": False,
                "redacted_at": "2026-09-11T00:00:00Z",
                "masked_regions": masked_regions(placed),
                "sensitive_context": any(s.kind in {"card", "password"} for s in placed),
                "ocr_text": screen.ocr_text,
                "excluded_by_user": False,
            }
        )

    session = {
        "schema_version": "session.v1",
        **spec["session"],
        "events": spec["events"],
        "frames": frames,
        "transcript": [
            {"id": i, "ts_ms": t, "end_ms": e, "speaker": s, "text": x}
            for i, t, e, s, x in spec["transcript"]
        ],
    }
    if spec["draft"] is not None:
        session["draft"] = spec["draft"]

    (directory / "session.json").write_text(json.dumps(session, indent=2) + "\n", encoding="utf-8")
    return {
        "slug": slug,
        "summary": spec["summary"],
        "frames": len(frames),
        "has_draft": spec["draft"] is not None,
    }


# --------------------------------------------------------------------------------------------------
# The redaction seed: 20 frames with labelled secrets, unmasked on purpose
# --------------------------------------------------------------------------------------------------

SEED = [
    ("card", "4111 1111 1111 1111", "Visa test number in a billing grid"),
    ("card", "5500005555555559", "Mastercard test number, no separators"),
    ("card", "378282246310005", "Amex test number, 15 digits"),
    ("card", "4111-1111-1111-1111", "hyphen separated"),
    ("ssn", "123-45-6789", "SSN on a patient intake form"),
    ("ssn", "078 05 1120", "SSN written with spaces"),
    ("password", "Autumn2026!", "password typed in a sign-in field"),
    ("password", "hunter2", "password pasted into a chat window"),
    ("password", "Sp00lerFix", "password read aloud and typed into a prompt"),
    ("api_key", "AKIAIOSFODNN7EXAMPLE", "AWS access key id from the AWS docs example"),
    ("api_key", "ghp_ExampleExampleExampleExampleExam01", "GitHub-shaped token, not a real one"),
    ("api_key", "xoxb-0000000000-ExampleExampleEx", "Slack-shaped bot token"),
    ("api_key", "sk-ExampleExampleExampleExample0001", "OpenAI-shaped key"),
    ("email", "sarah.jones@acmedental.example", "end-user email in a mailbox list"),
    ("email", "billing@acmedental.example", "shared mailbox address"),
    ("custom_pattern", "ACME-481203", "tenant asset tag, matched by a custom pattern"),
    ("custom_pattern", "ACME-990117", "second asset tag on the same screen"),
    ("card", "4111111111111111", "card with no separators at all"),
    ("ssn", "123-45-6789", "SSN inside a dialog rather than a grid"),
    ("password", "Winter2026", "the transcript's example, shown on screen too"),
]

SEED_DECOYS = [
    "Order 20260912001234",
    "Asset 4111111111111112",
    "Ticket 48213",
    "Ext. 4821",
    "Invoice 2026-09-0042",
]


def write_redaction_seed() -> list[dict[str, object]]:
    directory = ROOT / "redaction-seed"
    frames_dir = directory / "frames"
    if frames_dir.exists():
        shutil.rmtree(frames_dir)
    frames_dir.mkdir(parents=True, exist_ok=True)

    labels = []
    for index, (kind, text, note) in enumerate(SEED, start=1):
        name = f"seed-{index:02d}"
        secret = Secret(kind, text, note)
        decoy = SEED_DECOYS[index % len(SEED_DECOYS)]
        in_dialog = index % 5 == 4
        screen = Screen(
            frame_id=name,
            ts_ms=index * 1000,
            trigger="click",
            tool="screenconnect" if index % 2 else "rdp",
            title="Billing" if kind == "card" else "Account details",
            lines=[f"{decoy} | | ", f"Value | {text} | ", "Status | Open | "],
            columns=("Item", "Detail", ""),
            secrets=[secret],
            dialog=("Confirm the details", ["Check the value above before continuing."])
            if in_dialog
            else None,
            dialog_title="Confirm",
        )
        image, placed = draw_screen(screen)
        image.save(frames_dir / f"{name}.png", optimize=True)
        placed_secret = placed[0]
        assert placed_secret.box is not None
        x0, y0, x1, y1 = placed_secret.box
        labels.append(
            {
                "frame": f"frames/{name}.png",
                "width": WIDTH,
                "height": HEIGHT,
                "secrets": [
                    {
                        "kind": kind,
                        "text": text,
                        "note": note,
                        "box": {"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0},
                    }
                ],
                "decoys": [decoy],
            }
        )

    (directory / "labels.json").write_text(
        json.dumps(
            {
                "about": "Ground truth for the ST-042 redaction evaluation. Every value here is synthetic.",
                "frames": labels,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    return labels


def main() -> None:
    made = [write_bundle(bundle()) for bundle in BUNDLES]
    labels = write_redaction_seed()
    for bundle in made:
        print(f"{bundle['slug']}: {bundle['frames']} frames, draft={bundle['has_draft']}")
    print(f"redaction seed: {len(labels)} frames")


if __name__ == "__main__":
    main()
