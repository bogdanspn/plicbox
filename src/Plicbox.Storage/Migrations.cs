namespace Plicbox.Storage;

/// <summary>Append-only list of schema migrations. Never edit a shipped entry; add a new one.</summary>
internal static class Migrations
{
    public static readonly string[] All =
    [
        """
        CREATE TABLE accounts (
            id            INTEGER PRIMARY KEY,
            display_name  TEXT NOT NULL,
            email         TEXT NOT NULL,
            provider      INTEGER NOT NULL,
            auth          INTEGER NOT NULL,
            username      TEXT NOT NULL,
            imap_host     TEXT NOT NULL,
            imap_port     INTEGER NOT NULL,
            smtp_host     TEXT NOT NULL,
            smtp_port     INTEGER NOT NULL,
            dav_url       TEXT,
            color         TEXT NOT NULL,
            signature     TEXT
        );

        CREATE TABLE folders (
            id              INTEGER PRIMARY KEY,
            account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            path            TEXT NOT NULL,
            name            TEXT NOT NULL,
            role            INTEGER NOT NULL,
            uid_validity    INTEGER NOT NULL DEFAULT 0,
            highest_modseq  INTEGER NOT NULL DEFAULT 0,
            unread_count    INTEGER NOT NULL DEFAULT 0,
            total_count     INTEGER NOT NULL DEFAULT 0,
            UNIQUE (account_id, path)
        );

        CREATE TABLE messages (
            id               INTEGER PRIMARY KEY,
            account_id       INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            folder_id        INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            uid              INTEGER NOT NULL,
            message_id       TEXT,
            in_reply_to      TEXT,
            thread_key       TEXT,
            subject          TEXT NOT NULL,
            from_name        TEXT NOT NULL,
            from_address     TEXT NOT NULL,
            to_list          TEXT NOT NULL,
            date             INTEGER NOT NULL,
            preview          TEXT NOT NULL,
            is_read          INTEGER NOT NULL,
            is_flagged       INTEGER NOT NULL,
            has_attachments  INTEGER NOT NULL,
            size             INTEGER NOT NULL,
            UNIQUE (folder_id, uid)
        );
        CREATE INDEX ix_messages_folder_date ON messages(folder_id, date DESC);
        CREATE INDEX ix_messages_thread ON messages(account_id, thread_key);

        CREATE VIRTUAL TABLE messages_fts USING fts5(
            subject, from_name, from_address, to_list, body,
            content = '', contentless_delete = 1, tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
            DELETE FROM messages_fts WHERE rowid = old.id;
        END;
        """,

        """
        ALTER TABLE folders ADD COLUMN last_uid INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE folders ADD COLUMN is_synced INTEGER NOT NULL DEFAULT 1;

        CREATE TABLE pending_ops (
            id           INTEGER PRIMARY KEY,
            account_id   INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            message_id   INTEGER NOT NULL,
            folder_path  TEXT,
            uid          INTEGER NOT NULL DEFAULT 0,
            target_path  TEXT,
            attempts     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_pending_ops_account ON pending_ops(account_id, id);
        """,

        """
        ALTER TABLE accounts ADD COLUMN label TEXT;
        ALTER TABLE accounts ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN bcc_address TEXT;
        UPDATE accounts SET sort_order = id;
        UPDATE accounts SET is_default = 1 WHERE id = (SELECT MIN(id) FROM accounts);

        ALTER TABLE folders ADD COLUMN is_virtual INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_messages_message_id ON messages(account_id, message_id);
        """,

        """
        ALTER TABLE accounts ADD COLUMN imap_security INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN smtp_security INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN sync_period_days INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN offline_download INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN aliases TEXT;
        ALTER TABLE accounts ADD COLUMN folder_overrides TEXT;
        ALTER TABLE accounts ADD COLUMN ignored_paths TEXT;
        -- Lets unread/flag counts be computed from the index alone.
        CREATE INDEX ix_messages_folder_flags ON messages(folder_id, is_read, is_flagged);
        """,

        """
        CREATE TABLE trusted_senders (
            address  TEXT PRIMARY KEY COLLATE NOCASE
        );
        """,

        """
        CREATE TABLE calendars (
            id           INTEGER PRIMARY KEY,
            account_id   INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id    TEXT NOT NULL,
            name         TEXT NOT NULL,
            color        TEXT NOT NULL,
            is_visible   INTEGER NOT NULL DEFAULT 1,
            is_primary   INTEGER NOT NULL DEFAULT 0,
            access_role  TEXT NOT NULL,
            time_zone    TEXT,
            UNIQUE (account_id, remote_id)
        );

        -- One row per occurrence; all-day events keep their dates as text so no time zone can shift them.
        CREATE TABLE events (
            id               INTEGER PRIMARY KEY,
            calendar_id      INTEGER NOT NULL REFERENCES calendars(id) ON DELETE CASCADE,
            remote_id        TEXT NOT NULL,
            series_id        TEXT,
            title            TEXT NOT NULL,
            description      TEXT,
            location         TEXT,
            start_utc        INTEGER NOT NULL,
            end_utc          INTEGER NOT NULL,
            is_all_day       INTEGER NOT NULL,
            start_date       TEXT,
            end_date         TEXT,
            time_zone        TEXT,
            status           TEXT NOT NULL,
            is_busy          INTEGER NOT NULL,
            organizer_email  TEXT,
            organizer_name   TEXT,
            attendees        TEXT,
            meeting_url      TEXT,
            html_link        TEXT,
            color            TEXT,
            reminder_minutes INTEGER,
            recurrence       TEXT,
            updated_utc      INTEGER NOT NULL,
            UNIQUE (calendar_id, remote_id)
        );
        CREATE INDEX ix_events_range ON events(calendar_id, start_utc, end_utc);
        """,
        """
        -- Who a message went to, by address ("Name <a@x>, b@y"): to_list only kept names.
        ALTER TABLE messages ADD COLUMN recipients TEXT;
        CREATE INDEX ix_messages_from ON messages(from_address COLLATE NOCASE);

        CREATE TABLE contacts (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id     TEXT,
            etag          TEXT,
            source        INTEGER NOT NULL,
            display_name  TEXT NOT NULL,
            given_name    TEXT,
            family_name   TEXT,
            organization  TEXT,
            job_title     TEXT,
            emails        TEXT NOT NULL,
            phones        TEXT NOT NULL,
            websites      TEXT NOT NULL,
            birthday      TEXT,
            notes         TEXT,
            photo_url     TEXT,
            address       TEXT,
            is_favorite   INTEGER NOT NULL DEFAULT 0,
            updated_utc   INTEGER NOT NULL,
            UNIQUE (account_id, remote_id)
        );
        -- Lookup by address (autocomplete, "is this sender a contact?").
        CREATE TABLE contact_emails (
            contact_id  INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            address     TEXT NOT NULL COLLATE NOCASE
        );
        CREATE INDEX ix_contact_emails ON contact_emails(address);
        CREATE TABLE contact_sync (
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            source      INTEGER NOT NULL,
            sync_token  TEXT,
            PRIMARY KEY (account_id, source)
        );
        """,
        """
        ALTER TABLE contacts ADD COLUMN nickname TEXT;
        ALTER TABLE contacts ADD COLUMN department TEXT;
        ALTER TABLE contacts ADD COLUMN addresses TEXT;
        """,
        """
        CREATE TABLE task_lists (
            id          INTEGER PRIMARY KEY,
            account_id  INTEGER REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id   TEXT,
            title       TEXT NOT NULL,
            updated_utc INTEGER NOT NULL,
            UNIQUE (account_id, remote_id)
        );
        CREATE TABLE tasks (
            id            INTEGER PRIMARY KEY,
            list_id       INTEGER NOT NULL REFERENCES task_lists(id) ON DELETE CASCADE,
            remote_id     TEXT,
            parent_remote TEXT,
            title         TEXT NOT NULL,
            notes         TEXT,
            due           TEXT,
            completed     INTEGER NOT NULL DEFAULT 0,
            completed_utc INTEGER,
            position      TEXT,
            updated_utc   INTEGER NOT NULL,
            message_ref   TEXT,
            web_link      TEXT,
            UNIQUE (list_id, remote_id)
        );
        CREATE INDEX ix_tasks_due ON tasks(completed, due);
        """,
        // Changed here but not yet accepted by Google (offline, or the call failed): sent on the next sync.
        "ALTER TABLE tasks ADD COLUMN dirty INTEGER NOT NULL DEFAULT 0;",
        // Gmail's Important label, kept by sync.
        "ALTER TABLE messages ADD COLUMN is_important INTEGER NOT NULL DEFAULT 0;",
        // Outgoing sign-in of its own (or none), for servers that want that.
        """
        ALTER TABLE accounts ADD COLUMN smtp_auth INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE accounts ADD COLUMN smtp_username TEXT;
        """,
        // How many messages the server had in the folder at the last sync (-1: not known yet): with the mod-sequence it
        // tells an untouched folder at a glance.
        "ALTER TABLE folders ADD COLUMN server_count INTEGER NOT NULL DEFAULT -1;",
        // Calendars kept only on this computer have no account. SQLite can't drop NOT NULL in place, so the table is
        // rebuilt (Database.Migrate turns foreign keys off meanwhile, or dropping it would take every event along).
        // Their repeating events are kept whole in local_series and written out as occurrences for the dates in view.
        """
        CREATE TABLE calendars_new (
            id           INTEGER PRIMARY KEY,
            account_id   INTEGER REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id    TEXT NOT NULL,
            name         TEXT NOT NULL,
            color        TEXT NOT NULL,
            is_visible   INTEGER NOT NULL DEFAULT 1,
            is_primary   INTEGER NOT NULL DEFAULT 0,
            access_role  TEXT NOT NULL,
            time_zone    TEXT,
            UNIQUE (account_id, remote_id)
        );
        INSERT INTO calendars_new (id, account_id, remote_id, name, color, is_visible, is_primary, access_role, time_zone)
            SELECT id, account_id, remote_id, name, color, is_visible, is_primary, access_role, time_zone FROM calendars;
        DROP TABLE calendars;
        ALTER TABLE calendars_new RENAME TO calendars;

        CREATE TABLE local_series (
            calendar_id  INTEGER NOT NULL REFERENCES calendars(id) ON DELETE CASCADE,
            series_id    TEXT NOT NULL,
            first        TEXT NOT NULL,
            skipped      TEXT,
            changed      TEXT,
            PRIMARY KEY (calendar_id, series_id)
        );
        """,
        // Drafts saved here first: the queued upload (and deletion) finds a draft's server copies by its Message-ID.
        "ALTER TABLE pending_ops ADD COLUMN header_message_id TEXT;",
        // Changes to Google calendars made offline, already shown here, sent on the next sync (oldest first).
        """
        CREATE TABLE calendar_changes (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            calendar_id  INTEGER NOT NULL REFERENCES calendars(id) ON DELETE CASCADE,
            change       TEXT NOT NULL
        );
        """,
        // The same for Google contacts: what to send, by the contact's row here (or, for a deletion, Google's id).
        """
        CREATE TABLE contact_changes (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id   INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            contact_id   INTEGER NOT NULL,
            remote_id    TEXT
        );
        """,
        // Offline mail: how far back it goes (existing accounts keep what they had, now with a year as the reach), and
        // which messages are already downloaded, so the background download finds the rest without looking at every file.
        """
        ALTER TABLE accounts ADD COLUMN offline_days INTEGER NOT NULL DEFAULT 365;
        ALTER TABLE messages ADD COLUMN has_body INTEGER NOT NULL DEFAULT 0;
        """,
    ];
}
