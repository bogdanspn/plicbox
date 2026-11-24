# Google sign-in: from Testing to published

Everything needed to fill in the Google Cloud console (Google Auth Platform) for Plicbox's built-in OAuth client.

| File | What it's for |
|---|---|
| `plicbox-logo-120.png` | App logo for the Branding page (120×120 PNG, as Google asks) |
| `scope-justifications.md` | Text to paste into the Data access / verification form, one block per scope |
| `demo-video.md` | Shot list for the unlisted YouTube video Google asks for |

## 1. Put the website up

Upload the contents of `site/` to **https://plicbox.bogdanrosu.net/** (everything except `README.md`; `.htaccess` blocks it anyway).
Check these open without a login and over https:

- https://plicbox.bogdanrosu.net/
- https://plicbox.bogdanrosu.net/privacy.html
- https://plicbox.bogdanrosu.net/terms.html

The home page links to the privacy policy (in the note under the Download button and in the footer) and explains what Google data is
used for. Google checks both.

## 2. Prove you own the domain

The **Google account that owns the Cloud project** must be a verified owner of `bogdanrosu.net` in
[Search Console](https://search.google.com/search-console).

- Open Search Console with that account. If `bogdanrosu.net` is already listed as a **Domain** property (it may be, because the
  domain is set up for Google mail), you're done.
- If not: Add property > **Domain** > `bogdanrosu.net` > add the TXT record it shows to the domain's DNS > Verify.
  A Domain property covers every subdomain, so `plicbox.bogdanrosu.net` is included.
- If the domain was verified by a different Google account, add the project owner as an **Owner** under Settings > Users and permissions.

## 3. Branding page

Google Auth Platform > **Branding**:

| Field | Value |
|---|---|
| App name | `Plicbox` |
| User support email | the project owner's address, or a Google Group they manage (the list only offers those) |
| App logo | `plicbox-logo-120.png` |
| Application home page | `https://plicbox.bogdanrosu.net/` |
| Application privacy policy link | `https://plicbox.bogdanrosu.net/privacy.html` |
| Application terms of service link | `https://plicbox.bogdanrosu.net/terms.html` |
| Authorized domains | `bogdanrosu.net` |
| Developer contact information | the address Google should write to about the review |

Uploading a logo means the brand has to be verified before the logo shows (2–3 business days). That review happens anyway.

## 4. Publish (same day)

Google Auth Platform > **Audience** > **Publish app** > confirm. It's no longer greyed out once Branding is filled in.

What changes right away:

- No test-user list: anyone can sign in.
- **Sign-ins no longer expire after 7 days.** While an app is in Testing, Google expires its refresh tokens weekly.
- Until verification passes, people see a "Google hasn't verified this app" screen (Advanced > Go to Plicbox), and at most
  100 Google accounts can ever sign in.

Existing testers should use **Sign in again** once after publishing, so they get a token that doesn't expire.

## 5. Submit for verification

Google Auth Platform > **Data access**: the scopes must match what the app asks for (`src/Plicbox.Auth/GoogleAuth.cs`):

| Scope | Level |
|---|---|
| `https://mail.google.com/` | **Restricted** |
| `https://www.googleapis.com/auth/calendar` | Sensitive |
| `https://www.googleapis.com/auth/contacts` | Sensitive |
| `https://www.googleapis.com/auth/contacts.other.readonly` | Sensitive |
| `https://www.googleapis.com/auth/tasks` | Sensitive |
| `openid`, `email`, `profile` | Basic |

Then **Verification center** > Prepare for verification:

1. Paste the justifications from `scope-justifications.md`.
2. Record and upload the video from `demo-video.md` (YouTube, **Unlisted**) and paste its link.
3. When asked how data is handled, answer that Plicbox is a **local client**: it has no servers, Google user data and tokens are
   only stored on the user's own PC, and the OAuth redirect goes to a loopback address on that PC.
4. Submit, then watch the developer contact inbox. Reviewers write there and expect replies in the same thread.

### Security assessment (CASA)

Google requires a yearly paid security assessment for restricted scopes when data is stored on, or passes through, the developer's
servers. Plicbox has none, so ask for the local-client treatment and point at the privacy policy. If the reviewer still insists:

- either do the assessment (an authorised lab, yearly, paid), or
- drop `https://mail.google.com/` and have Gmail users connect with IMAP and an app password, keeping Calendar, Contacts and
  Tasks (sensitive scopes only, no assessment).

### Narrower scopes the reviewer may suggest

Reviewers sometimes ask whether a narrower scope would do. Likely questions and answers are in `scope-justifications.md`. The one
that could come up is Calendar: `calendar.events` plus `calendar.calendarlist.readonly` might cover what Plicbox does. If they push
on it, switching is a small change in `GoogleAuth.cs` and `GoogleCalendarApi.cs`.

## Timeline

| Step | How long |
|---|---|
| Website, Search Console, Branding, Publish | a day |
| Brand verification | 2–3 business days |
| Sensitive + restricted scope review | a few weeks, longer if Google asks questions |

## When something changes later

- New scope in the app > add it under Data access and submit again before shipping it.
- Privacy policy, home page or domain change > update Branding. Big changes trigger a new review.
- Restricted-scope apps are re-checked every year. Google emails the developer contact.
