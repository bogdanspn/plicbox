# Scope justifications

Paste-ready text for the Google verification form. Each block answers "how will you use this scope" and "why a narrower scope
isn't enough".

---

## App summary (for "Tell us about your app")

Plicbox is a desktop email client for Windows (https://plicbox.bogdanrosu.net/). It combines mail, calendar, contacts and tasks for
the user's own accounts in one window, similar to Thunderbird or eM Client. It is a local client: it has no backend servers.
Google user data is transferred only between Google's APIs and the user's own PC, stored locally in the user's Windows profile,
and never sent to the developer or any third party. OAuth uses the installed-app flow with a loopback redirect on the user's PC,
and tokens are stored in Windows Credential Manager. There are no analytics, ads or AI features, and Google user data is not
used to train models.

---

## https://mail.google.com/

Plicbox is a full email client for Gmail. It connects to Gmail over IMAP and SMTP with OAuth (XOAUTH2) to download the user's mail and folders/labels for reading and offline search, keep read, flagged, moved, archived and deleted state in sync both ways, receive new mail instantly with IMAP IDLE, and send the mail the user writes. It also reads the user's verified "Send mail as" addresses (Gmail API users.settings.sendAs.list) so they can choose them as the From address.

Why narrower scopes aren't enough: Google's IMAP and SMTP servers accept OAuth only with the https://mail.google.com/ scope. gmail.readonly, gmail.modify, gmail.compose and gmail.send work only with the Gmail REST API, not IMAP/SMTP. Plicbox also needs to permanently delete messages when the user empties Trash or Spam, which gmail.modify doesn't allow.

---

## https://www.googleapis.com/auth/calendar

Plicbox shows the user's Google calendars (day, week, month and agenda views) with each calendar's name and colour. It creates,
edits, moves and deletes events the user makes in the app, and answers meeting invitations (accept, tentative, decline). Changes
sync both ways with Google Calendar.

Why narrower scopes aren't enough: calendar.readonly can't save events or invitation replies. calendar.events alone doesn't give
the list of the user's calendars with their names, colours and access roles, which the app needs to show calendars and decide
which ones the user can edit.

---

## https://www.googleapis.com/auth/contacts

Plicbox has a Contacts module that shows the user's Google contacts and lets them add, edit and delete contacts (for example
"Save sender to contacts" from an email). Changes sync both ways with Google Contacts.

Why narrower scopes aren't enough: contacts.readonly can't save new or edited contacts.

---

## https://www.googleapis.com/auth/contacts.other.readonly

When the user types a recipient in a new email, Plicbox suggests addresses of people they have written to before, from Google's
"Other contacts". This data is only read, never changed, and is used only for these suggestions and for showing the sender's name.

Why narrower scopes aren't enough: this is already the narrowest scope that gives access to "Other contacts".

---

## https://www.googleapis.com/auth/tasks

Plicbox has a Tasks module that shows the user's Google task lists, and lets them add, complete, edit and delete tasks, including
turning an email into a task. Changes sync both ways with Google Tasks.

Why narrower scopes aren't enough: tasks.readonly can't save tasks.

---

## openid, email, profile

Used once at sign-in to identify which Google account was added (its verified email address) and to show the user's name on
the account in the sidebar.
