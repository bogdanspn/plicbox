# Plicbox

A personal, no-AI Windows email client (mail, calendar, contacts, tasks) built on .NET 10 + WPF + MailKit.

## Run

Double-click `run.cmd`, or from a terminal in this folder:

```powershell
.\run.ps1              # build and start with your real accounts (menu ☰ → Add account…)
.\run.ps1 -Demo        # fictional sample mail, throwaway database, no syncing
.\run.ps1 -Test        # unit tests (integration test skipped)
```

The same by hand:

```powershell
dotnet run --project src/Plicbox.App             # your real accounts (menu ☰ → Add account…)
dotnet run --project src/Plicbox.App -- --demo   # fictional sample mail, throwaway database, no syncing
dotnet test                                       # unit tests (integration test skipped)
```

Passwords and Google tokens live in Windows Credential Manager (`Plicbox/account/<id>`), never in the database.

### Google accounts (Gmail and Google Workspace)

Detected automatically, including custom domains on Workspace (via MX records). They sign in with Google in the
browser (OAuth), through **your own** Google Cloud OAuth client. One-time setup:

1. [Google Cloud Console](https://console.cloud.google.com/projectcreate): create a project, e.g. "Plicbox".
2. APIs & Services → Library → **Gmail API** → Enable.
3. Google Auth Platform → Branding: app name, your email. Audience: **External**, then **Publish app** (In production).
   Leaving it in "Testing" works too, but Google then expires the sign-in every 7 days.
4. Data Access → add scope `https://mail.google.com/`.
5. Clients → Create client → **Desktop app** → Download JSON.
6. In Plicbox's Add account dialog, **Choose client file…** (copied to `%LOCALAPPDATA%\Plicbox\google-client.json`).

On first sign-in Google warns the app is unverified. It's your own app: Advanced → Go to Plicbox.
Company Workspace accounts may need their admin to allow the app. App passwords remain available as a fallback.
iCloud and Yahoo use app passwords.

### Testing against a local mail server

Integration tests and the debug-only `--dev-greenmail` mode use [GreenMail](https://greenmail-mail-test.github.io/greenmail/)
(IMAPS 3993, SMTPS 3465, user `alice` / `secret`):

```powershell
java -Dgreenmail.setup.test.all -Dgreenmail.hostname=127.0.0.1 -Dgreenmail.users=alice:secret@localhost -jar greenmail-standalone-2.1.3.jar
$env:PLICBOX_GREENMAIL = "1"; dotnet test                       # full sync/send round trip
dotnet run --project src/Plicbox.App -- --dev-greenmail          # the real app against it (sandboxed data)
```

Data lives in `%LOCALAPPDATA%\Plicbox` (`plicbox.db`, `bodies\`, `logs\`). Demo mode uses `%LOCALAPPDATA%\Plicbox\demo` and wipes it on each start.

## Releasing and updates

```powershell
.\release.ps1                     # next patch version: build, pack, publish
.\release.ps1 -Version 0.2.0 -Notes "What changed"
.\release.ps1 -NoUpload           # pack only, into artifacts\releases
```

Releases go to the public [plicbox-releases](https://github.com/bogdanspn/plicbox-releases) repo (installer and
update packages only, no source). Install once with `Plicbox.App-win-Setup.exe` from there; the app lives in
`%LOCALAPPDATA%\Plicbox.App`, checks for a new version a minute after start and every 4 hours (Settings → General →
Updates), downloads only what changed, and installs it when it quits or on **Restart to update**. Uninstalling removes
the app, never the mail data. The installer isn't signed, so Windows asks once (More info → Run anyway).

Each release is also measured (`perf\measure.ps1`): download and install size, how long until the mail list is up, and
memory for Plicbox and its message display, with the demo's data (`--demo --measure`, a window opens for about 20
seconds). The figures are printed next to the last release's, with a warning when one grew noticeably, and kept in
`perf\history.csv`, committed with the release.

A build from `run.cmd` is a **dev build** (a DEV tag by the title): it shares the installed app's mail, settings and
sign-ins but runs beside it, never updates, and leaves starting with Windows to the installed one. Its log is
`logs\plicbox-dev-*.log`.

## Layout

| Project | Purpose |
|---|---|
| `Plicbox.App` | WPF shell, view models, WebView2 reading pane |
| `Plicbox.Core` | Domain models, repository interfaces, threading |
| `Plicbox.Storage` | SQLite + FTS5 search, `.eml` body cache |
| `Plicbox.Mail` | IMAP sync engine (IDLE push, offline action queue), SMTP sending, discovery, MIME rendering, reply/forward |
| `Plicbox.Dav` | Calendar, contacts and tasks: Google APIs, local calendars, offline change queues, `.ics`/`.vcf` |
| `Plicbox.Auth` | Windows Credential Manager store; Google and Microsoft sign-in |

## Privacy defaults

Message HTML is sanitized, scripts are disabled, and remote images/fonts (tracking pixels) are blocked via CSP until you click **Show images**. Clicked links open in your default browser; messages can never navigate the reading pane.

## Safety warnings

- **Doubtful senders** get a red or amber banner explaining why: the message failed the sender domain's SPF/DKIM/DMARC checks (as recorded by your mail server), its display name shows a different address than the real one, its domain uses look-alike letters from another alphabet (`pаypal.com` with a Cyrillic "а"), it claims to come from your own address, or replies would go to a free mailbox instead of the sender's company.
- **Attachments** always show their extension in a separate chip, so a long name can't hide it. Files that can run programs (`.exe`, `.js`, `.lnk`, `.iso`, …) are red and ask before opening; macro documents and HTML pages are amber. Names disguised as documents (`invoice.pdf.exe`, padding before the real extension, hidden right-to-left characters) are unmasked.

## Pictures

Senders show a coloured letter by default. You can give each of your accounts its own picture (Accounts → General → Picture), shown on mail you send. With **Settings → Pictures** on (the default), Plicbox also looks senders up once: the person's Gravatar, a company's verified BIMI logo (the one Gmail shows), or — for automated senders like `noreply@` — the website's icon. Free-mail domains never get a company logo, mail in Junk is never looked up, results are cached for a month, and a sender flagged as possibly forged never gets a picture in the reading pane.

## Writing mail

The compose window has an eM Client–style toolbar (attach, importance, signature, read receipt, text formatting, font, colours, lists, alignment, links, pictures, emoji) and a **…** menu (reply-to address, save draft, print, HTML or plain text, undo/redo, table, horizontal line). Web addresses turn into links as you type or paste them, and any still left as plain text are linked when the message is sent. Pasted screenshots and inserted pictures go out as embedded images.

Drafts are saved with `Ctrl+S`, automatically every minute, or when you close the window and choose **Save draft**. They're stored on this PC first, then uploaded to the account's Drafts folder (a `SaveDraft` pending op, so it works offline); each save replaces the previous copy. Opening a message in Drafts continues editing it, and sending it removes the draft.

Sent mail goes through a local **Outbox** (a folder per account with role `Outbox`, never synced; shown once under Favorites). A message waits there for the undo-send delay (Settings → Mail → Sending, 30 s by default; **Undo** appears next to Refresh), or until the time chosen with **Send later**, then goes out when the server can be reached and is filed in Sent. A message the server refuses stays in the Outbox with the reason.

## Offline and backups

- New accounts download whole messages for the last year (Accounts → Sync options → Offline mail: a month, 6 months, a year, everything, or only when opened).
- Mail actions, Google calendar changes (`calendar_changes`) and contact changes (`contact_changes`) are queued while offline and sent before the next sync reads anything back.
- Settings → Backup: a daily zip of everything that exists only here (the database without mail, plus `settings.json`) into `OneDrive\Plicbox Backups` or `Documents\Plicbox Backups`, the newest 10 kept. A restore is staged and applied on the next start.
- Calendars import and export `.ics` (right-click a calendar; **Add to Calendar** on `.ics` attachments), contacts `.vcf` (the **…** button in Contacts).

## Shortcuts

`Ctrl+F` search · `Del` delete · `Ctrl+Q` toggle read · `Ctrl+E` archive · `F5` sync now

Select several messages with `Shift`/`Ctrl`+click (or `Ctrl+A`), then drag them onto a folder or use Delete, Archive, Move to Folder…, Copy to Folder…, Junk, flag and read/unread on all of them at once.

Toolbar split buttons, as in eM Client: **Refresh ▾** (receive all, one account only, Show Operations), **Forward ▾** (as attachment, as original, resend) and **Mark ▾** (flag, read, unread, junk). When the window gets narrow, the message actions fold into **More ▾**.

Right-click a folder for New Folder, Rename, Move Folder, Delete, Empty Trash/Junk and Mark All as Read (these change the server), plus Show / Show if unread / Hide, Add to Favorites, Folder Color and Properties (these only change Plicbox's sidebar). The account menu can bring hidden folders back.

Right-click inside a message for Copy, Select All, Find (`F4`), link actions, reply/forward, flags, Save As / Save Attachments, folder moves, Print, **Format › Plain Text** (shows just the text, handy for suspicious mail), Properties, the raw header/source and Zoom.

Compose: `Ctrl+Enter` send · `Ctrl+S` save draft · `Ctrl+K` link · `Ctrl+P` print

## Licence

Plicbox is free software: Copyright (C) 2026 Cristian Bogdan Rosu, under the [GNU General Public License, version 3](LICENSE),
with the additional terms in [NOTICE.md](NOTICE.md). In short: you may use, study, change and share it; a changed
version you share must say it's changed, carry its own name and icon (the Plicbox name and logo aren't licensed), and
stay under the GPL with its source available.

The libraries it ships are by others, under MIT, Apache 2.0 and BSD licences; their notices are in
[src/Plicbox.App/THIRD-PARTY-NOTICES.txt](src/Plicbox.App/THIRD-PARTY-NOTICES.txt), regenerated by
`licenses\update-notices.ps1` (release.ps1 runs it; a test fails when a package is missing).

Building from source works without the Google sign-in client that release builds carry (`src/Plicbox.Auth/google-client.json`,
not in the repository): Google accounts then use your own OAuth client, as described under *Google accounts* above.

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).
