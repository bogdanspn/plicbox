# Demo video

Google wants a screen recording that shows the whole sign-in and then each scope being used. Upload it to YouTube as **Unlisted**
and paste the link into the verification form.

## Before recording

- Use the **installed release build** (not `--demo`, not a dev build) so the OAuth client is the one being verified.
- Set Plicbox and the browser to **English**. The consent screen must be in English.
- Use a test Google account with some mail, a couple of events, contacts and tasks. No personal data on screen.
- 1080p, no music needed. Short captions or voice-over help ("Now showing calendar access").
- About 3–5 minutes.

## Shot list

1. **The app and its website.** Open https://plicbox.bogdanrosu.net/ briefly, then start Plicbox. Shows the app name and home page match.
2. **Start sign-in.** Add account > Google. The browser opens Google's sign-in page.
3. **The OAuth client ID.** Zoom into the browser's address bar so `client_id=…apps.googleusercontent.com` is readable.
   Google checks that it matches the project being reviewed. Easy to miss, and a common reason for rejection.
4. **Consent screen.** Pick the account, show the consent screen with the app name "Plicbox" and the list of permissions, then Continue.
5. **Back in the app.** The account appears and mail loads.
6. **https://mail.google.com/**
   - Open and read a message, search for it (works offline too).
   - Flag it, move it to a folder, delete it. Then show the same change in Gmail on the web (two-way sync).
   - Write and send a mail. Pick a "Send mail as" alias in From if the account has one. Show it arriving in Gmail's Sent.
7. **contacts.other.readonly:** in a new mail, type part of a recipient's name and show the suggestions.
8. **calendar:** open Calendar, show the calendar list with colours, create an event, move it, answer an invitation. Show it in Google Calendar.
9. **contacts:** open Contacts, add or edit a contact (or "Save sender to contacts" from a mail). Show it in Google Contacts.
10. **tasks:** open Tasks, add a task, complete one, turn an email into a task. Show it in Google Tasks.
11. **Data stays local (optional, helps the local-client case).** Show `%LOCALAPPDATA%\Plicbox` in Explorer and say there is no
    Plicbox server: everything goes straight between the PC and Google.
12. **Removing access.** Right-click the account > Remove account. Mention the Google Account permissions page for revoking.
