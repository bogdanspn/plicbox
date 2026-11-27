# Plicbox website

Static pages with no build step and no external requests: no outside fonts, scripts or analytics. Live at
**https://plicbox.bogdanrosu.net/** (Apache).

```
index.html                    the home page
docs.html privacy.html terms.html   text pages (Google's consent screen links to privacy and terms)
changes.html                  What's new: every version and its notes (written by update-versions.ps1)
404.html                      the error page; self-contained, because Apache shows it at any missing address
css/  base.css                Inter, colours (light and dark), type, footer: every page
      home.css                the home page
      pages.css               docs, privacy, terms, changes
js/   home.js                 tabs, the videos, the feature tiles' animations, the dot floor behind the hero
      docs.js                 marks the section in view in the docs sidebar
icons/                        favicon.svg, apple-touch-icon.png, icon-192/512.png (favicon.ico stays at the root)
fonts/                        Inter (SIL Open Font License, fonts/LICENSE.txt): Latin, and the extra letters Romanian
                              and other European languages need and currency signs, cut down from Google Fonts' Latin Extended file
media/                        the videos recorded from the app, their posters, the link-preview picture
favicon.ico robots.txt sitemap.xml site.webmanifest .htaccess
README.md record-videos.ps1 update-versions.ps1   tools, never published
```

`.htaccess`: https redirect, caching, compression, security headers, the 404 page (needs `AllowOverride All`).

To publish, run `webdisk/deploy.ps1` (see below). It uploads everything here except `README.md` and scripts, which
`.htaccess` would block anyway.
The absolute links (canonical, `og:url`, `og:image`, sitemap) use `https://plicbox.bogdanrosu.net/`. Change them if the address
ever changes. Google sign-in verification steps are in `branding/google/README.md`.

The Download buttons point at the newest installer in the repository's releases:
`https://github.com/bogdanspn/plicbox/releases/latest/download/Plicbox.App-win-Setup.exe`

## Versions

The home page shows the newest version and its date under both Download buttons (between `<!-- version -->` markers),
and `changes.html` lists every version with its release notes. Both are written by `site/update-versions.ps1` from the
GitHub releases (needs `gh`). `release.ps1` runs it after each upload; commit the result and deploy. The release notes
are the `-Notes` given to `release.ps1`: one `- change` per line.

The same script fills the home page's "Measured, every release" (between the `<!-- perf -->` markers) from
`perf/history.csv`. The charts hold every recorded version; the start time and memory above them are always a median (`runs` > 1).

## Publishing

`webdisk/deploy.ps1` publishes the site through cPanel Web Disk (WebDAV), the same way wallnode's `tools/deploy.ps1` does:
committed files only, only what changed in `site/` since the last deploy, every upload read back to verify, and nothing
ever deleted on the server (removed files are listed to delete by hand). Then it opens the live pages.

```powershell
pwsh -File webdisk\deploy.ps1 -Setup          # once per machine: Web Disk address (Enter = https://bogdanrosu.ro:2078) and login
pwsh -File webdisk\deploy.ps1 -All            # the first time: every file in site/
pwsh -File webdisk\deploy.ps1                 # afterwards: what changed since the last deploy (commit first)
pwsh -File webdisk\deploy.ps1 -DryRun         # list what would go up
pwsh -File webdisk\deploy.ps1 -Check          # compare the server with site/
pwsh -File webdisk\deploy.ps1 -InstallHook    # publish automatically after each commit that touches site/
```

- The Web Disk is reached at the server's own name, `bogdanrosu.ro:2078`. Names behind Cloudflare (plicbox.bogdanrosu.net,
  webdisk.…) can't be used: Cloudflare doesn't pass port 2078 through.
- The address and the login (encrypted with DPAPI) are kept in `%APPDATA%\Plicbox\site-deploy`, not in the repository.
- Cloudflare caches pictures and videos. To clear its copies of what a deploy changed, set `CLOUDFLARE_API_TOKEN` (a token
  with Zone > Cache Purge) and `CLOUDFLARE_ZONE_ID`; otherwise purge in the Cloudflare dashboard, or wait.

## Feature tiles

The small pieces of the app in the home page's Features tiles (the tracking and pictures notes, the phishing banner,
Undo, the search box, the Outbox, the Reply button in four languages, the accent colours, the rolling €0) are plain HTML and CSS, not
pictures. Their wording and colours are the app's own, from `src/Plicbox.Core/Languages/*.json` and
`Services/Appearance.cs`; when those change, change the tiles too. The pop-ups struck through around the headline are
made up on purpose: the noise Plicbox leaves out.

Two figures in the tiles are measured, not made up; check them now and then:
- Search, "250,000 messages searched in 0.013 s": Plicbox's own search (MessageRepository.SearchAsync, from the 0.1.24
  build) on a test mailbox of 250,000 made-up messages (words from Plicbox's own texts), on the PC Plicbox is built on.
  "boarding pass" took 12.6 ms (median of 25); the most common words up to 0.17 s.
- Phishing, "About 150,000 reported sites": the app logs "Threat lists loaded: N entries" (153,732 on 26 November 2026:
  41,013 phishing, 112,722 malware). Their animations run only while on screen, and not at all for
reduced motion.

## Logos

The accounts under the hero video show their providers' logos, one grey, in their own colours when pointed at: Gmail,
Google, iCloud, Zoho and GMX from [Simple Icons](https://simpleicons.org) (CC0), and Microsoft's four squares, the mark
it gives "Sign in with Microsoft" buttons. The names and logos belong to their owners, and only say which accounts work.
Simple Icons dropped Microsoft's, Yahoo's and Fastmail's own logos at their owners' request, so Yahoo and Fastmail are
named next to "any IMAP account" instead.

## Videos

The app's pictures on the page are short videos recorded from the app itself (demo data: fictional mail, no real
accounts), one per module in light and dark, each with a `.jpg` of its first frame:

| File | What happens |
|---|---|
| `media/hero-*` | new mail arrives, then a reply to Ana is typed and sent |
| `media/video-calendar-*` | "Coffee with Mia Friday 3pm" typed into Quick add appears in the week |
| `media/video-contacts-*` | Keep in touch opens the colleague you haven't written to in three months |
| `media/video-tasks-*` | a task typed into Today, then ticked off |
| `media/video-settings-*` | Settings opens over the inbox and goes through its pages |

The page picks the visitor's theme, loads a video only once it's on screen and shows just the first frame for reduced
motion. `mail-*.png` is only the link preview image.

To record them again after UI changes, run `site/record-videos.ps1` (about four minutes; needs ffmpeg,
`winget install Gyan.FFmpeg`). It builds the app separately, lets it film itself with `--demo --record-site <folder>`, and
encodes the results into `media/`. Keep the app window on screen while it records; other windows in front are fine.
`--screenshot-site <folder>` still takes still pictures of each module, if needed.
