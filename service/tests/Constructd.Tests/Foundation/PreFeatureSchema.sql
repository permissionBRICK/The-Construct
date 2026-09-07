
            CREATE TABLE IF NOT EXISTS users (
                name                TEXT PRIMARY KEY COLLATE NOCASE,
                role                TEXT NOT NULL,
                max_vms             INTEGER NOT NULL,
                created             TEXT NOT NULL,
                allow_host_forwards INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tokens (
                id         TEXT PRIMARY KEY,
                user_name  TEXT NOT NULL COLLATE NOCASE,
                token_hash TEXT NOT NULL UNIQUE,
                created    TEXT NOT NULL,
                last_used  TEXT NULL,
                label      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tokens_user ON tokens (user_name);

            CREATE TABLE IF NOT EXISTS vms (
                name                 TEXT PRIMARY KEY COLLATE NOCASE,
                owner                TEXT NOT NULL COLLATE NOCASE,
                cpu                  INTEGER NOT NULL,
                ram_gb               INTEGER NOT NULL,
                disk_gb              INTEGER NOT NULL,
                created              TEXT NOT NULL,
                state                TEXT NOT NULL,
                ssh_forward_port     INTEGER NULL,
                vm_token_hash        TEXT NULL,
                idle_timeout_minutes INTEGER NOT NULL,
                idle_action          TEXT NOT NULL,
                deleting             INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_vms_owner ON vms (owner);
            CREATE INDEX IF NOT EXISTS ix_vms_token ON vms (vm_token_hash);

            CREATE TABLE IF NOT EXISTS activity (
                vm_name     TEXT PRIMARY KEY COLLATE NOCASE,
                busy        INTEGER NOT NULL,
                reasons     TEXT NOT NULL,
                reported_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS forwards (
                id              TEXT PRIMARY KEY,
                vm_name         TEXT NOT NULL COLLATE NOCASE,
                vm_port         INTEGER NOT NULL,
                public_port     INTEGER NULL,
                target          TEXT NOT NULL,
                label           TEXT NOT NULL,
                created         TEXT NOT NULL,
                ack_status      TEXT NULL,
                ack_local_port  INTEGER NULL,
                ack_host_label  TEXT NULL,
                ack_message     TEXT NULL,
                ack_at          TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_forwards_vm ON forwards (vm_name);

            CREATE TABLE IF NOT EXISTS audit (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                actor   TEXT NOT NULL,
                action  TEXT NOT NULL,
                target  TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail  TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id       TEXT PRIMARY KEY,
                kind     TEXT NOT NULL,
                vm_name  TEXT NULL COLLATE NOCASE,
                owner    TEXT NOT NULL COLLATE NOCASE,
                state    TEXT NOT NULL,
                progress TEXT NOT NULL,
                result   TEXT NULL,
                error    TEXT NULL,
                created  TEXT NOT NULL,
                finished TEXT NULL
            );
            