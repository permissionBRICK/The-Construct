set -u
export LC_ALL=C
d={{dir}}
# stdin: "<action>\t<base64 path>[\t<base64 pattern>...]" lines, action redact|delete.
# The patterns ARE the secrets: they are copied into a private directory on the spool's
# tmpfs and handed to python3 by file name, never through argv or the environment.
[ -d "$d" ] || install -d -m 0700 -- "$d" 2>/dev/null
umask 077
w=$(mktemp -d "$d/clean.XXXXXX" 2>/dev/null) || { printf 'vault-clean: cannot create a work directory\n' >&2; exit 1; }
trap 'rm -rf -- "$w"' EXIT
trap 'exit 1' HUP INT TERM PIPE
cat >"$w/in" || exit 1

gone() {
  local p
  p=$(printf '%s' "$1" | base64 -d 2>/dev/null && printf x) || p=""
  case "$p" in /*x) p=${p%x} ;; *) printf 'R\tfailed\t%s\t0\tbad path\n' "$1"; return ;; esac
  if [ ! -e "$p" ] && [ ! -L "$p" ]; then
    printf 'R\tmissing\t%s\t0\n' "$1"
  elif [ -d "$p" ] && [ ! -L "$p" ]; then
    printf 'R\tfailed\t%s\t0\tis a directory\n' "$1"
  elif rm -f -- "$p" 2>/dev/null; then
    printf 'R\tok\t%s\t0\n' "$1"
  else
    printf 'R\tfailed\t%s\t0\tcannot delete\n' "$1"
  fi
}
# Without a usable python3 the deletes still happen; a redaction cannot be done safely
# in shell (same-length, in place, SQLite-aware), so say so per file instead.
plain() {
  local skip=$1 why=$2 k=0 action b64 rest
  while IFS=$'\t' read -r action b64 rest || [ -n "${action:-}" ]; do
    k=$((k + 1))
    [ "$k" -gt "$skip" ] || continue
    b64=${b64%$'\r'}
    case "$action" in
      '') ;;
      delete) gone "$b64" ;;
      redact) printf 'R\tfailed\t%s\t0\t%s\n' "$b64" "$why" ;;
      *) printf 'R\tfailed\t%s\t0\tunknown action\n' "$b64" ;;
    esac
  done <"$w/in"
}
if ! command -v python3 >/dev/null 2>&1; then
  plain 0 'python3 missing'
  printf 'DONE\n'
  exit 0
fi

IFS= read -r -d '' py <<'PY'
import base64, mmap, os, re, stat, sys

work, start = sys.argv[1], int(sys.argv[2])
out = sys.stdout.buffer
AUX = (b'-wal', b'-shm', b'-journal')
MAGIC = b'SQLite format 3\x00'
GONE = (FileNotFoundError, NotADirectoryError)
# Rebuild (VACUUM) a database only up to this size; see scrub_sqlite.
VACUUM_LIMIT = 256 * 1024 * 1024
# Progress for the shell wrapper: the number of input lines fully handled. If this process
# dies on a file (SIGBUS when someone truncates a file we have mapped), the wrapper reports
# that one file and restarts us behind it instead of losing the rest of the batch.
progress = os.open(os.path.join(work, 'at'), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)


def mark(k):
    os.pwrite(progress, b'%d\n' % k, 0)


def emit(status, field, count, why=''):
    line = b'R\t' + status.encode() + b'\t' + field + b'\t' + str(count).encode()
    if why:
        line += b'\t' + ' '.join(str(why).split())[:120].encode('ascii', 'replace')
    out.write(line + b'\n')
    out.flush()


def describe(e):
    # Errno text, SQLite's own message, or just the kind of failure: never echo data back.
    if isinstance(e, OSError) and e.strerror:
        return e.strerror
    if type(e).__module__.startswith('sqlite3'):
        return str(e)
    return type(e).__name__


def contains(path, pats):
    # The same raw-byte test vault-scan.sh uses, so "ok" means a rescan finds nothing.
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        size = os.fstat(fd).st_size
        if size == 0:
            return False
        with mmap.mmap(fd, size, prot=mmap.PROT_READ) as mm:
            return any(mm.find(p) != -1 for p in pats)
    finally:
        os.close(fd)


def patch(fd, pats):
    # Same length, in place, through a shared mapping: agents keep these files open and
    # append to them while we work, so the file is never truncated, renamed or recreated
    # (their writes would land in an orphaned inode), and a multi-GB log is never read
    # into memory. Longest pattern first, so a short variant cannot break up a longer one
    # before it is masked. Text appended meanwhile gets another (bounded) pass.
    total, done, longest = 0, 0, len(pats[0])
    for _ in range(4):
        size = os.fstat(fd).st_size
        if size <= done:
            break
        with mmap.mmap(fd, size) as mm:
            lo, hit = max(0, done - longest + 1), False
            for p in pats:
                mask = b'*' * len(p)
                i = mm.find(p, lo)
                while i != -1:
                    mm[i:i + len(p)] = mask
                    total, hit = total + 1, True
                    i = mm.find(p, i + len(p))
            if hit:
                mm.flush()
        done = size
    return total


def qi(name):
    return '"' + name.replace('"', '""') + '"'


def textual(decl):
    # SQLite's affinity rules, in their order: INTEGER, REAL and NUMERIC columns hold numbers,
    # and a full scan of them for a secret is wasted time on a live database. TEXT and BLOB
    # (which includes "no declared type") are scanned. JSON is NUMERIC by those rules but in
    # practice holds text payloads, exactly where a pasted secret ends up.
    t = (decl or '').upper()
    if 'JSON' in t:
        return True
    if 'INT' in t:
        return False
    return 'CHAR' in t or 'CLOB' in t or 'TEXT' in t or 'BLOB' in t or not t.strip()


def columns(con, sqlite3, table):
    # table_xinfo also lists hidden columns: 1 = virtual-table internals, 2/3 = generated
    # columns, which cannot be UPDATEd and follow from the columns we do rewrite. Older
    # SQLite silently returns nothing for the unknown pragma; fall back to table_info.
    for pragma in ('table_xinfo', 'table_info'):
        try:
            rows = con.execute('PRAGMA %s(%s)' % (pragma, qi(table))).fetchall()
        except sqlite3.Error:
            return []
        if rows:
            return [r[1] for r in rows if (len(r) < 7 or r[6] == 0) and textual(r[2])]
    return []


def update_sql(table, column, n):
    # One statement per column for all patterns: :p0..:pN are the patterns (longest first,
    # so the innermost replace() handles it before a shorter variant can split it), :m0..:mN
    # their masks. Blobs are rewritten as blobs, everything else through replace() as text.
    c = qi(column)

    def masked(x):
        for i in range(n):
            x = 'replace(%s, :p%d, :m%d)' % (x, i, i)
        return x
    hit = ' OR '.join('instr(CAST(%s AS TEXT), :p%d) > 0' % (c, i) for i in range(n))
    return ("UPDATE %s SET %s = CASE WHEN typeof(%s) = 'blob' THEN CAST(%s AS BLOB) ELSE %s END WHERE %s"
            % (qi(table), c, c, masked('CAST(%s AS TEXT)' % c), masked(c), hit))


def rollback(con):
    try:
        if con.in_transaction:
            con.execute('ROLLBACK')
    except Exception:
        pass


def scrub_db(uri, sqlite3, pats):
    con = sqlite3.connect(uri, uri=True, timeout=30, isolation_level=None)
    changed, errors, status, why = 0, [], 'ok', ''
    # Bound as text where possible, so SQLite converts them to the database's encoding.
    values = dict()
    for i, p in enumerate(pats):
        try:
            v = p.decode('utf-8')
        except UnicodeDecodeError:
            v = p
        values['p%d' % i] = v
        values['m%d' % i] = ('*' if isinstance(v, str) else b'*') * len(v)
    try:
        # Freed cells and pages are zeroed instead of keeping the old bytes around.
        con.execute('PRAGMA secure_delete=ON')
        rows = con.execute("SELECT name, coalesce(sql, '') FROM sqlite_master WHERE type = 'table'"
                           " AND lower(substr(name, 1, 7)) <> 'sqlite_'").fetchall()
        virtual = [n for n, s in rows if re.match(r'\s*create\s+virtual\s+table', s, re.I)]
        # Shadow tables (<vtab>_content, _data, ...) belong to their virtual table: writing
        # them directly corrupts its index. Updating the virtual table rewrites them for us.
        tables = [n for n, _ in rows if n in virtual or not any(n.startswith(v + '_') for v in virtual)]
        fts = [n for n, s in rows if n in virtual and re.search(r'using\s+fts[345]\b', s, re.I)]
        # The database usually belongs to a running app (a session store, an agent's state).
        # One short write transaction per table lets its writers in between; the busy
        # timeout makes us queue behind them instead of failing.
        for t in tables:
            cols = columns(con, sqlite3, t)
            if not cols:
                continue
            done = 0
            try:
                con.execute('BEGIN IMMEDIATE')
                for c in cols:
                    try:
                        done += max(0, con.execute(update_sql(t, c, len(pats)), values).rowcount)
                    except sqlite3.Error:
                        # One odd column (STRICT type, UNIQUE clash, read-only virtual table)
                        # must not stop the rest, unless the error ended the transaction.
                        if not con.in_transaction:
                            raise
                con.execute('COMMIT')
                changed += done
            except sqlite3.Error as e:
                rollback(con)
                errors.append(describe(e))
        if changed:
            # Full-text indexes keep deleted terms in old segments until they are merged.
            for t in fts:
                try:
                    con.execute('BEGIN IMMEDIATE')
                    con.execute("INSERT INTO %s(%s) VALUES('optimize')" % (qi(t), qi(t)))
                    con.execute('COMMIT')
                except sqlite3.Error:
                    rollback(con)
        if errors:
            status, why = ('partial' if changed else 'failed'), errors[0]
        # In WAL mode the old pages stay in the database file (and old frames in the -wal)
        # until a checkpoint. Best effort: a reader holding an old snapshot keeps it busy,
        # and the scrub is then only partial. Do not hold the app up for 30 s over it.
        con.execute('PRAGMA busy_timeout=5000')
        try:
            r = con.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone()
            if r and r[0] == 1 and status == 'ok':
                status, why = 'partial', 'database busy, write-ahead log not checkpointed'
        except sqlite3.Error as e:
            if status == 'ok':
                status, why = 'partial', 'checkpoint failed: ' + describe(e)
    finally:
        rollback(con)
        con.close()
    return changed, status, why


def scrub_sqlite(path, pats):
    try:
        import sqlite3
    except ImportError:
        return 'failed', 0, 'python3 has no sqlite3 module'
    from urllib.parse import quote
    # mode=rw: a database that vanished meanwhile must not be recreated as an empty file.
    uri = 'file://' + quote(path) + '?mode=rw'
    changed, status, why = scrub_db(uri, sqlite3, pats)
    # Checked only with no connection open: closing ANY descriptor of a database file drops
    # the POSIX locks this process holds on it.
    if status == 'ok' and contains(path, pats):
        # Rows deleted before secure_delete was on leave their bytes in free pages. Rebuild
        # the file once, but only while that is quick: VACUUM holds the write lock for the
        # whole rebuild, and a big live database would stall its app for that long.
        if os.stat(path).st_size > VACUUM_LIMIT:
            return 'partial', changed, 'old copies remain in free pages; database too large to rebuild while in use'
        con = sqlite3.connect(uri, uri=True, timeout=30, isolation_level=None)
        try:
            con.execute('PRAGMA secure_delete=ON')
            con.execute('VACUUM')
            con.execute('PRAGMA busy_timeout=5000')
            con.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone()
        except sqlite3.Error:
            pass
        finally:
            con.close()
        if contains(path, pats):
            status, why = 'partial', 'secret still in the database file'
    return status, changed, why


def redact(field, path, encoded):
    pats = []
    for f in encoded:
        try:
            p = base64.b64decode(f, validate=True)
        except ValueError:
            return emit('failed', field, 0, 'bad pattern')
        if p and p not in pats:
            pats.append(p)
    if not pats:
        return emit('failed', field, 0, 'no patterns')
    pats.sort(key=len, reverse=True)
    # The host scrubs the database a side file belongs to; touching the -wal/-journal
    # directly would corrupt it.
    if path.endswith(AUX):
        return emit('skipped', field, 0)
    try:
        if not stat.S_ISREG(os.lstat(path).st_mode):
            return emit('failed', field, 0, 'not a regular file')
        fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    except GONE:
        return emit('missing', field, 0)
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            return emit('failed', field, 0, 'not a regular file')
        if os.pread(fd, 16, 0) != MAGIC:
            return emit('ok', field, patch(fd, pats))
    finally:
        os.close(fd)
    status, changed, why = scrub_sqlite(path, pats)
    emit(status, field, changed, why)


def delete(field, path):
    try:
        if stat.S_ISDIR(os.lstat(path).st_mode):
            return emit('failed', field, 0, 'is a directory')
        os.unlink(path)
    except GONE:
        return emit('missing', field, 0)
    emit('ok', field, 0)


mark(start)
with open(os.path.join(work, 'in'), 'rb') as src:
    for k, raw in enumerate(src):
        if k < start:
            continue
        fields = raw.rstrip(b'\r\n').split(b'\t')
        field = fields[1] if len(fields) > 1 else b''
        if fields[0]:
            try:
                path = base64.b64decode(field, validate=True)
                if not path.startswith(b'/') or b'\0' in path:
                    raise ValueError
            except ValueError:
                emit('failed', field, 0, 'bad path')
            else:
                try:
                    if fields[0] == b'delete':
                        delete(field, path)
                    elif fields[0] == b'redact':
                        redact(field, path, fields[2:])
                    else:
                        emit('failed', field, 0, 'unknown action')
                except Exception as e:
                    emit('failed', field, 0, describe(e))
        mark(k + 1)
PY

start=0
while :; do
  rm -f -- "$w/at"
  if python3 -c "$py" "$w" "$start" </dev/null; then break; fi
  at=$(head -n 1 -- "$w/at" 2>/dev/null)
  case "$at" in
    '' | *[!0-9]*)
      # python3 is there but would not even start: handle the rest like python3 missing.
      plain "$start" 'python3 failed'
      break
      ;;
  esac
  # python3 died on line $at (most likely SIGBUS: another process truncated a file while
  # it was mapped). Report that file and carry on behind it.
  b64=$(sed -n "$((at + 1))p" -- "$w/in" | cut -s -f2)
  b64=${b64%$'\r'}
  [ -z "$b64" ] || printf 'R\tfailed\t%s\t0\tinterrupted while redacting\n' "$b64"
  start=$((at + 1))
done
printf 'DONE\n'
exit 0
