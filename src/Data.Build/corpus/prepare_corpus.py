#!/usr/bin/env python3
"""Prepare the training samples and the held-out sets of the light corpus experiment (plan corpus-light, 0.5).

Reads the inputs pinned in Data.Build/data.lock.json (paths through fetch_corpus.py: $PEPITE_CORPUS_WORK) and
writes everything under <out> (default <work>/corpus-light); nothing here is committed.

    extract  [--what vikidia,gutenberg]   Vikidia ZIM: the text of the <p> elements, disambiguation pages dropped;
                                          Gutenberg: header / footer, tables of contents and dialogue dashes stripped;
                                          both split into sentences, sentences under 4 tokens dropped
                                          -> extract/vikidia.tsv (title<TAB>sentence), extract/gutenberg.tsv (id<TAB>sentence)
    split                                 Vikidia article held out when sha256(title) mod 20 == 0; books by pd-books.tsv
                                          role; stops if a held-out book id is in a training list
                                          -> split/{vikidia-train,vikidia-heldout,books-heldout,books-train-light,books-train-full}.tsv
    heldout                               seeded draws of E1 (held-out books), E2 (Vikidia bucket), E3 (G-invented gold
                                          sentences), W (Leipzig Wikipedia 2021), S-unseen (subtitle held-out); every
                                          sentence seen in the union of the training pools is dropped; sentence and 4-gram
                                          overlap reported -> heldout/*.txt + heldout/manifest.json
    sample                                line i of source s is kept when sha256(seed|s|i) < rate * 2^256; news, Tatoeba and
                                          subtitles at 0.15; D's Vikidia rate solved so that Vikidia + books match A′'s
                                          subtitle tokens within 5 %; books capped at 20 % of D
                                          -> sample/{A1,D1}.list + sample/*.txt + sample/manifest.json

The tokeniser is the n-gram builders' one (lower case, ' ’ - -> space, [a-zà-ÿœ]+) with the « œ » / « æ » fold applied
to each token after the match (plan 0.1); every count here (token mass, evaluated words, overlap) uses it.
Run with ``python -I``; stdlib only, plus libzim (requirements-corpus.txt) for « extract --what vikidia ».
"""
import argparse
import hashlib
import html.parser
import json
import os
import random
import re
import sys
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = re.compile(r"[a-zà-ÿœ]+")
SEED = 20261115
MIN_TOKENS = 4               # plan 0.5: sentences under 4 tokens are dropped at extraction
BUCKETS, HELD_BUCKET = 20, 0  # plan 0.5: Vikidia article held out when sha256(title) mod 20 == 0
RATE = Fraction(15, 100)      # news, Tatoeba, subtitles
BOOK_CAP = Fraction(20, 100)  # books at most 20 % of D's tokens
MATCH_TOL = Fraction(5, 100)  # Vikidia + books within 5 % of A′'s subtitle tokens
TARGETS = {"E1": 5000, "E2": 5000, "E3": None, "W": 10000, "S-unseen": 10000}   # evaluated words (None = all)


def fold(token):
    """« œ » -> « oe », « æ » -> « ae » (Lexique spells « coeur »). Applied per token, never to the whole line.
    Same function in build_ngrams.py, build_4grams.py and db_checks.py (a unit test checks that they agree)."""
    return token.replace("œ", "oe").replace("æ", "ae")


def tokenize(line):
    return [fold(t) for t in TOKEN.findall(line.lower().replace("'", " ").replace("’", " ").replace("-", " "))]


def count_tokens(sentences):
    return sum(len(tokenize(s)) for s in sentences)


def norm_key(line):
    """Identity of a sentence for the leak check: its token sequence."""
    return " ".join(tokenize(line))


def key_hash(line):
    k = norm_key(line)
    return int.from_bytes(hashlib.blake2b(k.encode("utf-8"), digest_size=8).digest(), "big") if k else None


# ---------------------------------------------------------------- sentence splitting

ABBREV = {"m", "mm", "mme", "mmes", "mlle", "mlles", "dr", "st", "ste", "cf", "etc", "p", "pp", "vol", "chap", "env",
          "av", "apr", "j.-c", "c", "n", "no", "ex", "fig", "art", "hab", "km", "kg"}
_END = re.compile(r"[.!?…]+[»”\")\]]*\s+(?=[«“\"—–\-(A-ZÀ-ÖØ-Þ0-9Œ])")


def split_sentences(text):
    """Split running text into sentences at . ! ? … followed by a space and an upper-case letter, a digit, an opening
    quote or a dash; never after a known abbreviation (« M. Dupont ») or a single capital initial (« J. Verne »)."""
    text = re.sub(r"\s+", " ", text).strip()
    out, start = [], 0
    for m in _END.finditer(text):
        before = text[start:m.start()]
        last = re.search(r"([\w.\-]+)$", before)
        word = last.group(1) if last else ""
        if text[m.start()] == "." and (word.lower().rstrip(".") in ABBREV or re.fullmatch(r"[A-ZÀ-Ý]", word)):
            continue
        out.append(text[start:m.end()].strip())
        start = m.end()
    tail = text[start:].strip()
    if tail:
        out.append(tail)
    return [s for s in out if s]


def strip_dialogue(s):
    """Drop a leading dialogue dash or guillemet-dash (« — Oui », « -- Oui », « - Oui »)."""
    return re.sub(r"^(?:[«»\"]\s*)?(?:—|–|--+|-)\s*", "", s).strip()


def keep(sentences):
    return [s for s in sentences if len(tokenize(s)) >= MIN_TOKENS]


# ---------------------------------------------------------------- Gutenberg

_START = re.compile(r"^\*{3}\s*START OF (THE|THIS) PROJECT GUTENBERG.*$", re.M | re.I)
_END_MARK = re.compile(r"^\*{3}\s*END OF (THE|THIS) PROJECT GUTENBERG.*$|^\s*End of (the )?Project Gutenberg'?s?\b.*$"
                       r"|^\s*Fin (de|du) (ce )?(livre|Project Gutenberg).*$", re.M | re.I)
_TOC_HEAD = re.compile(r"^\s*(TABLE( DES MATI[ÈE]RES| DES CHAPITRES| ANALYTIQUE)?|Table( des mati[èe]res| des chapitres)?"
                       r"|SOMMAIRE|Sommaire|INDEX)\.?\s*$")
_TOC_LINE = re.compile(r"(\.{3,}|\s{2,}|\s)\d+\.?\s*$|^\s*([IVXLC]+|\d+)[.\-—)]|^\s*(CHAPITRE|Chapitre|CHAP\.)\b")


def read_gutenberg(path, encoding="utf-8"):
    with open(path, "rb") as f:
        data = f.read()
    text = data.decode(encoding or "utf-8", "replace")
    return text.lstrip("\ufeff").replace("\r\n", "\n").replace("\r", "\n")


def strip_gutenberg(text):
    """The book body: after the « *** START OF … » line, before the « *** END OF » / « End of … Project Gutenberg »
    line; a « Produced by » credit paragraph at the top is dropped."""
    m = _START.search(text)
    body = text[m.end():] if m else text
    e = _END_MARK.search(body)
    body = body[:e.start()] if e else body
    paras = re.split(r"\n\s*\n", body.strip("\n"))
    while paras and re.match(r"^\s*(Produced by|E-text prepared by|Ce livre a été|This (e)?book was produced)", paras[0], re.I):
        paras.pop(0)
    return "\n\n".join(paras)


def drop_toc(paras):
    """Remove a table of contents: its heading and the paragraphs after it whose lines are all short or look like
    entries (« I. Titre ....... 5 », « CHAPITRE II », a page number at the end)."""
    out, i = [], 0
    while i < len(paras):
        lines = [l for l in paras[i].split("\n") if l.strip()]
        if lines and _TOC_HEAD.match(lines[0]):
            rest = lines[1:]
            i += 1
            if rest and not all(_toc_like(l) for l in rest):
                continue
            while i < len(paras):
                ls = [l for l in paras[i].split("\n") if l.strip()]
                if ls and all(_toc_like(l) for l in ls):
                    i += 1
                else:
                    break
            continue
        if lines and len(lines) >= 3 and all(_TOC_LINE.search(l) and len(l.strip()) < 75 for l in lines) \
                and sum(1 for l in lines if re.search(r"\d+\.?\s*$", l)) >= len(lines) // 2 + 1:
            i += 1      # an entry block without a heading (page numbers at the end of most lines)
            continue
        out.append(paras[i])
        i += 1
    return out


def _toc_like(line):
    s = line.strip()
    return bool(_TOC_LINE.search(s)) or len(s) < 50 and not re.search(r"[.!?»:;]$", s)


def gutenberg_sentences(raw):
    body = strip_gutenberg(raw)
    paras = drop_toc(re.split(r"\n\s*\n", body))
    out = []
    for p in paras:
        p = re.sub(r"\[(Illustration[^\]]*|\d+|[A-Z])\]", " ", p)
        p = p.replace("_", "").replace("\n", " ")
        if not p.strip():
            continue
        for s in split_sentences(p):
            s = strip_dialogue(s)
            if s:
                out.append(s)
    return out


# ---------------------------------------------------------------- Vikidia (ZIM)

class _Paragraphs(html.parser.HTMLParser):
    """Text of the <p> elements only (nested markup kept as text, <sup> reference marks and <style>/<script> dropped)."""

    SKIP = {"sup", "style", "script"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth, self.skip, self.buf, self.paras, self.classes = 0, 0, [], [], []

    def handle_starttag(self, tag, attrs):
        cls = dict(attrs).get("class") or ""
        if cls:
            self.classes.append(cls)
        if tag == "p":
            self.depth += 1
        elif tag in self.SKIP and self.depth:
            self.skip += 1
        elif tag == "br" and self.depth:
            self.buf.append(" ")

    def handle_endtag(self, tag):
        if tag == "p" and self.depth:
            self.depth -= 1
            if not self.depth:
                t = re.sub(r"\s+", " ", "".join(self.buf)).strip()
                if t:
                    self.paras.append(t)
                self.buf = []
        elif tag in self.SKIP and self.skip:
            self.skip -= 1

    def handle_data(self, data):
        if self.depth and not self.skip:
            self.buf.append(data)


def html_paragraphs(html_text):
    p = _Paragraphs()
    p.feed(html_text)
    p.close()
    return p.paras, p.classes


_DISAMBIG_TITLE = re.compile(r"\((homonymie|désambiguïsation)\)\s*$", re.I)
_DISAMBIG_TEXT = re.compile(r"\bpeut (désigner|faire référence|signifier|renvoyer)\b|\bpeuvent désigner\b"
                            r"|\b(peut|peuvent) (être|signifier|désigner)\s*:\s*$|^Cette page d'homonymie", re.I)
# the banner of a Vikidia disambiguation page (« Sur cette page d'homonymie, tu trouveras… »). Not a CSS class: MediaWiki
# marks every *link* to such a page with class « mw-disambig », so ordinary articles carry that class too.
_DISAMBIG_BANNER = re.compile(r"cette page d.homonymie", re.I)


def is_disambiguation(title, paras, html_text=""):
    if _DISAMBIG_TITLE.search(title or "") or _DISAMBIG_BANNER.search(html_text or ""):
        return True
    return bool(paras) and bool(_DISAMBIG_TEXT.search(paras[0])) and len(paras) <= 3


def article_sentences(title, html_text):
    """Sentences of one Vikidia article, or [] for a disambiguation page."""
    paras, _ = html_paragraphs(html_text)
    if is_disambiguation(title, paras, html_text):
        return []
    out = []
    for p in paras:
        p = re.sub(r"\[\s*(\d+|réf\.?|note \d+|[a-z])\s*\]", " ", p)
        out.extend(strip_dialogue(s) for s in split_sentences(p))
    return keep([s for s in out if s])


# wiki pages that are not encyclopaedia articles (the ZIM also holds portals, help and project pages)
_NOT_ARTICLE = re.compile(r"^(Portail|Catégorie|Modèle|Aide|Vikidia|Utilisateur|Fichier|Projet|Spécial|Wikipédia|MediaWiki"
                          r"|Module|Discussion|Annexe|Livre|Thème|Accueil)( [^:]*)?:", re.I)


def is_article_title(title):
    return bool(title) and not _NOT_ARTICLE.match(title)


def zim_articles(path):
    """(title, html) of every article of a ZIM file (libzim 4.x): HTML entries (mimetype exactly text/html, as the
    wiki pages; the generated index pages carry a charset), no redirect, no main page, no portal / help / project page."""
    from libzim.reader import Archive   # build-time only (requirements-corpus.txt)
    zim = Archive(path)
    main = zim.main_entry.get_item().path if zim.has_main_entry else None
    for i in range(zim.all_entry_count):
        e = zim.get_entry_by_id(i)
        if e.is_redirect or not is_article_title(e.title):
            continue
        item = e.get_item()
        if item.mimetype != "text/html" or item.path == main:
            continue
        yield e.title, bytes(item.content).decode("utf-8", "replace")


# ---------------------------------------------------------------- lock access

def _fc():
    sys.path.insert(0, HERE)
    import fetch_corpus as fc   # noqa: E402 (same folder; -I does not put it on sys.path)
    return fc


def input_path(lock, work, ident, verify=True):
    fc = _fc()
    e = fc.entry(lock, ident)
    p = fc.resolve(e, work)
    if verify:
        if e.get("sha256") is None:
            raise SystemExit("%s is not pinned in data.lock.json (run fetch_corpus.py pin-new)" % ident)
        fc.check_pin(p, e)
    return p


def text_lines(path):
    """Text of each line: the last tab column for Tatoeba *.tsv / Leipzig *sentences.txt (as the builders), else the line."""
    tabbed = path.endswith(".tsv") or path.endswith("sentences.txt")
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            line = line.rstrip("\n")
            yield line.split("\t")[-1] if tabbed and "\t" in line else line


def read_tsv2(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            k, _, s = line.rstrip("\n").partition("\t")
            yield k, s


def write_lines(path, lines):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    n = 0
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for s in lines:
            f.write(s.replace("\n", " ") + "\n")
            n += 1
    return n


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_books_tsv(path):
    with open(path, encoding="utf-8") as f:
        rows = [l.rstrip("\n").split("\t") for l in f if not l.startswith("#")]
    head = rows[0]
    return [dict(zip(head, r)) for r in rows[1:] if r and r[0]]


# ---------------------------------------------------------------- split / held-out / sample (pure parts)

def title_bucket(title):
    return int(hashlib.sha256(title.encode("utf-8")).hexdigest(), 16) % BUCKETS


def is_heldout_title(title):
    return title_bucket(title) == HELD_BUCKET


def check_book_lists(heldout_ids, *training_lists):
    """Stop the build when a held-out book id appears in a training list."""
    bad = set(heldout_ids) & set().union(*map(set, training_lists))
    if bad:
        raise SystemExit("held-out book id(s) in a training list: %s" % ", ".join(sorted(map(str, bad))))


def line_hash(seed, source, line_no):
    return int.from_bytes(hashlib.sha256(("%s|%s|%d" % (seed, source, line_no)).encode("utf-8")).digest(), "big")


def threshold(rate):
    return int(Fraction(rate) * (1 << 256))


def picked(seed, source, line_no, rate=None, limit=None):
    """Plan 0.5: keep line `line_no` of `source` when sha256(seed|source|line_no) < rate × 2^256."""
    return line_hash(seed, source, line_no) < (threshold(rate) if limit is None else limit)


def solve_limit(hash_tokens, target):
    """Smallest hash limit such that the lines with hash < limit hold at least `target` tokens.
    hash_tokens: [(hash, tokens)]. Returns (limit, tokens taken, lines taken)."""
    total, n = 0, 0
    for h, t in sorted(hash_tokens):
        if total >= target:
            return h, total, n
        total += t
        n += 1
    if total < target:
        raise SystemExit("not enough text to reach %d tokens (only %d)" % (target, total))
    return 1 << 256, total, n


def plan_d(subs_tokens, shared_tokens, books, vikidia_hash_tokens, cap=BOOK_CAP, tol=MATCH_TOL):
    """Choose D's books and Vikidia limit. books: [(id, tokens)] in pd-books.tsv order (by Gutenberg id); shared_tokens = news +
    Tatoeba sample. Books are dropped from the end while they exceed `cap` of D; Vikidia fills the rest of the subtitle
    token mass. Returns dict(books, book_tokens, limit, vikidia_tokens, vikidia_lines, total)."""
    books = list(books)
    while True:
        b = sum(t for _, t in books)
        total_est = shared_tokens + subs_tokens
        if books and Fraction(b) > cap * total_est:
            books.pop()
            continue
        break
    limit, v, n = solve_limit(vikidia_hash_tokens, subs_tokens - b)
    if abs(Fraction(v + b - subs_tokens)) > tol * subs_tokens:
        raise SystemExit("Vikidia + books = %d tokens, not within 5 %% of %d" % (v + b, subs_tokens))
    total = shared_tokens + v + b
    if books and Fraction(b) > cap * total:
        raise SystemExit("books are %.1f %% of D (cap 20 %%)" % (100.0 * b / total))
    return {"books": [i for i, _ in books], "book_tokens": b, "limit": limit, "vikidia_tokens": v,
            "vikidia_lines": n, "total": total}


def evaluable(tokens, lexicon=None):
    """Words Predict.Eval scores in a line: every token after the first, of 2 letters or more, known to the lexicon."""
    return sum(1 for t in tokens[1:] if len(t) >= 2 and (lexicon is None or t in lexicon))


def draw(sentences, seen, rng, target, lexicon=None, shuffle=True):
    """Seeded draw: shuffle, drop sentences whose token sequence was seen in training, take sentences until `target`
    evaluated words (all when target is None). Returns (kept, dropped_seen, words)."""
    order = list(sentences)
    if shuffle:
        rng.shuffle(order)
    kept, dropped, words = [], 0, 0
    for s in order:
        if target is not None and words >= target:
            break
        toks = tokenize(s)
        if not toks:
            continue
        h = key_hash(s)
        if h in seen:
            dropped += 1
            continue
        kept.append(s)
        words += evaluable(toks, lexicon)
    return kept, dropped, words


def fourgrams(tokens):
    return zip(tokens, tokens[1:], tokens[2:], tokens[3:])


def fourgram_overlap(heldout_sentences, training_lines):
    """Share of the held-out 4-gram occurrences whose 4-gram occurs in training."""
    occ = [g for s in heldout_sentences for g in fourgrams(tokenize(s))]
    want = set(occ)
    found = set()
    for line in training_lines:
        for g in fourgrams(tokenize(line)):
            if g in want:
                found.add(g)
    return (sum(1 for g in occ if g in found) / len(occ)) if occ else 0.0, len(occ)


# ---------------------------------------------------------------- commands

def out_dir(a, work):
    return a.out or os.path.join(work, "corpus-light")


def cmd_extract(a, lock, work):
    out = out_dir(a, work)
    what = set((a.what or "vikidia,gutenberg").split(","))
    fc = _fc()
    if "vikidia" in what:
        zim = input_path(lock, work, "vikidia-fr-2026-09")
        n_art = n_dis = n_sent = 0
        with open(os.path.join(_mk(out, "extract"), "vikidia.tsv"), "w", encoding="utf-8", newline="\n") as f:
            for title, page in zim_articles(zim):
                ss = article_sentences(title, page)
                if not ss:
                    n_dis += 1
                    continue
                n_art += 1
                t = title.replace("\t", " ")
                for s in ss:
                    f.write("%s\t%s\n" % (t, s.replace("\t", " ")))
                    n_sent += 1
        print("vikidia: %d articles kept, %d dropped (disambiguation or no text), %d sentences" % (n_art, n_dis, n_sent))
    if "gutenberg" in what:
        paths = fc.local_paths(work)
        n_books = n_sent = 0
        with open(os.path.join(_mk(out, "extract"), "gutenberg.tsv"), "w", encoding="utf-8", newline="\n") as f:
            for e in lock["inputs"]:
                if e.get("group") != "gutenberg" or e.get("use") not in ("train", "heldout"):
                    continue
                p = fc.resolve(e, work, paths=paths)
                fc.check_pin(p, e)
                for s in keep(gutenberg_sentences(read_gutenberg(p, e.get("encoding")))):
                    f.write("%d\t%s\n" % (e["gutenberg_id"], s.replace("\t", " ")))
                    n_sent += 1
                n_books += 1
        print("gutenberg: %d books, %d sentences" % (n_books, n_sent))
    return 0


def _mk(*parts):
    p = os.path.join(*parts)
    os.makedirs(p, exist_ok=True)
    return p


def cmd_split(a, lock, work):
    out = out_dir(a, work)
    ex, sp = os.path.join(out, "extract"), _mk(out, "split")
    books = read_books_tsv(a.books)
    roles = {r["gutenberg_id"]: r["role"] for r in books}
    held = [i for i, r in roles.items() if r == "heldout-E1"]
    light = [i for i, r in roles.items() if r == "train-light"]
    full = [i for i, r in roles.items() if r in ("train-light", "train-full")]
    check_book_lists(held, light, full)
    n = {"vikidia-train": 0, "vikidia-heldout": 0}
    with open(os.path.join(sp, "vikidia-train.tsv"), "w", encoding="utf-8", newline="\n") as tr, \
            open(os.path.join(sp, "vikidia-heldout.tsv"), "w", encoding="utf-8", newline="\n") as ho:
        for title, s in read_tsv2(os.path.join(ex, "vikidia.tsv")):
            held_out = is_heldout_title(title)
            (ho if held_out else tr).write("%s\t%s\n" % (title, s))
            n["vikidia-heldout" if held_out else "vikidia-train"] += 1
    outs = {k: open(os.path.join(sp, "books-%s.tsv" % k), "w", encoding="utf-8", newline="\n")
            for k in ("heldout", "train-light", "train-full")}
    try:
        for bid, s in read_tsv2(os.path.join(ex, "gutenberg.tsv")):
            r = roles.get(bid, "")
            if r == "heldout-E1":
                outs["heldout"].write("%s\t%s\n" % (bid, s))
            if bid in light:
                outs["train-light"].write("%s\t%s\n" % (bid, s))
            if bid in full:
                outs["train-full"].write("%s\t%s\n" % (bid, s))
    finally:
        for f in outs.values():
            f.close()
    print("split: %d Vikidia training sentences, %d held out; books held out %s, light %d, full %d"
          % (n["vikidia-train"], n["vikidia-heldout"], ",".join(held), len(light), len(full)))
    return 0


def training_pools(lock, work, out):
    """Every line of every training pool (the full sources, not only the light samples)."""
    sp = os.path.join(out, "split")
    for ident in ("leipzig-news-2023", "tatoeba-fra", "opensubtitles-fr-4m"):
        yield from text_lines(input_path(lock, work, ident, verify=False))
    for name in ("vikidia-train.tsv", "books-train-full.tsv"):
        for _, s in read_tsv2(os.path.join(sp, name)):
            yield s


def load_lexicon(path):
    words = set()
    with open(path, encoding="utf-8") as f:
        head = f.readline().rstrip("\n").split("\t")
        col = head.index("ortho")
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) > col and p[col] and " " not in p[col]:
                words.add(fold(p[col].lower()))
    return words


def cmd_heldout(a, lock, work):
    out = out_dir(a, work)
    sp, ho = os.path.join(out, "split"), _mk(out, "heldout")
    fc = _fc()
    for ident in ("leipzig-news-2023", "tatoeba-fra", "opensubtitles-fr-4m", "leipzig-wikipedia-2021",
                  "opensubtitles-heldout-40k", "lexique-3.83", "g-invented"):
        input_path(lock, work, ident)            # every input checked against the lock once
    lexicon = load_lexicon(input_path(lock, work, "lexique-3.83", verify=False))
    seen = set()
    n_train = 0
    for s in training_pools(lock, work, out):
        h = key_hash(s)
        if h is not None:
            seen.add(h)
        n_train += 1
    print("training pools: %d lines, %d distinct token sequences" % (n_train, len(seen)))
    g_inv = []
    with open(input_path(lock, work, "g-invented", verify=False), encoding="utf-8") as f:
        for line in f:
            o = json.loads(line) if line.strip() else {}
            if "gold" in o and "errors" in o:
                g_inv.append(o["gold"])
    sources = {
        "E1": ([s for _, s in read_tsv2(os.path.join(sp, "books-heldout.tsv"))], True),
        "E2": ([s for _, s in read_tsv2(os.path.join(sp, "vikidia-heldout.tsv"))], True),
        "E3": (g_inv, False),
        "W": (list(text_lines(input_path(lock, work, "leipzig-wikipedia-2021", verify=False))), True),
        "S-unseen": (list(text_lines(input_path(lock, work, "opensubtitles-heldout-40k", verify=False))), False),
    }
    manifest = {"seed": SEED, "tokeniser": "lower; ' ’ - -> space; [a-zà-ÿœ]+; per-token fold œ->oe æ->ae",
                "evaluated_word": "token after the first of its line, 2+ letters, in Lexique 3.83 (as Predict.Eval)",
                "text_folded": "œ -> oe, æ -> ae in the written files (for a Predict.Eval without the fold; lower-case runs)",
                "inputs": {e["id"]: e.get("sha256") for e in lock["inputs"]
                           if e.get("group") != "gutenberg" and e["id"] != "gutendex-fr-children"},
                "books_heldout": [r["gutenberg_id"] for r in read_books_tsv(a.books) if r["role"] == "heldout-E1"],
                "vikidia_rule": "sha256(title) mod %d == %d" % (BUCKETS, HELD_BUCKET),
                "training_pools": ["leipzig-news-2023", "tatoeba-fra", "opensubtitles-fr-4m", "vikidia (train bucket)",
                                   "every eligible training book (train-light + train-full)"],
                "training_lines": n_train, "sets": {}}
    drawn = {}
    for name, (sents, shuffle) in sources.items():
        rng = random.Random("%d|%s" % (SEED, name))
        kept, dropped, words = draw(sents, seen, rng, TARGETS[name], lexicon, shuffle)
        drawn[name] = kept
        path = os.path.join(ho, "%s.txt" % name)
        # written with « œ » / « æ » already folded: until Predict.Eval folds its tokens (plan 0.1, C# part) an unfolded
        # « cœur » would be skipped as out of lexicon; the runs are lower-case (no --keep-case), so a line-level fold is safe
        write_lines(path, (fold(s) for s in kept))
        manifest["sets"][name] = {"file": "%s.txt" % name, "pool_sentences": len(sents), "sentences": len(kept),
                                  "dropped_seen": dropped, "evaluated_words": words, "target": TARGETS[name],
                                  "order": "seeded shuffle" if shuffle else "source order",
                                  "sha256": sha256_file(path)}
        print("%-8s %6d sentences, %6d evaluated words, %5d dropped as seen in training" % (name, len(kept), words, dropped))
    for name, kept in drawn.items():           # second pass per set: 4-gram overlap with the training pools
        share, n4 = fourgram_overlap(kept, training_pools(lock, work, out))
        manifest["sets"][name]["fourgram_overlap"] = round(share, 4)
        manifest["sets"][name]["fourgrams"] = n4
        pool = manifest["sets"][name]
        pool["sentence_overlap"] = round(pool["dropped_seen"] / max(1, pool["dropped_seen"] + pool["sentences"]), 4)
        print("%-8s 4-gram overlap with training %.1f %% of %d" % (name, 100 * share, n4))
    with open(os.path.join(ho, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return 0


def sample_source(seed, source, lines, rate):
    lim = threshold(rate)
    for i, s in enumerate(lines):
        if line_hash(seed, source, i) < lim:
            yield s


def cmd_sample(a, lock, work):
    out = out_dir(a, work)
    sp, sm = os.path.join(out, "split"), _mk(out, "sample")
    counts = {}
    for name, ident in (("news", "leipzig-news-2023"), ("tatoeba", "tatoeba-fra"), ("subtitles", "opensubtitles-fr-4m")):
        p = os.path.join(sm, "%s.txt" % name)
        counts[name] = {"lines": write_lines(p, sample_source(SEED, name, text_lines(input_path(lock, work, ident)), RATE))}
        counts[name]["tokens"] = count_tokens(text_lines(p))
        counts[name]["rate"] = str(RATE)
    subs, shared = counts["subtitles"]["tokens"], counts["news"]["tokens"] + counts["tatoeba"]["tokens"]
    roles = read_books_tsv(a.books)
    light_order = [r["gutenberg_id"] for r in roles if r["role"] == "train-light"]
    book_sents = {}
    for bid, s in read_tsv2(os.path.join(sp, "books-train-light.tsv")):
        book_sents.setdefault(bid, []).append(s)
    books = [(b, count_tokens(book_sents.get(b, []))) for b in light_order]
    vik = [(line_hash(SEED, "vikidia", i), len(tokenize(s)))
           for i, (_, s) in enumerate(read_tsv2(os.path.join(sp, "vikidia-train.tsv")))]
    d = plan_d(subs, shared, books, vik)
    write_lines(os.path.join(sm, "books.txt"), (s for b in d["books"] for s in book_sents.get(b, [])))
    lim = d["limit"]
    write_lines(os.path.join(sm, "vikidia.txt"),
                (s for i, (_, s) in enumerate(read_tsv2(os.path.join(sp, "vikidia-train.tsv"))) if line_hash(SEED, "vikidia", i) < lim))
    counts["books"] = {"tokens": d["book_tokens"], "books": d["books"]}
    counts["vikidia"] = {"tokens": d["vikidia_tokens"], "lines": d["vikidia_lines"],
                         "rate": float(Fraction(lim, 1 << 256)), "limit_hex": "%064x" % lim if lim < (1 << 256) else "all"}
    a1 = ["news.txt", "tatoeba.txt", "subtitles.txt"]
    d1 = ["news.txt", "tatoeba.txt", "vikidia.txt", "books.txt"]
    for name, files in (("A1", a1), ("D1", d1)):
        with open(os.path.join(sm, "%s.list" % name), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(os.path.join(sm, x) for x in files) + "\n")
    a_tot = shared + subs
    manifest = {"seed": SEED, "rule": "line i of source s kept when sha256(seed|s|i) < rate * 2^256", "sources": counts,
                "A1_tokens": a_tot, "D1_tokens": d["total"],
                "books_share_of_D1": round(d["book_tokens"] / d["total"], 4),
                "vikidia_plus_books_vs_subtitles": round((d["vikidia_tokens"] + d["book_tokens"]) / subs - 1, 4),
                "files": {x: sha256_file(os.path.join(sm, x)) for x in sorted(set(a1 + d1))}}
    with open(os.path.join(sm, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("A1 %d tokens; D1 %d tokens (books %.1f %%, Vikidia + books vs subtitles %+.1f %%)"
          % (a_tot, d["total"], 100 * manifest["books_share_of_D1"], 100 * manifest["vikidia_plus_books_vs_subtitles"]))
    return 0


def cmd_full(a, lock, work):
    """Phase 2: the full D input list — all of Leipzig news and Tatoeba, the Vikidia training bucket and every training
    book (train-light + train-full), books capped at 20 % of the tokens (whole books dropped from the end of the id
    order). Writes full/D.list, full/books.txt and full/manifest.json."""
    out = out_dir(a, work)
    sp, fu = os.path.join(out, "split"), _mk(out, "full")
    news, tat = input_path(lock, work, "leipzig-news-2023"), input_path(lock, work, "tatoeba-fra")
    vik = os.path.join(sp, "vikidia-train.tsv")
    counts = {"news": count_tokens(text_lines(news)), "tatoeba": count_tokens(text_lines(tat)),
              "vikidia": count_tokens(s for _, s in read_tsv2(vik))}
    order = [r["gutenberg_id"] for r in read_books_tsv(a.books) if r["role"] in ("train-light", "train-full")]
    held = [r["gutenberg_id"] for r in read_books_tsv(a.books) if r["role"] == "heldout-E1"]
    check_book_lists(held, order)
    sents = {}
    for bid, s in read_tsv2(os.path.join(sp, "books-train-full.tsv")):
        sents.setdefault(bid, []).append(s)
    books = [(b, count_tokens(sents.get(b, []))) for b in order]
    other = counts["news"] + counts["tatoeba"] + counts["vikidia"]
    while books and Fraction(sum(t for _, t in books)) > BOOK_CAP * (other + sum(t for _, t in books)):
        books.pop()
    counts["books"] = sum(t for _, t in books)
    write_lines(os.path.join(fu, "books.txt"), (s for b, _ in books for s in sents.get(b, [])))
    with open(os.path.join(fu, "D.list"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join([news, tat, vik, os.path.join(fu, "books.txt")]) + "\n")
    total = sum(counts.values())
    manifest = {"tokens": counts, "total": total, "books": [b for b, _ in books],
                "books_share": round(counts["books"] / total, 4), "books_sha256": sha256_file(os.path.join(fu, "books.txt"))}
    with open(os.path.join(fu, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("full D: %d tokens (news %d, Tatoeba %d, Vikidia %d, %d books %d = %.1f %%)"
          % (total, counts["news"], counts["tatoeba"], counts["vikidia"], len(books), counts["books"],
             100 * manifest["books_share"]))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", help="work folder (default: $PEPITE_CORPUS_WORK)")
    ap.add_argument("--out", help="output folder (default: <work>/corpus-light)")
    ap.add_argument("--books", default=os.path.join(HERE, "pd-books.tsv"))
    ap.add_argument("cmd", choices=["extract", "split", "heldout", "sample", "full"])
    ap.add_argument("--what", help="extract: vikidia,gutenberg (default both)")
    a = ap.parse_args(argv)
    fc = _fc()
    work = fc.work_dir(a.work)
    lock = fc.load_lock()
    return {"extract": cmd_extract, "split": cmd_split, "heldout": cmd_heldout, "sample": cmd_sample,
            "full": cmd_full}[a.cmd](a, lock, work)


if __name__ == "__main__":
    sys.exit(main())
