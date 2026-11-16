#!/usr/bin/env python3
"""The grammar test sets G-synthetic and G-clean of plan 11 (10D-0), built into ``<work>/grammar-sets/``.

Test text only, never committed nor shipped (plan 11 Q4):
- French Wikipedia articles on school topics (text CC BY-SA 4.0), not the 20 articles of the lesson-domain set;
- Vikidia articles on the same topics (CC BY-SA 3.0 + GFDL; not GPL-3-compatible, hence test-only);
- not Tatoeba: it is in the n-gram training data, so injected sentences would be memorised.

**G-synthetic** (5 000 sentences): one error injected per sentence, a second one in 20 % of them. Every substitution is a
lexicon form that **sounds the same** (equal Lexique pronunciation) and is labelled with its class:
``num`` (N/A number: maisons → maison), ``gender`` (A gender: jolie → joli), ``person`` (finite person: mangent →
mange), ``qy2p`` (mangé ↔ manger ↔ mangez), ``qgn`` (participle gender / number: allée → allé), ``family`` (a ↔ à,
est ↔ et, sont ↔ son, ont ↔ on, ces ↔ ses ↔ sait). A token whose sound-alike siblings fall in two classes (« portes »:
noun or verb), or also a function word (« avant », « une »), is never used, nor a sibling reached through two
classes: the label would be a guess.
**G-clean** (10 000): 5 000 untouched sentences of the same articles, the first 2 500 sentences of
``heldout_subs_40k.txt`` and 2 500 of ``heldout_wiki_8k.txt`` that pass the same sentence filter.

Sub-commands (``python -I``, standard library only):

    fetch --out DIR                      the articles' plain-text extracts (MediaWiki API, one request at a time, rate
                                         limited) -> DIR/raw/<site>/*.json, plus the sites' licence info (rightsinfo)
    build --out DIR --grammar G --db P --heldout-subs F --heldout-wiki F
                                         -> DIR/g-synthetic.jsonl, DIR/g-clean.jsonl, DIR/manifest.json
    check FILE.jsonl ...                 counts per class; text differs from gold only at the spans; no duplicates

Format (jsonl, one object per line): {"id", "src", "licence", "text", "gold", "errors": [{"start", "end", "typed",
"gold", "class"}]} — ``start`` / ``end`` index ``text`` (the sentence as the pupil typed it), ``gold`` is the original.
Downloaded files are data: parsed as JSON, never executed.
"""
import argparse
import datetime
import hashlib
import json
import os
import random
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_grammar as G  # noqa: E402
from build_lesson_set import TOPICS as LESSON_TOPICS  # noqa: E402  (excluded: they are the lesson-domain set)

SEED = 20261115
USER_AGENT = G.USER_AGENT          # no personal data: the project's provisional public URL (D18)
DELAY = 1.5                    # seconds between two requests (one at a time)
CHARS = 40000                  # first characters of an article used (long articles would dominate)
PER_ARTICLE = 150              # at most this many sentences per article and site
N_SYNTH, N_CLEAN_OWN, N_HELDOUT = 5000, 5000, 2500
SECOND = 0.2                   # share of the synthetic sentences with a second error

SITES = {
    "wikipedia": {"api": "https://fr.wikipedia.org/w/api.php", "licence": "CC BY-SA 4.0",
                  "terms": ["https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use",
                            "https://fr.wikipedia.org/wiki/Wikip%C3%A9dia:Citation_et_r%C3%A9utilisation_du_contenu_de_Wikip%C3%A9dia"]},
    "vikidia": {"api": "https://fr.vikidia.org/w/api.php", "licence": "CC BY-SA 3.0 + GFDL",
                "terms": ["https://fr.vikidia.org/wiki/Vikidia:Droit_d%27auteur",
                          "https://fr.vikidia.org/wiki/Vikidia:R%C3%A9utilisation_du_contenu_de_Vikidia"]},
}

# school topics (CE2–6e), none of the lesson-domain set's 20
TOPICS = [
    "Lune", "Soleil", "Terre", "Mars (planète)", "Jupiter (planète)", "Étoile", "Comète", "Océan", "Mer Méditerranée",
    "Fleuve", "Montagne", "Désert", "Savane", "Banquise", "Climat", "Pluie", "Nuage", "Orage", "Neige", "Saison", "Arbre",
    "Fleur", "Graine", "Champignon", "Insecte", "Fourmi", "Papillon", "Araignée", "Escargot", "Grenouille", "Serpent",
    "Crocodile", "Tortue", "Oiseau", "Aigle", "Chouette", "Dauphin", "Requin", "Poisson", "Loup", "Renard", "Ours brun",
    "Éléphant", "Lion", "Girafe", "Cheval", "Vache", "Chat", "Chien", "Lapin", "Mammouth", "Cœur", "Dent", "Œil",
    "Muscle", "Sommeil", "Alimentation", "Eau", "Feu", "Pétrole", "Recyclage", "Pollution", "Aimant", "Lumière",
    "Moulin à vent", "Roue", "Imprimerie", "Écriture", "Alphabet", "Rome antique", "Grèce antique", "Gaule", "Moyen Âge",
    "Chevalier", "Pyramide", "Pharaon", "Pompéi", "Jules César", "Charlemagne", "Louis XIV", "Napoléon Ier",
    "Jeanne d'Arc", "Christophe Colomb", "Première Guerre mondiale", "Seconde Guerre mondiale", "Europe", "Afrique",
    "Loire", "Alpes", "Amazone (fleuve)", "Football", "Musique", "Peinture", "Théâtre", "Cirque", "Jeux olympiques",
    "Pain", "Chocolat", "Pomme", "Blé", "Riz", "Agriculture", "École", "Livre", "Bibliothèque", "Conte", "Train", "Avion",
    "Bateau", "Bicyclette", "Fusée", "Château de Versailles", "Tour Eiffel", "Jardin", "Forêt", "Abeille", "Cerveau",
]
assert not set(TOPICS) & set(LESSON_TOPICS)

FAMILY_SWAPS = {"a": ["à"], "à": ["a"], "est": ["et"], "et": ["est"], "sont": ["son"], "son": ["sont"],
                "ont": ["on"], "on": ["ont"], "ces": ["ses", "sait"], "ses": ["ces", "sait"], "sait": ["ces", "ses"]}
CLASSES = ("num", "gender", "person", "qy2p", "qgn", "family")
# G-invented (committed, src/Predict.Eval/grammar/g-invented.jsonl) is labelled with the P-agreement categories
CATEGORIES = ("det-num", "adj-gender", "post-adj", "subj-verb", "noun-subj", "etre-part", "avoir-ppas", "prep-inf",
              "modal-inf", "fam-a", "fam-et", "fam-son", "fam-on", "fam-ces")
TENSES = {"Ip", "Iq", "Is", "If", "Sp", "Sq", "K", "E"}
CONTENT = {G.POS_N, G.POS_A, G.POS_FINITE, G.POS_Q, G.POS_Y, G.POS_P}
WORD = re.compile(r"[A-Za-zÀ-ÖØ-öø-ÿŒœ]+")
ALLOWED = re.compile(r"^[A-Za-zÀ-ÖØ-öø-ÿŒœ ,;:!?'’«»\"\-\.…]+$")


# ---------------------------------------------------------------- fetch (polite: one request at a time)
def api_get(api, params):
    q = urllib.parse.urlencode(dict(params, format="json", formatversion=2))
    req = urllib.request.Request(api + "?" + q, headers={"User-Agent": USER_AGENT})
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code not in (429, 503) or attempt == 5:
                raise
            time.sleep(15 * (attempt + 1))


def raw_path(out, site, title):
    return os.path.join(out, "raw", site, hashlib.sha256(title.encode("utf-8")).hexdigest()[:16] + ".json")


def cmd_fetch(a):
    info = {}
    for site, s in SITES.items():
        os.makedirs(os.path.join(a.out, "raw", site), exist_ok=True)
        r = api_get(s["api"], {"action": "query", "meta": "siteinfo", "siprop": "rightsinfo"})
        info[site] = {"rightsinfo": r["query"]["rightsinfo"], "read": datetime.date.today().isoformat()}
        time.sleep(DELAY)
        for title in TOPICS:
            p = raw_path(a.out, site, title)
            if os.path.exists(p):
                continue
            r = api_get(s["api"], {"action": "query", "prop": "extracts|revisions", "rvprop": "ids|timestamp",
                                   "explaintext": 1, "redirects": 1, "titles": title})
            page = r["query"]["pages"][0]
            rec = {"site": site, "asked": title, "title": page.get("title", title), "missing": "missing" in page,
                   "revid": page["revisions"][0]["revid"] if page.get("revisions") else None,
                   "timestamp": page["revisions"][0]["timestamp"] if page.get("revisions") else None,
                   "extract": page.get("extract", "")}
            with open(p + ".part", "w", encoding="utf-8") as f:
                json.dump(rec, f, ensure_ascii=False)
            os.replace(p + ".part", p)
            print("%s %-28s %s" % (site, title, "missing" if rec["missing"] else rec["revid"]))
            time.sleep(DELAY)
    with open(os.path.join(a.out, "raw", "sites.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, ensure_ascii=False, indent=1)


# ---------------------------------------------------------------- sentences
def sentences(text):
    """Sentences of 5–25 words, letters and punctuation only (no digit, no bracket, no abbreviation dot inside),
    starting with a capital and ending with . ! ? …; headings dropped."""
    out = []
    for line in text.splitlines():
        line = line.strip()
        if not line or re.match(r"^=+.*=+$", line):
            continue
        for s in re.split(r"(?<=[.!?…])\s+(?=[«\"A-ZÀ-ÖØ-ÞŒ])", line):
            s = s.strip()
            if ok_sentence(s):
                out.append(s)
    return out


def ok_sentence(s):
    if not s or not ALLOWED.match(s) or not s[0].isupper() or s[-1] not in ".!?…":
        return False
    if "." in s[:-1] or "  " in s:
        return False
    n = len(s.split())
    return 5 <= n <= 25


# ---------------------------------------------------------------- morphology for the injection
class Morph:
    """Sound-alike siblings of lexicon forms: another form of the same lemma, in the prediction lexicon, with the same
    Lexique pronunciation, whose analysis differs in exactly one of the classes."""

    def __init__(self, rows, phon, lexicon):
        self.phon = phon                      # form -> Lexique phon (prediction.sqlite words)
        self.lexicon = lexicon                # forms of the prediction lexicon (lowercase)
        self.by_form, self.by_lemma = {}, {}
        for form, lemma, tag in rows:
            self.by_form.setdefault(form, []).append((lemma, tag))
            self.by_lemma.setdefault(lemma, set()).add(form)
        self.known = set(self.by_form)
        self._cache = {}

    def lex_form(self, w):
        return G.lookup(w, self.known)

    def siblings(self, w):
        """[(sibling, class)] sorted, for a lowercase word; [] when none or ambiguous."""
        if w in self._cache:
            return self._cache[w]
        res = []
        if w in FAMILY_SWAPS:
            res = [(s, "family") for s in FAMILY_SWAPS[w] if s in self.lexicon]
        else:
            res = self._lemma_siblings(w)
        self._cache[w] = res
        return res

    def _lemma_siblings(self, w):
        lf = self.lex_form(w)
        if lf is None or w not in self.lexicon or not self.phon.get(w):
            return []
        # a form that is both a noun / adjective and a verb (« portes », « allée », « manger ») is left out: which of
        # them the sentence uses is a guess, and so would be the class
        kinds = {re.match(r"^:V[0-3]", t) is not None for _, t in self.by_form[lf]}
        if len(kinds) > 1:
            return []
        # nor a form that is also a function word (« avant » preposition, « une » determiner): only content words
        if any(b & G.POS_MASK not in CONTENT for _, t in self.by_form[lf] for b in G.decode(t)):
            return []
        found = {}
        for lemma, tag in self.by_form[lf]:
            for s in sorted(self.by_lemma.get(lemma, ())):
                if s == lf or s not in self.lexicon or s in FAMILY_SWAPS or self.phon.get(s) != self.phon[w]:
                    continue
                for slemma, stag in self.by_form[s]:
                    if slemma != lemma:
                        continue
                    c = classify(tag, stag)
                    if c:
                        found.setdefault(s, set()).add(c)
        # a sibling reached through two classes is dropped (its label would be a guess)
        return [(s, next(iter(cs))) for s, cs in sorted(found.items()) if len(cs) == 1]


def _kind(tag):
    """(verbal kind, POS set, gender, number, tenses, persons) of a tag."""
    fs = G.fields(tag)
    pos = {b & G.POS_MASK for b in G.decode(tag)}
    gender = {f for f in fs if f in ("m", "f", "e")}
    number = {f for f in fs if f in ("s", "p", "i")}
    tenses = {f for f in fs if f in TENSES}
    persons = {f.rstrip("!") for f in fs if f.rstrip("!") in G.PERSON}
    return pos, gender, number, tenses, persons


def classify(tag, stag):
    """The class of replacing a form of analysis ``tag`` by one of analysis ``stag`` (same lemma), or None."""
    p1, g1, n1, t1, s1 = _kind(tag)
    p2, g2, n2, t2, s2 = _kind(stag)

    def qy2(p, t, s):
        if G.POS_Q in p:
            return "Q"
        if G.POS_Y in p:
            return "Y"
        if G.POS_FINITE in p and "2p" in s and t & {"Ip", "E"}:
            return "2p"
        return None
    if G.POS_Q in p1 and G.POS_Q in p2:
        return "qgn" if (g1, n1) != (g2, n2) and "e" not in g1 | g2 and "i" not in n1 | n2 else None
    a, b = qy2(p1, t1, s1), qy2(p2, t2, s2)
    if a and b and a != b:
        return "qy2p"
    if G.POS_FINITE in p1 and G.POS_FINITE in p2 and t1 & t2 and not s1 <= s2 and not (a and b):
        return "person"
    for pos in (G.POS_N, G.POS_A):
        if pos in p1 and pos in p2 and G.POS_Q not in p1 | p2:
            if g1 == g2 and n1 != n2 and "i" not in n1 | n2:
                return "num"
            if pos == G.POS_A and n1 == n2 and g1 != g2 and "e" not in g1 | g2:
                return "gender"
    return None


def candidates(morph, text):
    """[(start, end, word, [(sibling, class)])] of the words of a sentence that have sound-alike siblings."""
    out = []
    for m in WORD.finditer(text):
        w = m.group(0)
        # a word glued to an apostrophe (« l'arbre » → « arbre », « qu'il » → « il ») is still a word; the elided
        # part before it is never replaced. Never a capitalised word inside the sentence (« Paris » is no « pari »),
        # an acronym, or a part of a hyphenated compound (« peut-être »).
        if m.start() > 0 and w[0].isupper() or len(w) > 1 and not w[1:].islower():
            continue
        if text[m.start() - 1:m.start()] == "-" or text[m.end():m.end() + 1] == "-":
            continue
        sib = morph.siblings(w.lower())
        if sib:
            out.append((m.start(), m.end(), w, sib))
    return out


def recase(model, s):
    return s[0].upper() + s[1:] if model[0].isupper() else s


def inject(rng, morph, text):
    """(typed text, errors) with one error, a second one in SECOND of the cases; None when no word can take one."""
    cands = candidates(morph, text)
    if not cands:
        return None
    n = 2 if rng.random() < SECOND and len(cands) >= 2 else 1
    chosen = []
    pool = list(cands)
    for _ in range(n):
        classes = sorted({c for (_, _, _, sib) in pool for _, c in sib})
        cls = rng.choice(classes)
        opts = [x for x in pool if any(c == cls for _, c in x[3])]
        st, en, w, sib = rng.choice(opts)
        typed = rng.choice([s for s, c in sib if c == cls])
        chosen.append((st, en, w, recase(w, typed), cls))
        pool = [x for x in pool if x[0] != st]
        if not pool:
            break
    chosen.sort()
    out, errors, pos, shift = [], [], 0, 0
    for st, en, w, typed, cls in chosen:
        out.append(text[pos:st])
        start = st + shift
        out.append(typed)
        errors.append({"start": start, "end": start + len(typed), "typed": typed, "gold": w, "class": cls})
        shift += len(typed) - (en - st)
        pos = en
    out.append(text[pos:])
    return "".join(out), errors


def make_sets(rng, morph, own, heldout_subs, heldout_wiki):
    """own: [(src, licence, sentence)] in a fixed order. Returns (synthetic, clean) lists of records."""
    order = list(range(len(own)))
    rng.shuffle(order)
    synth, rest = [], []
    for i in order:
        src, lic, s = own[i]
        if len(synth) < N_SYNTH:
            r = inject(rng, morph, s)
            if r is not None:
                text, errors = r
                synth.append({"id": "gs-%05d" % (len(synth) + 1), "src": src, "licence": lic, "text": text, "gold": s,
                              "errors": errors})
                continue
        rest.append((src, lic, s))
    clean = []
    for src, lic, s in rest[:N_CLEAN_OWN]:
        clean.append({"src": src, "licence": lic, "text": s, "gold": s, "errors": []})
    for name, lines in (("heldout-subs", heldout_subs), ("heldout-wiki", heldout_wiki)):
        for s in lines[:N_HELDOUT]:
            clean.append({"src": name, "licence": "held-out evaluation text (test only)", "text": s, "gold": s, "errors": []})
    for i, r in enumerate(clean, 1):
        r["id"] = "gc-%05d" % i
    return synth, [{k: r[k] for k in ("id", "src", "licence", "text", "gold", "errors")} for r in clean]


def heldout_sentences(path, seen):
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            s = line.strip()
            if ok_sentence(s) and s not in seen:
                seen.add(s)
                out.append(s)
                if len(out) >= N_HELDOUT:
                    break
    return out


def write_jsonl(path, recs):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for r in recs:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    return G.SR.sha256(path)


def load_morph(grammar, db):
    con = sqlite3.connect("file:%s?mode=ro" % grammar.replace("\\", "/"), uri=True)
    try:
        lemmas = [r[0] for r in con.execute("SELECT lemma FROM lemma ORDER BY id")]
        tags = [r[0] for r in con.execute("SELECT tag FROM tag ORDER BY id")]
        rows = [(f, lemmas[l], tags[t]) for f, l, t in con.execute("SELECT form, lemma_id, tag_id FROM morph")]
    finally:
        con.close()
    con = sqlite3.connect("file:%s?mode=ro" % db.replace("\\", "/"), uri=True)
    try:
        phon, lexicon = {}, set()
        for o, p in con.execute("SELECT ortho, phon FROM words"):
            lexicon.add(o.lower())
            phon[o.lower()] = p or ""
    finally:
        con.close()
    return Morph(rows, phon, lexicon)


def cmd_build(a):
    raw = os.path.join(a.out, "raw")
    with open(os.path.join(raw, "sites.json"), encoding="utf-8") as f:
        sites = json.load(f)
    own, seen, articles = [], set(), []
    for site in SITES:
        for title in TOPICS:
            p = raw_path(a.out, site, title)
            if not os.path.exists(p):
                continue
            with open(p, encoding="utf-8") as f:
                rec = json.load(f)
            if rec["missing"]:
                articles.append({"site": site, "asked": title, "missing": True})
                continue
            kept = 0
            for s in sentences(rec["extract"][:CHARS]):
                if s in seen:
                    continue
                seen.add(s)
                own.append(("%s:%s" % (site, rec["title"]), SITES[site]["licence"], s))
                kept += 1
                if kept >= PER_ARTICLE:
                    break
            articles.append({"site": site, "asked": title, "title": rec["title"], "revid": rec["revid"],
                             "timestamp": rec["timestamp"], "sentences": kept})
    morph = load_morph(a.grammar, a.db)
    hs = heldout_sentences(a.heldout_subs, seen)
    hw = heldout_sentences(a.heldout_wiki, seen)
    rng = random.Random(SEED)
    synth, clean = make_sets(rng, morph, own, hs, hw)
    out_s = os.path.join(a.out, "g-synthetic.jsonl")
    out_c = os.path.join(a.out, "g-clean.jsonl")
    sha_s, sha_c = write_jsonl(out_s, synth), write_jsonl(out_c, clean)
    manifest = {
        "plan": "11 (10D-0)", "built": datetime.date.today().isoformat(), "seed": SEED, "second_error_share": SECOND,
        "rule": "same-sound substitution (equal Lexique 3.83 phon of two forms of one French.lex lemma, or the family table)",
        "sites": {k: dict(sites.get(k, {}), licence=SITES[k]["licence"], terms=SITES[k]["terms"], api=SITES[k]["api"])
                  for k in SITES},
        "excluded": {"lesson-domain set": LESSON_TOPICS, "tatoeba": "in the n-gram training data"},
        "articles": articles,
        "heldout": {"subs": {"file": os.path.basename(a.heldout_subs), "sha256": G.SR.sha256(a.heldout_subs), "sentences": len(hs)},
                    "wiki": {"file": os.path.basename(a.heldout_wiki), "sha256": G.SR.sha256(a.heldout_wiki), "sentences": len(hw)}},
        "grammar_sqlite_sha256": G.SR.sha256(a.grammar),
        "outputs": {"g-synthetic.jsonl": {"sha256": sha_s, "sentences": len(synth), "classes": class_counts(synth)},
                    "g-clean.jsonl": {"sha256": sha_c, "sentences": len(clean),
                                      "by_src": count_src(clean)}},
        "own_sentences": len(own),
        "note": "test text only: never committed nor shipped (Vikidia CC BY-SA 3.0 + GFDL is not GPL-3-compatible)",
    }
    with open(os.path.join(a.out, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)
    print("own sentences %d; G-synthetic %d (%s) %s; G-clean %d %s" % (len(own), len(synth), class_counts(synth), sha_s,
                                                                        len(clean), sha_c))


def class_counts(recs):
    c = {}
    for r in recs:
        for e in r["errors"]:
            c[e["class"]] = c.get(e["class"], 0) + 1
    return dict(sorted(c.items()))


def count_src(recs):
    c = {}
    for r in recs:
        k = r["src"].split(":", 1)[0]
        c[k] = c.get(k, 0) + 1
    return dict(sorted(c.items()))


def check(path):
    """(problems, per-class counts, sentences) of one jsonl set."""
    problems, ids, texts, n = [], set(), set(), 0
    with open(path, encoding="utf-8") as f:
        for ln, line in enumerate(f, 1):
            if not line.strip():
                continue
            r = json.loads(line)
            if "note" in r and "text" not in r:          # the header line of a committed set
                continue
            n += 1
            for k in ("id", "src", "licence", "text", "gold", "errors"):
                if k not in r:
                    problems.append("line %d: no %r" % (ln, k))
            if r.get("id") in ids:
                problems.append("line %d: duplicate id %s" % (ln, r.get("id")))
            ids.add(r.get("id"))
            if r.get("text") in texts:
                problems.append("line %d: duplicate text" % ln)
            texts.add(r.get("text"))
            t, rebuilt, pos = r.get("text", ""), [], 0
            for e in sorted(r.get("errors", []), key=lambda e: e["start"]):
                if t[e["start"]:e["end"]] != e["typed"] or e["class"] not in CLASSES + CATEGORIES or e["start"] < pos:
                    problems.append("line %d: bad span %r" % (ln, e))
                rebuilt.append(t[pos:e["start"]])
                rebuilt.append(e["gold"])
                pos = e["end"]
            rebuilt.append(t[pos:])
            if "".join(rebuilt) != r.get("gold"):
                problems.append("line %d: text and gold differ outside the spans" % ln)
    counts = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                for e in json.loads(line).get("errors", []):
                    counts[e["class"]] = counts.get(e["class"], 0) + 1
    return problems, dict(sorted(counts.items())), n


def cmd_check(a):
    bad = 0
    for p in a.files:
        problems, counts, n = check(p)
        print("%s: %d sentences, %d errors %s, %d problems" % (os.path.basename(p), n, sum(counts.values()), counts, len(problems)))
        for x in problems[:20]:
            print("  " + x)
        bad += len(problems)
    return 1 if bad else 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("--out", required=True)
    b = sub.add_parser("build")
    b.add_argument("--out", required=True)
    b.add_argument("--grammar", default=os.path.join(HERE, "out", "grammar.sqlite"))
    b.add_argument("--db", default=os.path.join(HERE, "out", "prediction.sqlite"))
    b.add_argument("--heldout-subs", required=True)
    b.add_argument("--heldout-wiki", required=True)
    c = sub.add_parser("check")
    c.add_argument("files", nargs="+")
    a = p.parse_args(argv)
    return {"fetch": cmd_fetch, "build": cmd_build, "check": cmd_check}[a.cmd](a) or 0


if __name__ == "__main__":
    sys.exit(main())
