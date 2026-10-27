# Running the ScreenTail demo

**What this is for.** Showing the whole of ScreenTail on one Windows laptop: a support call is recorded,
a password typed during it is kept out, Gemini drafts the ticket note, and you review it. Two commands
start it and one stops it. Each of them says in plain words what is wrong when something is missing.

**Read this first.** As of 2026-10-27 the whole loop has never run on a real machine. The pieces are
tested; the joins between them are not. So do a full rehearsal on the laptop before the real demo, and
write down anything that looks wrong (the checklist at the end says what to look for).

The scripts are in `scripts\windows\demo\`. They do what the manual runbook
(`docs/dev/first-draft-end-to-end.md`) describes, in the same order, and check each step.

---

## What you need

| You need | Notes |
|---|---|
| The Windows test laptop, signed in, with you at it | Everything runs on the laptop, the backend included |
| Your own copy of the repository | For example `C:\src\ScreenTail`. Not the CI runner's folder. If you do not have one: `git clone https://github.com/hiratinspace/ScreenTail.git C:\src\ScreenTail`. If you do: `git pull` in it |
| The .NET 10 SDK | Already on the laptop (the runner's setup installed it). Setup checks |
| PostgreSQL, or Docker Desktop | If neither is there, setup prints the one line to install PostgreSQL and stops |
| The Gemini API key | The free tier is enough. On the Mac: `cd backend/src/ScreenTail.Api && dotnet user-secrets list` shows it (mind who is looking) |
| Internet | Gemini is on the internet, and so is the speech model ScreenTail downloads the first time |
| For showing recording start by itself: a second Windows PC or a virtual machine you can connect to with Remote Desktop | Optional. See "Two ways to run it" |

**The free Gemini key allows about 20 drafts a day, and 5 a minute.** Each session you stop and draft
uses one or two. Rehearse with that in mind, and do not rehearse the morning of the demo more than a
couple of times.

**Use only made-up details in the demo.** The note is drafted by Google's Gemini, and on the free tier
Google may use what it is sent to improve its products. The story below is invented for that reason.

---

## Set up (once, about ten minutes)

1. Open **PowerShell**: Start menu, type `PowerShell`, press Enter. Not "Run as administrator".
2. Go to your copy of the repository:
   ```powershell
   cd C:\src\ScreenTail
   ```
3. Run setup:
   ```powershell
   scripts\windows\demo\setup.cmd
   ```

It works through five things and says "ok" after each:

- **The .NET SDK.** Nothing to do unless it says it is missing.
- **The development certificate.** The first time, Windows shows a *Security Warning* about installing a
  certificate for "localhost". Answer **Yes**. This is what lets the laptop's ScreenTail talk to the
  laptop's backend over HTTPS without switching any safety check off.
- **The Gemini key.** It asks you to paste it. **Nothing appears while you paste; that is on purpose.**
  Right-click pastes in PowerShell. Then press Enter. It asks Google whether the key works and says so.
- **The signing key.** Made for you. Nothing to do.
- **PostgreSQL.** If PostgreSQL is installed, it asks for the password you chose when you installed it.
  If only Docker Desktop is there and running, it creates a small database container for you and makes up
  its password itself. If neither is there, it prints this line and stops:
  ```powershell
  winget install --id PostgreSQL.PostgreSQL.17 -e --source winget
  ```
  Run it, choose a password for the "postgres" user when the installer asks (and keep it), leave the
  rest at the defaults, open a **new** PowerShell window and run setup again.

It ends with **Setup is complete**. Running setup again later is harmless: it checks each thing and keeps
what is already there.

The key, the signing key and the database password are saved in one place only: the backend's
"user secrets" file in your Windows profile (`%APPDATA%\Microsoft\UserSecrets\screentail-api\secrets.json`).
They are never shown on screen or written anywhere else.

| To... | Run |
|---|---|
| Replace the Gemini key | `scripts\windows\demo\setup.cmd -NewGeminiKey` |
| Enter the database password again | `scripts\windows\demo\setup.cmd -NewDatabasePassword` |
| Set up while offline (skip asking Google about the key) | `scripts\windows\demo\setup.cmd -SkipKeyCheck` |

---

## On the day

### Before the audience arrives

1. **Close the CI runner's window.** The laptop is also the project's test machine, and a test job that
   starts during the demo takes over the keyboard, the mouse and the screen. Its window is minimized on
   the taskbar; its title ends in `run.cmd`. Close it. It comes back the next time you sign in to Windows.
   (`start.cmd` refuses to run while a test job is actually running, and warns you while the runner is
   merely waiting.)
2. **Close chat and mail apps** (Teams, Outlook, Slack) so nothing private pops up on screen. Do *not*
   turn on Windows "Do not disturb": it also hides ScreenTail's "Draft ready" notification.
3. **Start ScreenTail, no more than 15 minutes before you begin:**
   ```powershell
   cd C:\src\ScreenTail
   scripts\windows\demo\start.cmd
   ```
   The first start takes a few minutes (it builds everything). Later starts take under a minute.

### What start does, and what you will see

It checks everything setup did, then opens two windows and puts an icon in the notification area:

- a window titled **ScreenTail demo: backend**. Leave it open. It shows one line for each request.
- a window titled **ScreenTail demo: capture service**. Leave it open. It is the part that records.
- the **ScreenTail icon** in the notification area, bottom right (it may be hidden under the `^` arrow).

It finishes with a green **READY** and the line "Do this next". It also says until when the device's
token is good. **That token lasts one hour.** If the demo will run past that time, run `stop.cmd` and
`start.cmd` again (about a minute). A session that was waiting to be drafted is drafted after the restart.

**The very first time,** ScreenTail opens its setup wizard. On **Activate**, press **Skip for now** (start
has already connected the laptop). On **Permissions**, check the three marks are green; if the microphone
is not, press **Fix in Windows Settings** and allow microphone access for desktop apps. Leave the tools as
they are, skip **Connect**, and either try the sixty-second test or press **Finish**. The test is worth
doing once: the first session also downloads the speech model in the background.

If something is wrong, start stops with **PROBLEM:** and a **What to do** list. The common ones:

| It says | What it means |
|---|---|
| Setup has not been run, or did not finish | Run `setup.cmd` |
| The development certificate is missing, expired or not trusted | Run `setup.cmd` again and answer Yes to Windows |
| Port 5001 is already in use by ... | Something else, often an earlier backend, holds the port. The message names it and how to close it |
| PostgreSQL is installed but its Windows service is Stopped | Start it from the Services app, as it says |
| Nothing answers ... Docker is installed but not running | Start Docker Desktop, wait, start again |
| PostgreSQL refused the saved password | `setup.cmd -NewDatabasePassword` |
| The backend refused the device token | Start again; it gets a fresh one |
| ScreenTail is already running, started some other way | Quit it from its tray icon, as it says |
| A GitHub Actions job is running on this laptop | Wait for it to finish |
| Windows blocked the capture service (Application Control) | Unsigned fresh builds trip a Windows policy on this laptop; see the message |

### After the demo

```powershell
scripts\windows\demo\stop.cmd
```

It closes exactly what start opened and nothing else. If you want the capture service to finish
cleanly, press **Ctrl+C** in its window first; stop closes it either way, and a session cut off in the
middle is recovered the next time it starts. Your sessions and drafts are kept.

---

## Two ways to run it

ScreenTail starts recording by itself when a **remote-support tool** (Remote Desktop, ScreenConnect,
TeamViewer and a few others) comes to the front. On the laptop alone there is no remote session, so
there are two ways to run the demo.

**A. Remote Desktop into a second PC or a virtual machine (recommended).** Open the **Remote Desktop
Connection** app on the laptop and connect to the other machine. The moment its window is in front, the
recording pill appears: that is the "it starts by itself" moment. Everything you do inside that window
is recorded. When you leave the Remote Desktop window, recording carries on for 90 seconds (the grace
period, in Settings > Capture) and then stops on its own. A Hyper-V virtual machine on the laptop itself
works too, as long as you connect to it with the Remote Desktop app rather than the Hyper-V window.

**B. The laptop alone.** Press **Ctrl+Alt+R** to start recording yourself; say that a real technician's
session starts on its own when ScreenConnect or Remote Desktop opens. By default ScreenTail only records
remote-support tools and admin tools (PowerShell, Control Panel, File Explorer, Event Viewer and the
like), so the Windows Settings app and the print queue would be left out. For the demo, turn on
**All windows**: right-click the tray icon > **Open ScreenTail** > Settings > Capture > Capture scope > **All windows during a
session**, then press the **I understand** button under the warning that appears, and save.

Note that "All windows" does **not** make recording start by itself. It changes *what* a running session
records, not *when* one starts (only a remote-support tool starts one). Only way A shows the automatic
start.

**Turn All windows off again after the demo** (Settings > Capture > **Remote-tool windows and admin
tools (Recommended)**). Left on, it records every window you use during a session, your email included.

---

## The demo story: the front desk printer at Bright Smile Dental

*Everything here is made up: the practice, the people, the names and the password.*

You are the IT person for a small dental practice. Maria at the front desk calls: the printer says
**Offline**, and patients are waiting for their receipts. The practice prints through the back-office
PC, and Windows on the front desk PC has lost the saved password for it.

### Set the scene (the day before, about five minutes)

On the machine the story happens on (the Remote Desktop PC or VM for way A, the laptop for way B), open
PowerShell **as administrator** and add a printer that will play the part:

```powershell
Add-Printer -Name 'Front Desk Printer' -DriverName 'Microsoft Print To PDF' -PortName 'PORTPROMPT:'
```

Then: Settings > Bluetooth & devices > Printers & scanners > **Front Desk Printer** > **Open print
queue**. In the queue window's **Printer** menu, tick **Use Printer Offline**. Back on the printer's
page, press **Print test page** once or twice: the pages wait in the queue. The printer now shows
*Offline* with jobs stuck, which is the call Maria made. (Check this the day before: if your Windows
build words the menu differently, the story still works with whatever it shows.)

### The call, step by step

Talk while you work: what you say is half of what the note is written from. About five minutes.

| # | What you do | What you say (out loud) | What the audience sees |
|---|---|---|---|
| 1 | Way A: open Remote Desktop to the front desk PC. Way B: put the Settings app in front and press **Ctrl+Alt+R** | "Maria at Bright Smile Dental says the front desk printer is offline. I'm connecting to the front desk PC." | The **recording pill** appears at the top right of the screen with a dot and a running timer. With way A it appears by itself |
| 2 | Settings > Bluetooth & devices > Printers & scanners > Front Desk Printer | "The front desk printer shows Offline, and there are two jobs waiting." | The pill keeps counting |
| 3 | Open Control Panel > User Accounts > **Credential Manager** > Windows Credentials > **Add a Windows credential**. Type `BACKOFFICE-PC` as the address and `brightsmile\frontdesk` as the user name, then click into **Password** and type `Molar-Express-26`. Press OK | "The front desk prints through the back-office PC, and Windows has lost the saved password for it. I'm saving it again." | When the password box gets the cursor, the pill changes to **Paused: sensitive field**, and goes back to the timer after. Nothing typed into the box is recorded, and no screenshot is taken while it is paused |
| 4 | Say the password back, as you might on the phone | "Just to confirm, Maria, the password is Molar-Express-26." | Nothing on screen yet. In the note and the transcript this should appear masked (see the rehearsal checklist) |
| 5 | Open print queue > **Printer** menu > untick **Use Printer Offline** | "It was also stuck in offline mode, which Windows does after it fails to reach a printer a few times. Taking it out of offline mode." | The printer changes from Offline to ready |
| 6 | In the queue, right-click the old jobs > **Cancel**. Then on the printer's page press **Print test page** | "Clearing the two stuck jobs, and printing a test page to make sure it goes through." | (With the stand-in printer, Windows asks where to save the page: press Cancel, or save it anywhere) |
| 7 | | "That's fixed: the saved password for the back-office PC was missing and the printer was stuck offline. Maria can print again." | |
| 8 | Way A: close the Remote Desktop window first (inside it, Remote Desktop may send your keys to the other PC instead). Then press **Ctrl+Alt+S** (or right-click the tray icon > **Stop and draft**) | "And that's the end of the call. I didn't write a single note." | The pill says **Finishing up**, then **Not recording: draft ready** (usually within a minute). A notification says the draft is ready |
| 9 | Click the notification (or right-click the tray icon > **Open ScreenTail**) | "ScreenTail has written the ticket note for me. I just review it." | The **Review** screen: the drafted note on the left, the screenshots beside it, in order |
| 10 | Point at the steps, then at the gap where the password was typed | "Each step links to the screenshot it came from. Here, where I typed the password, there is no screenshot at all: it paused itself." | Where step 3 was, the timeline says capture was paused because a password field had focus |
| 11 | Select a screenshot, press **B** and drag over the PC name or anything you would rather not keep | "And if anything on a screenshot shouldn't go in the ticket, I blur it." | The region is blurred |
| 12 | Edit one sentence in the note | "I can change anything before it goes anywhere. ScreenTail learns from what I change." | The edit |
| 13 | **Publish.** This step uses the **Demo PSA once it lands; until then, stop at the reviewed draft.** Say: | "When I'm happy, one click puts it in the ticket. Nothing is ever sent to the ticket system without that click." | Until the Demo PSA exists: stop here. Do not press Publish |

### Afterwards, put the scene back

```powershell
Remove-Printer -Name 'Front Desk Printer'
```

and remove the made-up `BACKOFFICE-PC` entry from Credential Manager. If you used way B, turn **All
windows** off again (see above).

---

## When something goes wrong during the demo

| What you see | What it means and what to do |
|---|---|
| The pill does not appear when Remote Desktop opens | The capture service window may have stopped; read it. Or press Ctrl+Alt+R and carry on |
| The pill says "Not capturing: ..." | The window in front is not one ScreenTail records. Way B needs **All windows** on |
| "This device is not enrolled yet" in Review | The one-hour token ran out. `stop.cmd`, `start.cmd`; the waiting session is then drafted |
| "reached its drafting budget for today" | The backend's own daily cap. Unlikely in a demo; drafting resumes tomorrow |
| The draft fails and the backend window shows a 429 from Google | The free key's quota is used up (20 a day, 5 a minute). Wait a minute, or until tomorrow |
| No "Draft ready" after two minutes | Look at the backend window: one request to `/v1/sessions/summarize` should be there. If not, the capture service window says why |
| A certificate error in the capture service window | Run `setup.cmd` again (answer Yes to the certificate) and start again |

## Activated device

If this laptop was ever activated with an **invite code** (the wizard's Activate step, or Settings),
start sees that and does not hand it a one-hour development token: an activated device renews its own,
which also means no hourly restart. If drafts then say the device is not enrolled or was revoked (for
example because the database was made fresh), go back to the development token: run `stop.cmd`, delete
the file `%LOCALAPPDATA%\ScreenTail\device.cred`, and run `start.cmd`.

## Where things are kept

| What | Where |
|---|---|
| The Gemini key, the signing key, the database password | `%APPDATA%\Microsoft\UserSecrets\screentail-api\secrets.json` only |
| Recorded sessions and drafts (encrypted) | `%LOCALAPPDATA%\ScreenTail`, the product's own folder |
| The demo's own working files: the published programs, which processes start opened, and the backend's log (no session content in it) | `%LOCALAPPDATA%\ScreenTail.Dev\demo` |
| What is sent to Google for each draft | The session after redaction: screenshots, what was said, and the clicks, through the backend. Nothing is published anywhere without the Publish click |

Nothing is set in the Windows environment for good: the two settings the capture service needs
(`SCREENTAIL_BACKEND` and `SCREENTAIL_DEVICE_TOKEN`) are given to that one process when start launches it.

---

## Rehearsal checklist (the first real run)

Do the whole story once, then write down what happened. These are the things never yet seen on a real
machine:

- [ ] `setup.cmd` ran to "Setup is complete", and running it a second time changed nothing.
- [ ] `start.cmd` reached READY. How long did the first start take? A second start?
- [ ] The pill appeared on its own when Remote Desktop came to the front (way A).
- [ ] The pill showed **Paused: sensitive field** while the password box had the cursor (step 3), and
      there is no screenshot of that moment in Review.
- [ ] The spoken password (step 4) appears masked in the transcript and is not in the note. If it is
      not masked, drop step 4 from the demo and tell the developer.
- [ ] "Draft ready" arrived. How long after Ctrl+Alt+S?
- [ ] **Does the note describe what you did?** This is the question that matters most.
- [ ] Blur (B) and editing worked in Review.
- [ ] `stop.cmd` closed the two windows and the tray icon, and nothing else.
- [ ] Anything odd about the pill, the tray, Review or the wording.

Send the list to the developer, with `%LOCALAPPDATA%\ScreenTail.Dev\demo\backend.log` if anything failed.
That log has no session content in it, but read it before sending, as with anything.
