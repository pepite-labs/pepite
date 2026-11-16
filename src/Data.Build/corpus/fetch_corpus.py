#!/usr/bin/env python3
"""Fetch, pin and verify the corpus inputs listed in Data.Build/data.lock.json (plan corpus-light, 0.2–0.4).

Every input of the n-gram build and of the held-out sets has one lock entry: id, url, version / date, SHA-256,
bytes, licence, credit, use (train / heldout / test-only / private / selection) and public. The bulk files live in
the work folder named by the environment variable PEPITE_CORPUS_WORK (no default: the build never guesses a path):

    <work>/raw/<entry file>          downloaded inputs (ZIM, Gutendex snapshot, Gutenberg books)
    <work>/local-paths.json          inputs that already sit elsewhere on this machine (« adopt »); never committed

Sub-commands (run with ``python -I``; stdlib only):

    fetch   [--ids a,b] [--group g]   download the pinned entries that are missing; resume a partial download
                                      (<file>.part + HTTP Range); never overwrite; stop on a SHA-256 mismatch
    verify  [--ids a,b] [--group g]   re-hash every present input (and its archive); exit 1 on a mismatch or a gap
    pin-new [--ids a,b] [--group g]   download (or read the adopted copy of) every entry whose sha256 is null and
                                      record its SHA-256 and size in the lock — run it once, after reviewing the input
    adopt   <id> <path> [--archive]   use an existing local copy of an input (or of its archive) instead of a download
    books                             Gutendex snapshot -> pd-books.tsv (eligibility, held-out and training picks) and
                                      one lock entry per eligible book (pinned later by « pin-new --group gutenberg »)

Network rules: one request at a time, at least 2 s apart, User-Agent below; Gutenberg files come from the
gutenberg.pglaf.org mirror (the numbered tree, whose files are stable; « cache/epub » is regenerated upstream).
"""
import argparse
import datetime as _dt
import hashlib
import html.parser
import json
import os
import random
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DATA_BUILD = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(DATA_BUILD))          # repository root
LOCK = os.path.join(DATA_BUILD, "data.lock.json")
BOOKS_TSV = os.path.join(HERE, "pd-books.tsv")
USER_AGENT = "Pepite-Data.Build (+https://github.com/pepite-labs/pepite)"
PAUSE = 2.0                       # seconds between two requests (plan 0.3)
PGLAF = "https://gutenberg.pglaf.org/"
SEED = 20261115                   # plan 0.4: held-out drawing
DEATH_LIMIT = 1955                # owner Q5: authors and translators died in 1955 or earlier
HELDOUT_BOOKS = 3
LIGHT_TOKENS = 1_200_000          # plan 0.4: about 16 books, about 1.2 M tokens
HELDOUT_MIN_TOKENS = 20_000       # a held-out book must be able to give E1 its 5 000 evaluated words


class LockMismatch(Exception):
    """A file does not match its pinned SHA-256 (or size)."""


# ---------------------------------------------------------------- paths and lock

def work_dir(arg=None):
    w = arg or os.environ.get("PEPITE_CORPUS_WORK")
    if not w:
        raise SystemExit("set PEPITE_CORPUS_WORK (or --work) to the bulk-data work folder; there is no default")
    return w


def load_lock(path=LOCK):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_lock(lock, path=LOCK):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(lock, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def entry(lock, ident):
    for e in lock["inputs"]:
        if e["id"] == ident:
            return e
    raise KeyError(ident)


def select(lock, ids=None, group=None):
    want = set(ids.split(",")) if ids else None
    out = [e for e in lock["inputs"] if (want is None or e["id"] in want) and (group is None or e.get("group") == group)]
    if want and len(out) != len(want):
        missing = want - {e["id"] for e in out}
        raise SystemExit("unknown lock ids: %s" % ", ".join(sorted(missing)))
    return out


def local_paths(work):
    p = os.path.join(work, "local-paths.json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def save_local_paths(work, paths):
    os.makedirs(work, exist_ok=True)
    with open(os.path.join(work, "local-paths.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(paths, f, ensure_ascii=False, indent=2, sort_keys=True)
        f.write("\n")


def resolve(e, work, archive=False, paths=None):
    """Local path of an input (or of its archive): a repository file, an adopted copy, else <work>/raw/<file>."""
    paths = local_paths(work) if paths is None else paths
    key = e["id"] + (":archive" if archive else "")
    rec = e["archive"] if archive else e
    if not archive and rec.get("repo_path"):
        return os.path.join(REPO, *rec["repo_path"].split("/"))
    if key in paths:
        return paths[key]
    return os.path.join(work, "raw", *rec["file"].split("/"))


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check_pin(path, rec):
    """Raise LockMismatch unless path has the pinned SHA-256 (and size, when pinned)."""
    got = sha256_file(path)
    if got != rec["sha256"]:
        raise LockMismatch("SHA-256 mismatch for %s: %s (lock %s)" % (path, got, rec["sha256"]))
    size = os.path.getsize(path)
    if rec.get("bytes") is not None and size != rec["bytes"]:
        raise LockMismatch("size mismatch for %s: %d (lock %d)" % (path, size, rec["bytes"]))
    return got


# ---------------------------------------------------------------- network

class Throttle:
    """At most one request every PAUSE seconds (the clock and sleep are injectable for the tests)."""

    def __init__(self, pause=PAUSE, clock=time.monotonic, sleep=time.sleep):
        self.pause, self.clock, self.sleep, self.last = pause, clock, sleep, None

    def wait(self):
        if self.last is not None:
            left = self.pause - (self.clock() - self.last)
            if left > 0:
                self.sleep(left)
        self.last = self.clock()


def _request(url, extra=None):
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    for k, v in (extra or {}).items():
        req.add_header(k, v)
    return req


def get_bytes(url, throttle, opener=urllib.request.urlopen, tries=3, timeout=300):
    """GET a small document (an API page, a directory listing); Gutendex can take minutes per page, so a timeout or a
    network error is retried (one request at a time, still throttled)."""
    for k in range(tries):
        throttle.wait()
        try:
            with opener(_request(url), timeout=timeout) as r:
                return r.read()
        except (OSError, TimeoutError) as e:     # URLError and socket timeouts are OSErrors
            if k == tries - 1:
                raise
            print("  retry %s (%s)" % (url, e), file=sys.stderr)


def download(url, dest, sha, throttle, opener=urllib.request.urlopen):
    """Download url to dest through dest.part, resuming a partial .part with an HTTP Range request.

    Never overwrites: an existing dest is only checked (LockMismatch when it differs from the pin). With a pin, a
    finished download that does not match is deleted and LockMismatch raised; without one (pin-new) the file is kept
    and its SHA-256 returned."""
    if os.path.exists(dest):
        if sha is None:
            return sha256_file(dest)
        return check_pin(dest, {"sha256": sha})
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    part = dest + ".part"
    have = os.path.getsize(part) if os.path.exists(part) else 0
    throttle.wait()
    with opener(_request(url, {"Range": "bytes=%d-" % have} if have else None), timeout=120) as r:
        mode = "ab" if have and getattr(r, "status", 200) == 206 else "wb"
        with open(part, mode) as f:
            shutil.copyfileobj(r, f, 1 << 20)
    got = sha256_file(part)
    if sha is not None and got != sha:
        os.remove(part)
        raise LockMismatch("SHA-256 mismatch for %s: %s (lock %s); the download was deleted" % (url, got, sha))
    os.replace(part, dest)
    return got


def safe_member(name):
    """An archive member name we accept: relative, no « .. » part, no drive, no backslash tricks."""
    if not name or name.startswith(("/", "\\")) or re.match(r"^[A-Za-z]:", name):
        return False
    parts = re.split(r"[\/]+", name)
    return ".." not in parts


def extract_member(archive, member, dest):
    """Extract one file of a pinned archive (.zip, .tar.gz / .tgz / .tar, or a single-stream .bz2 where member is None)
    to dest, through dest.part. Refuses unsafe member names and anything but a regular file; never extracts a whole
    archive."""
    import bz2
    import tarfile
    import zipfile
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    part = dest + ".part"
    low = archive.lower()
    if low.endswith(".bz2") and not low.endswith(".tar.bz2"):
        with bz2.open(archive, "rb") as src, open(part, "wb") as out:
            shutil.copyfileobj(src, out, 1 << 20)
    elif low.endswith(".zip"):
        if not safe_member(member):
            raise LockMismatch("unsafe archive member %r in %s" % (member, archive))
        with zipfile.ZipFile(archive) as z:
            info = z.getinfo(member)
            if info.is_dir():
                raise LockMismatch("%s in %s is not a file" % (member, archive))
            with z.open(info) as src, open(part, "wb") as out:
                shutil.copyfileobj(src, out, 1 << 20)
    elif low.endswith((".tar.gz", ".tgz", ".tar", ".tar.bz2")):
        if not safe_member(member):
            raise LockMismatch("unsafe archive member %r in %s" % (member, archive))
        with tarfile.open(archive) as t:
            info = t.getmember(member)
            if not info.isfile():
                raise LockMismatch("%s in %s is not a regular file (link or folder)" % (member, archive))
            with t.extractfile(info) as src, open(part, "wb") as out:
                shutil.copyfileobj(src, out, 1 << 20)
    else:
        raise LockMismatch("unknown archive format: %s" % archive)
    os.replace(part, dest)
    return dest


def fetch_via_archive(e, work, paths, throttle, opener, pin=False):
    """An input pinned as a file inside an archive (Lexique zip, Leipzig tar.gz, Tatoeba bz2): the URL is the archive's.
    Download it to the archive path (checked against e["archive"]["sha256"], or pinned when pin=True and it has none),
    then extract the member to the input's path. The archive is never written to the extracted file's path."""
    arch = e["archive"]
    apath = resolve(e, work, archive=True, paths=paths)
    if not os.path.exists(apath):
        if arch.get("sha256") is None and not pin:
            raise SystemExit("%s: its archive is not pinned (run pin-new after review)" % e["id"])
        download(e["url"], apath, arch.get("sha256"), throttle, opener)
    if arch.get("sha256") is None:
        arch["sha256"], arch["bytes"] = sha256_file(apath), os.path.getsize(apath)
    else:
        check_pin(apath, arch)
    dest = resolve(e, work, paths=paths)
    extract_member(apath, arch.get("member"), dest)
    return dest


def gutenberg_dir(book_id):
    """Directory of a book in the numbered tree: 15058 -> 1/5/0/5/15058/, 7 -> 0/7/."""
    s = str(int(book_id))
    head = "0" if len(s) == 1 else "/".join(s[:-1])
    return "%s/%s/" % (head, s)


class _Links(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []

    def handle_starttag(self, tag, attrs):
        if tag == "a":
            href = dict(attrs).get("href")
            if href:
                self.links.append(href)


def pick_gutenberg_file(book_id, listing_html):
    """Choose the plain-text file of a book from its directory listing: UTF-8 (-0.txt), then ISO-8859-1 (-8.txt),
    then ASCII (.txt). Returns (file name, encoding) or None."""
    p = _Links()
    p.feed(listing_html)
    names = {urllib.parse.unquote(h.rsplit("/", 1)[-1]) for h in p.links}
    s = str(int(book_id))
    for name, enc in ((s + "-0.txt", "utf-8"), (s + "-8.txt", "iso-8859-1"), (s + ".txt", "ascii")):
        if name in names:
            return name, enc
    return None


def resolve_gutenberg_url(e, throttle, opener=urllib.request.urlopen):
    base = PGLAF + gutenberg_dir(e["gutenberg_id"])
    listing = get_bytes(base, throttle, opener).decode("utf-8", "replace")
    got = pick_gutenberg_file(e["gutenberg_id"], listing)
    if got is None:
        raise SystemExit("no plain-text file for Gutenberg #%s in %s" % (e["gutenberg_id"], base))
    e["url"], e["encoding"] = base + got[0], got[1]


def snapshot_gutendex(url, dest, throttle, opener=urllib.request.urlopen):
    """Follow the « next » links of a Gutendex query and write one JSON file (results sorted by id, stable keys)."""
    results, pages, nxt = [], 0, url
    while nxt:
        page = json.loads(get_bytes(nxt, throttle, opener).decode("utf-8"))
        results.extend(page["results"])
        nxt, pages = page.get("next"), pages + 1
    results.sort(key=lambda b: b["id"])
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    tmp = dest + ".part"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"query": url, "count": len(results), "pages": pages, "results": results}, f,
                  ensure_ascii=False, indent=1, sort_keys=True)
        f.write("\n")
    os.replace(tmp, dest)
    return sha256_file(dest)


# ---------------------------------------------------------------- commands

def cmd_fetch(a, lock, work, throttle, opener=urllib.request.urlopen):
    paths = local_paths(work)
    for e in select(lock, a.ids, a.group):
        if e.get("repo_path"):
            continue
        dest = resolve(e, work, paths=paths)
        if e.get("sha256") is None:
            print("skip %s: not pinned yet (review it, then run pin-new)" % e["id"])
            continue
        if os.path.exists(dest):
            check_pin(dest, e)
            print("ok (present) %s" % e["id"])
            continue
        if not e.get("url") or e.get("kind") == "gutendex" or e["id"] in paths:
            raise SystemExit("%s is missing at %s and cannot be re-downloaded identically (snapshot or adopted copy)"
                             % (e["id"], dest))
        if e.get("archive"):
            fetch_via_archive(e, work, paths, throttle, opener)
        else:
            download(e["url"], dest, e["sha256"], throttle, opener)
        check_pin(dest, e)
        print("ok %s <- %s" % (e["id"], e["url"]))
    return 0


def cmd_verify(a, lock, work):
    paths, bad = local_paths(work), 0
    for e in select(lock, a.ids, a.group):
        recs = [(e, False)] + ([(e["archive"], True)] if e.get("archive") else [])
        for rec, is_arch in recs:
            name = e["id"] + (" (archive)" if is_arch else "")
            if rec.get("sha256") is None:
                print("UNPINNED %s" % name)
                bad += 1
                continue
            p = resolve(e, work, archive=is_arch, paths=paths)
            if not os.path.exists(p):
                if is_arch:
                    print("absent   %s (archive not kept locally)" % name)
                    continue
                print("MISSING  %s" % name)
                bad += 1
                continue
            try:
                check_pin(p, rec)
                print("ok       %s" % name)
            except LockMismatch as ex:
                print("MISMATCH %s" % ex)
                bad += 1
    print("%d problem(s)" % bad)
    return 1 if bad else 0


def cmd_pin_new(a, lock, work, throttle, opener=urllib.request.urlopen):
    paths, changed = local_paths(work), False
    for e in select(lock, a.ids, a.group):
        if e.get("archive") and e.get("url") and e["id"] not in paths and not os.path.exists(resolve(e, work, paths=paths)):
            fetch_via_archive(e, work, paths, throttle, opener, pin=True)     # archive first, then its member
            changed = True
            save_lock(lock, a.lock)
        recs = [(e, False)] + ([(e["archive"], True)] if e.get("archive") else [])
        for rec, is_arch in recs:
            if rec.get("sha256") is not None:
                continue
            dest = resolve(e, work, archive=is_arch, paths=paths)
            if not os.path.exists(dest):
                if is_arch or e.get("repo_path") or e["id"] in paths:
                    print("skip %s%s: no local copy" % (e["id"], " archive" if is_arch else ""))
                    continue
                if e.get("kind") == "gutendex":
                    snapshot_gutendex(e["url"], dest, throttle, opener)
                else:
                    if e.get("kind") == "gutenberg" and not e.get("url"):
                        resolve_gutenberg_url(e, throttle, opener)
                    t0 = time.monotonic()
                    download(e["url"], dest, None, throttle, opener)
                    print("  downloaded %s in %.1f s" % (e["id"], time.monotonic() - t0))
            rec["sha256"], rec["bytes"] = sha256_file(dest), os.path.getsize(dest)
            if not is_arch and not rec.get("date"):
                rec["date"] = _dt.date.today().isoformat()
            changed = True
            print("pinned %s%s %s (%d bytes)" % (e["id"], " archive" if is_arch else "", rec["sha256"], rec["bytes"]))
            save_lock(lock, a.lock)   # after every file: an interrupted run keeps what it pinned
    if not changed:
        print("nothing to pin")
    return 0


def cmd_adopt(a, lock, work):
    e = entry(lock, a.id)
    if not os.path.isfile(a.path):
        raise SystemExit("no such file: %s" % a.path)
    paths = local_paths(work)
    paths[e["id"] + (":archive" if a.archive else "")] = os.path.abspath(a.path)
    save_local_paths(work, paths)
    print("adopted %s%s -> %s (recorded in <work>/local-paths.json)" % (e["id"], " archive" if a.archive else "", a.path))
    return 0


# ---------------------------------------------------------------- books (plan 0.4)

# Authors who did not write in French, by Gutendex name prefix: their French texts are translations even when the
# catalogue lists no translator (owner Q5: no translation without a named translator). Reviewed by hand on the snapshot.
FOREIGN_AUTHORS = (
    "Andersen,", "Grimm,", "Carroll,", "Kipling,", "Stevenson,", "Swift,", "Defoe,", "Twain,", "Collodi,", "Wyss,",
    "Spyri,", "Hoffmann,", "Schmid,", "Dickens,", "Hawthorne,", "Alcott,", "Burnett,", "Baum,", "Barrie,", "Scott,",
    "Cooper,", "Marryat,", "Mayne Reid", "Reid,", "Kingsley,", "Grahame,", "Lagerlöf,", "Amicis,", "Perrault-Grimm",
    "Aesop", "Ésope", "Esope", "Aisopos", "Bechstein,", "Hauff,", "Musäus,", "Canth,", "Topelius,", "Asbjørnsen,",
    "Moe,", "Afanasyev,", "Pushkin,", "Tolstoy,", "Gogol,", "Lewis,", "Ruskin,", "Wilde,", "Harris, Joel",
    "Kingston,", "Ballantyne,", "Henty,", "Potter, Beatrix", "Goldsmith,", "Granstrem,",
)
TRANSLATION_WORDS = re.compile(r"(?im)^\s*(translator|traducteur|traductrice)\s*:|\btraduit (de|par)\b|\btraduction (de|par|nouvelle|française)\b")


def death_ok(people):
    return bool(people) and all(p.get("death_year") is not None and p["death_year"] <= DEATH_LIMIT for p in people)


def foreign_author(authors):
    return any(p["name"].startswith(FOREIGN_AUTHORS) for p in authors)


def eligibility(book, header_text=None):
    """(eligible, translation, evidence, reason) for one Gutendex record under the owner's rule: every author and
    every translator died in 1955 or earlier; a translation without a named translator is excluded."""
    authors, translators = book.get("authors") or [], book.get("translators") or []
    evidence = []
    if translators:
        evidence.append("translators listed")
    if foreign_author(authors):
        evidence.append("author did not write in French")
    if header_text and TRANSLATION_WORDS.search(header_text):
        evidence.append("file header mentions a translation")
    translation = bool(evidence)
    if book.get("languages") != ["fr"]:
        return False, translation, evidence, "not French only: %s" % ",".join(book.get("languages") or [])
    if not authors:
        return False, translation, evidence, "no author listed"
    if not death_ok(authors):
        return False, translation, evidence, "author death year unknown or after %d" % DEATH_LIMIT
    if translation and not translators:
        return False, translation, evidence, "translation without a named translator"
    if translators and not death_ok(translators):
        return False, translation, evidence, "translator death year unknown or after %d" % DEATH_LIMIT
    return True, translation, evidence, ""


# Owner review (2026-11-16): legally eligible, but not children's reading. Applied after the seeded picks, so the
# held-out books (E1) do not move; a curated book becomes « excluded-content » and stays in pd-books.tsv as the trace.
CURATED_EXCLUDE = {
    18112: "adult fiction (Defoe, Moll Flanders)",
    78398: "adult fiction (Mirbeau, Sébastien Roch)",
    52376: "adult fiction (Goldsmith, Le Vicaire de Wakefield)",
    64798: "adult fiction (Frapié, La Maternelle, Goncourt 1904)",
    71143: "adult fiction (Frapié, La Maternelle, Goncourt 1904)",
    27876: "adult fiction (Ducray-Duminil, Victor ou l'enfant de la forêt, 1797 melodrama novel)",
    22917: "textbook (Leçons de cosmographie)",
    29398: "textbook / word list (French Reader on the Cumulative Method)",
    38913: "manual (Manuel des difficultés les plus communes de la langue française)",
    39256: "treatise for educators (Petit traité des punitions et des récompenses)",
    20966: "scientific report (Itard, De l'éducation d'un homme sauvage, 1801)",
}
# pre-1835 spelling (« étoit », « avoit », « enfans »…): a book above this rate per 1 000 tokens is excluded
# (measured 2026-11-16: #9891 22.4, #31176 2.7, #20966 0.63, #27876 0.51, #38913 0.42; every other book ≤ 0.22)
OLD_SPELLING = re.compile(r"\b(étoit|avoit|étoient|avoient|faisoit|disoit|pouvoit|vouloit|alloit|connoître|paroître|"
                          r"foible|enfans|parens|savoit|devoit|seroit|auroit|étois|avois)\b")
OLD_SPELLING_MAX = 0.3


def old_spelling_rate(sentences):
    """Pre-1835 spellings per 1 000 tokens."""
    n = o = 0
    for s in sentences:
        low = s.lower()
        n += len(re.findall(r"[a-zà-ÿœ]+", low))
        o += len(OLD_SPELLING.findall(low))
    return 1000.0 * o / n if n else 0.0


def curate(roles, old_rates):
    """Turn the training roles of curated or old-spelling books into « excluded-content »; returns {id: reason}."""
    reasons = {}
    for bid, role in list(roles.items()):
        if not role.startswith("train"):
            continue
        why = CURATED_EXCLUDE.get(bid)
        if why is None and old_rates.get(bid, 0.0) > OLD_SPELLING_MAX:
            why = "pre-1835 spelling (%.1f per 1 000 tokens)" % old_rates[bid]
        if why:
            roles[bid] = "excluded-content"
            reasons[bid] = why
    return reasons


def author_key(book):
    return " & ".join(sorted(p["name"] for p in book.get("authors") or []))


def pick_books(books, tokens, seed=SEED, n_heldout=HELDOUT_BOOKS, light=LIGHT_TOKENS, min_tokens=HELDOUT_MIN_TOKENS):
    """Seeded split of the eligible books. Returns {id: role}: 'heldout-E1' (n_heldout books, one per author, from
    authors that then have no book in training), 'train-light' (the first books of a seeded order until `light`
    tokens) and 'train-full' (the other eligible books, Phase 2 only); excluded authors' other books get
    'excluded-heldout-author'; a second edition of the same title by the same author gets 'duplicate' (the catalogue
    holds « Les petites filles modèles » and « La Maternelle » twice: counting them twice would double their n-grams)."""
    rng = random.Random(seed)
    roles, first = {}, {}
    for b in sorted(books, key=lambda b: b["id"]):
        key = (author_key(b), re.sub(r"\W+", " ", b["title"].split(":")[0]).strip().lower())
        if key in first:
            roles[b["id"]] = "duplicate"
        else:
            first[key] = b["id"]
    books = [b for b in books if b["id"] not in roles]
    by_author = {}
    for b in books:
        by_author.setdefault(author_key(b), []).append(b)
    candidates = sorted(a for a, bs in by_author.items()
                        if any(tokens.get(b["id"], 0) >= min_tokens for b in bs))
    rng.shuffle(candidates)
    held_authors = candidates[:n_heldout]
    held_people = {p for a in held_authors for p in a.split(" & ")}
    for a in held_authors:
        pick = rng.choice(sorted((b for b in by_author[a] if tokens.get(b["id"], 0) >= min_tokens), key=lambda b: b["id"]))
        roles[pick["id"]] = "heldout-E1"
    rest = []
    for b in sorted(books, key=lambda b: b["id"]):
        if b["id"] in roles:
            continue
        if any(p["name"] in held_people for p in b.get("authors") or []):
            roles[b["id"]] = "excluded-heldout-author"
        else:
            rest.append(b["id"])
    rng.shuffle(rest)
    total = 0
    for bid in rest:
        if total < light:
            roles[bid] = "train-light"
            total += tokens.get(bid, 0)
        else:
            roles[bid] = "train-full"
    return roles


# bidirectional marks and zero-width characters found in catalogue strings (a U+200F after « Mirmont »): never copied
_INVISIBLE = re.compile("[\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]")


def clean_text(s):
    return re.sub(r"\s+", " ", _INVISIBLE.sub("", s or "")).strip()


def clean_book(b):
    """The Gutendex record with its title and person names cleaned (invisible characters, spacing)."""
    b = dict(b)
    b["title"] = clean_text(b.get("title"))
    for k in ("authors", "translators"):
        b[k] = [dict(p, name=clean_text(p.get("name"))) for p in b.get(k) or []]
    return b


def people(ps):
    return "; ".join("%s (%s–%s)" % (p["name"], p.get("birth_year") or "?", p.get("death_year") or "?") for p in ps)


def max_death(ps):
    ds = [p.get("death_year") for p in ps]
    return "" if not ds else ("?" if any(d is None for d in ds) else str(max(ds)))


def book_entry(b):
    title = re.sub(r"\s+", " ", b["title"]).strip()
    names = "; ".join(p["name"] for p in b["authors"])
    tr = "; ".join(p["name"] for p in b.get("translators") or [])
    return {
        "id": "gutenberg-%d" % b["id"], "group": "gutenberg", "kind": "gutenberg", "gutenberg_id": b["id"],
        "file": "gutenberg/%d.txt" % b["id"], "url": None, "encoding": None,
        "page": "https://www.gutenberg.org/ebooks/%d" % b["id"], "version": "Project Gutenberg eBook #%d" % b["id"],
        "date": None, "sha256": None, "bytes": None,
        "licence": "Public domain in the EU (author%s died ≤ %d); Project Gutenberg file" % (
            " and translator" if tr else "", DEATH_LIMIT),
        "credit": "« %s », %s%s — Project Gutenberg eBook #%d (https://www.gutenberg.org/ebooks/%d)" % (
            title, names, (", trad. " + tr) if tr else "", b["id"], b["id"]),
        "use": "train", "public": True, "title": title, "authors": names, "translators": tr,
    }


def cmd_books(a, lock, work):
    snap = entry(lock, "gutendex-fr-children")
    path = resolve(snap, work)
    if snap.get("sha256"):
        check_pin(path, snap)
    with open(path, encoding="utf-8") as f:
        books = [clean_book(b) for b in json.load(f)["results"]]
    sys.path.insert(0, HERE)
    import prepare_corpus as pc   # noqa: E402 (same folder; -I does not put it on sys.path)
    paths = local_paths(work)
    by_id = {e.get("gutenberg_id"): e for e in lock["inputs"] if e.get("group") == "gutenberg"}
    rows, eligible, tokens, old_rates, have_all = [], [], {}, {}, True
    for b in books:
        e = by_id.get(b["id"])
        header, text_path = None, None
        if e and e.get("sha256"):
            text_path = resolve(e, work, paths=paths)
            check_pin(text_path, e)
            raw = pc.read_gutenberg(text_path, e.get("encoding") or "utf-8")
            header = raw[:6000]
        ok, translation, evidence, reason = eligibility(b, header)
        if ok:
            if text_path:
                sents = pc.gutenberg_sentences(raw)
                tokens[b["id"]] = pc.count_tokens(sents)
                old_rates[b["id"]] = old_spelling_rate(sents)
            else:
                have_all = False
            eligible.append(b)
        rows.append((b, ok, translation, evidence, reason))
    roles = pick_books(eligible, tokens) if have_all and eligible else {}
    curated = curate(roles, old_rates)
    # lock entries: one per eligible book (kept even if a later header check excludes it: the file is pinned)
    for b in eligible:
        if b["id"] not in by_id:
            e = book_entry(b)
            lock["inputs"].append(e)
            by_id[b["id"]] = e
        else:   # descriptive fields follow the (cleaned) catalogue; url, encoding, date and pins stay
            fresh = book_entry(b)
            for k in ("title", "authors", "translators", "credit", "licence", "page", "version"):
                by_id[b["id"]][k] = fresh[k]
        role = roles.get(b["id"], "")
        by_id[b["id"]]["use"] = {"heldout-E1": "heldout", "duplicate": "excluded",
                                       "excluded-content": "excluded",
                                       "excluded-heldout-author": "excluded"}.get(role, "train")
    for bid, e in by_id.items():
        if bid not in {b["id"] for b in eligible}:
            e["use"] = "excluded"
            e["public"] = False
    save_lock(lock, a.lock)
    with open(BOOKS_TSV, "w", encoding="utf-8", newline="\n") as f:
        f.write("# Public-domain children's books (Gutendex languages=fr&topic=children, snapshot pinned in data.lock.json).\n"
                "# Rule (owner): every author and translator died in %d or earlier; a translation needs a named translator.\n"
                "# Roles: heldout-E1 (seed %d, %d books, authors absent from training), train-light (about %d tokens),\n"
                "# train-full (Phase 2 only), excluded-heldout-author, excluded. Generated by fetch_corpus.py books.\n"
                % (DEATH_LIMIT, SEED, HELDOUT_BOOKS, LIGHT_TOKENS))
        f.write("\t".join(["gutenberg_id", "title", "authors", "author_death_max", "translators", "translator_death_max",
                           "languages", "translation", "translation_evidence", "eligible", "reason", "tokens", "role"]) + "\n")
        for b, ok, translation, evidence, reason in rows:
            role = roles.get(b["id"], "" if ok else "excluded")
            f.write("\t".join([str(b["id"]), re.sub(r"\s+", " ", b["title"]).strip(), people(b.get("authors") or []),
                               max_death(b.get("authors") or []), people(b.get("translators") or []),
                               max_death(b.get("translators") or []), ",".join(b.get("languages") or []),
                               "yes" if translation else "no", "; ".join(evidence), "yes" if ok else "no",
                               curated.get(b["id"], reason),
                               str(tokens.get(b["id"], "")), role]) + "\n")
    n_ok = len(eligible)
    print("%d books in the snapshot, %d eligible -> %s" % (len(books), n_ok, BOOKS_TSV))
    if roles:
        for r in ("heldout-E1", "train-light", "train-full", "excluded-heldout-author", "duplicate", "excluded-content"):
            ids = [bid for bid, x in roles.items() if x == r]
            print("  %-24s %3d books, %9d tokens" % (r, len(ids), sum(tokens.get(i, 0) for i in ids)))
    else:
        print("  picks pending: pin the eligible books first (pin-new --group gutenberg), then run books again")
    return 0


# ---------------------------------------------------------------- main

def main(argv=None, opener=urllib.request.urlopen, throttle=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", help="work folder (default: $PEPITE_CORPUS_WORK)")
    ap.add_argument("--lock", default=LOCK)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("fetch", "verify", "pin-new"):
        s = sub.add_parser(name)
        s.add_argument("--ids")
        s.add_argument("--group")
    s = sub.add_parser("adopt")
    s.add_argument("id")
    s.add_argument("path")
    s.add_argument("--archive", action="store_true")
    sub.add_parser("books")
    a = ap.parse_args(argv)
    work = work_dir(a.work)
    lock = load_lock(a.lock)
    throttle = throttle or Throttle()
    try:
        if a.cmd == "fetch":
            return cmd_fetch(a, lock, work, throttle, opener)
        if a.cmd == "verify":
            return cmd_verify(a, lock, work)
        if a.cmd == "pin-new":
            return cmd_pin_new(a, lock, work, throttle, opener)
        if a.cmd == "adopt":
            return cmd_adopt(a, lock, work)
        return cmd_books(a, lock, work)
    except LockMismatch as ex:
        print("STOP: %s" % ex, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
