# SPDX-License-Identifier: GPL-3.0-or-later
# Pépite — invented dys sound-confusion cases (« cas inventés pour Pépite »). From src/:
#   python -I -X utf8 Predict.Eval/dys-sound/gen_dys_sound.py Predict.Eval/dys-sound
#   python -I -X utf8 Predict.Eval/dys-sound/gen_dys_sound.py Predict.Eval/dys-sound --verify Data.Build/out/prediction.sqlite
# The committed dys-sound-cases.json and dys-sound-spelling.json are its output byte for byte (plan dys-sound-tolerance,
# step 4); edit the tables here, never the output.
"""Writes dys-sound-cases.json (Predict.Eval curated cases) and dys-sound-spelling.json (--spelling cases).

A pupil who confuses a voiced and a voiceless consonant (ch/j, f/v, k/g, t/d, p/b) writes « chardin » for jardin. Each
typed form below has exactly one such sound swap. The swap patterns come from the literature cited in
development/studies/dys-sound-confusions.md ([R1], [R6], [R7]); every word and context is our own frequent child
vocabulary; there is no pupil text and nothing from any product (owner Q6, 2026-11-26).

Self-check: the script folds every form with a port of src/Predict.Core/PhoneticKey.Fold and keeps a misspelling only if
its key differs from the intended word's key in exactly one symbol of the pair (the engine's tolerant variants); the forms
dropped by that rule are printed (« ge » before a/o/u writes an extra e in the key, « qu »/« gu » before e/i keep a u). The
xUnit test SoundToleranceTests.Every_Full_Dys_Case_Is_One_Variant_Away checks the same on the C# fold.
"""
import json
import os
import sys
import unicodedata

OUT = sys.argv[1]
VERIFY = sys.argv[sys.argv.index("--verify") + 1] if "--verify" in sys.argv else None

# ---------------------------------------------------------------------------------------------- the words (our own)
# pair -> [(intended, context)] ; the context is a few words typed before (no capital, no final space)
WORDS = {}
WORDS["chj"] = [
    # intended spelt with j / g / ge -> typed with ch
    ("jardin", "dans le"), ("jambe", "il a mal à la"), ("jupe", "elle porte une"), ("jaune", "le soleil est"),
    ("jouer", "on va"), ("jour", "un beau"), ("jamais", "je ne sais"), ("déjà", "il est"), ("toujours", "il pleut"),
    ("bonjour", "elle dit"), ("manger", "il faut"), ("rouge", "le ballon est"), ("plage", "on va à la"),
    ("fromage", "du pain et du"), ("visage", "elle lave son"), ("image", "il regarde une"), ("orange", "elle mange une"),
    ("neige", "il y a de la"), ("girafe", "au zoo la"), ("gilet", "il met son"), ("genou", "il a mal au"),
    ("gentil", "il est très"), ("page", "tourne la"), ("nuage", "un gros"), ("voyage", "un long"), ("joli", "un"),
    ("jeudi", "à"), ("journal", "il lit le"), ("juste", "c'est"), ("magie", "un tour de"),
    # intended spelt with ch -> typed with j / g / ge
    ("chat", "le"), ("chien", "mon"), ("chocolat", "un gâteau au"), ("cheval", "il monte à"), ("chaise", "assis sur une"),
    ("chapeau", "il porte un"), ("chanter", "elle aime"), ("chambre", "dans ma"), ("chose", "une autre"),
    ("chaud", "il fait"), ("chercher", "il va"), ("machine", "une"), ("vache", "la"), ("poche", "dans ma"),
    ("mouche", "une"), ("riche", "il est"), ("marché", "au"), ("cochon", "le"), ("dimanche", "le"), ("douche", "sous la"),
    ("chemin", "sur le"), ("chaussure", "une"), ("chemise", "une"), ("cherche", "il"),
]
WORDS["fv"] = [
    ("voiture", "la"), ("vélo", "mon"), ("fleur", "une"), ("feu", "le"), ("faim", "j'ai"), ("fenêtre", "la"),
    ("forêt", "dans la"), ("fusée", "une"), ("fraise", "une"), ("frère", "mon"), ("valise", "une"), ("vert", "le pull est"),
    ("voir", "je veux"), ("vite", "il court"), ("facile", "c'est"), ("fête", "la"), ("film", "un"), ("ferme", "à la"),
    ("livre", "un"), ("rivière", "la"), ("café", "un"), ("famille", "ma"), ("vacances", "les"), ("vendredi", "le"),
    ("devoir", "un"),
]
WORDS["kg"] = [
    ("grogne", "le chien"), ("gâteau", "un"), ("canard", "le"), ("cadeau", "un"), ("garçon", "un"), ("gare", "à la"),
    ("glace", "une"), ("goûter", "le"), ("gros", "un"), ("gris", "un chat"), ("carte", "une"), ("crayon", "un"),
    ("classe", "la"), ("couleur", "une"), ("cuisine", "la"), ("école", "à l'"), ("sucre", "du"), ("camion", "un"),
    ("gagner", "il veut"), ("regarder", "il aime"), ("bague", "une"), ("cour", "dans la"), ("copain", "mon"),
    ("grand", "il est"), ("calcul", "un"),
]
WORDS["td"] = [
    ("dormir", "il va"), ("dessin", "un"), ("dragon", "un"), ("droite", "à"), ("tête", "la"), ("table", "sur la"),
    ("tomate", "une"), ("tortue", "une"), ("tasse", "une"), ("train", "le"), ("tarte", "une"), ("toit", "le"),
    ("tigre", "un"), ("tambour", "un"), ("dauphin", "un"), ("dîner", "le"), ("idée", "une"), ("radio", "la"),
    ("salade", "une"), ("été", "en"), ("dimanche", "le"), ("docteur", "le"), ("tirer", "il faut"), ("porte", "la"),
    ("étoile", "une"),
]
WORDS["pb"] = [
    ("boulanger", "le"), ("bateau", "un"), ("ballon", "un"), ("banane", "une"), ("bébé", "un"), ("bleu", "le ciel est"),
    ("bras", "le"), ("bonbon", "un"), ("bouteille", "une"), ("pomme", "une"), ("papa", "mon"), ("pantalon", "un"),
    ("piano", "un"), ("pied", "le"), ("prince", "le"), ("pluie", "la"), ("lapin", "un"), ("soupe", "la"),
    ("bifteck", "un"), ("bureau", "le"), ("robe", "une"), ("poupée", "une"), ("petit", "un"), ("parc", "au"),
    ("bougie", "une"),
]

# literature patterns (each typed form with its source); added to the generated forms of its pair
LIT = [
    ("chj", "visache", "visage", "elle lave son"),   # [R7] Eduscol 2026, « visache » for visage
    ("chj", "chardin", "jardin", "dans le"),          # user feedback 2026-11-26 (ch for j)
    ("chj", "jien", "chien", "mon"),                  # user feedback (j for ch)
    ("chj", "gien", "chien", "mon"),                  # user feedback (g for ch)
    ("chj", "mancher", "manger", "il faut"),          # user feedback (ch for ge)
    ("kg", "crogne", "grogne", "le chien"),           # [R6] « grogne → crogne »
    ("pb", "pifteck", "bifteck", "un"),               # [R1] Jaffré 1992 (« bifteck » → PIFK)
]

# real-word swaps (information, not gated): (pair, typed real word, intended, context). gomme / comme: [R7]
REAL = [
    ("chj", "bouche", "bouge", "il"), ("chj", "chou", "joue", "il"), ("chj", "cache", "cage", "dans sa"),
    ("chj", "sache", "sage", "il est"), ("chj", "manche", "mange", "il"), ("chj", "marche", "marge", "dans la"),
    ("chj", "chez", "j'ai", "il"),
    ("fv", "ville", "fille", "une"), ("fv", "fille", "ville", "la"), ("fv", "vent", "faon", "le"),
    ("kg", "comme", "gomme", "une"), ("kg", "gomme", "comme", "il fait"), ("kg", "car", "gare", "à la"),
    ("td", "tent", "dent", "une"), ("td", "doit", "toit", "le"), ("td", "dort", "tort", "il a"),
    ("pb", "poisson", "boisson", "une"), ("pb", "bain", "pain", "du"), ("pb", "boule", "poule", "une"),
    # generated swaps that --verify found in the lexicon (2026-11-26): real words, so information only
    ("fv", "cavé", "café", "un"), ("fv", "vête", "fête", "la"), ("kg", "care", "gare", "à la"),
    ("kg", "coûter", "goûter", "le"), ("kg", "cris", "gris", "un chat"), ("kg", "gour", "cour", "dans la"),
    ("pb", "pureau", "bureau", "le"), ("td", "drain", "train", "le"), ("td", "ratio", "radio", "la"),
]
REAL_FORMS = {(p, t) for p, t, _, _ in REAL}

# ---------------------------------------------------------------------------------------------- a port of PhoneticKey.Fold
SOUNDED, SCHWA, OE = "\x01", "\x02", "\x03"


def _vowel(c):
    return c in (SOUNDED, OE) or (c != "" and c in "aeiouy")


def _prepare(word):
    out = []
    for c in word.lower():
        if ord(c) < 0x80:
            out.append(c)
        elif c in "éèêë":
            out.append(SOUNDED)
        elif c in "àâä":
            out.append("a")
        elif c in "îï":
            out.append("i")
        elif c in "ôö":
            out.append("o")
        elif c in "ùûü":
            out.append("u")
        elif c == "ÿ":
            out.append("y")
        elif c == "ç":
            out.append("s")
        elif c == "œ":
            out.append(OE)
        elif c == "æ":
            out.append("e")
        else:
            out.append("".join(x for x in unicodedata.normalize("NFD", c) if unicodedata.category(x) != "Mn"))
    return "".join(out)


def _finish(o):
    plain = lambda c: "e" if c in (SOUNDED, SCHWA) else c
    r = []
    for ch in o:
        if r and plain(r[-1]) == plain(ch):
            if ch == SOUNDED:
                r[-1] = SOUNDED
            elif ch == "e" and r[-1] == SCHWA:
                r[-1] = "e"
            continue
        r.append(ch)
    while len(r) > 1 and r[-1] in ("e", "s", SCHWA):
        r.pop()
    return "".join("e" if c in (SOUNDED, SCHWA) else c for c in r)


def fold(word):
    w = _prepare(word)
    n = len(w)
    o = []

    def nasal(j):
        return j >= n or (not _vowel(w[j]) and w[j] not in "nm")

    i = 0
    while i < n:
        a0 = w[i]
        a1 = w[i + 1] if i + 1 < n else ""
        a2 = w[i + 2] if i + 2 < n else ""
        if a2 and a0 == "e" and a1 == "a" and a2 == "u":
            o.append("o"); i += 3; continue
        if a2 and a0 in "ae" and a1 == "i" and a2 in "nm":
            o.append("E"); i += 3; continue
        if a1:
            p = a0 + a1
            simple = {"au": "o", "ou": "u", "eu": SOUNDED, "oe": SOUNDED, "ai": SOUNDED, "ei": SOUNDED, "oi": "wa",
                      "ph": "f", "ch": "C", "gn": "N", "qu": "k"}
            if p in simple:
                o.extend(simple[p]); i += 2; continue
            nas = {"an": "A", "am": "A", "en": "A", "em": "A", "on": "O", "om": "O", "un": "U", "um": "U",
                   "in": "E", "im": "E", "yn": "E", "ym": "E"}
            if p in nas and nasal(i + 2):
                o.append(nas[p]); i += 2; continue
        c = w[i]
        nx = w[i + 1] if i + 1 < n else ""
        soft = nx != "" and (nx in "eiy" or nx == SOUNDED)
        if c == "c":
            o.append("s" if soft else "k")
        elif c == "g":
            o.append("j" if soft else "g")
        elif c == "q":
            o.append("k")
        elif c == "s":
            o.append("z" if o and _vowel(o[-1]) and _vowel(nx) else "s")
        elif c == "h":
            pass
        elif c == "y":
            o.append("i")
        elif c == "w":
            o.append("v")
        elif c == "x":
            o.extend("ks")
        elif c == OE:
            o.append(SOUNDED)
        else:
            o.append(c)
        i += 1
    return _finish(o)


PARTNER = {"chj": {"C": "j", "j": "C"}, "fv": {"f": "v", "v": "f"}, "kg": {"k": "g", "g": "k"},
           "td": {"t": "d", "d": "t"}, "pb": {"p": "b", "b": "p"}}


TOLERANT_FROM = 3   # PredictionEngine.TolerantFrom: the typed key needs this many symbols for the tolerant pass


def reachable(prefix, intended, pair):
    """A typed prefix the tolerant pass can reach (owner 2026-11-26): its key has TOLERANT_FROM symbols or more and, with
    one symbol of the pair swapped, is a prefix of the intended word's key (the prefix cuts no grapheme: « jerc » for
    chercher folds to « jerk », « cham » for jambe to « CA »)."""
    k, f = fold(prefix), fold(intended)
    return len(k) >= TOLERANT_FROM and any(
        f.startswith(k[:i] + PARTNER[pair][k[i]] + k[i + 1:]) for i in range(len(k)) if k[i] in PARTNER[pair])


def one_swap(typed, intended, pair):
    """True when the keys differ in exactly one symbol, and that symbol is a swap of the pair."""
    a, b = fold(typed), fold(intended)
    if len(a) != len(b):
        return False
    diff = [(x, y) for x, y in zip(a, b) if x != y]
    return len(diff) == 1 and PARTNER[pair].get(diff[0][0]) == diff[0][1]


# ---------------------------------------------------------------------------------------------- grapheme-level swap rules
SOFT = "eiyéèêë"
HARD = "aouâàôûù"
VOWELS = "aeiouyéèêëàâîïôöùûü"


def swaps(word, pair):
    """The misspellings of `word` with its first sound of `pair` swapped: [(typed, end)], `end` = the index in `typed`
    just after the swapped grapheme (a prefix of that many letters contains the whole swap)."""
    w = word
    out = []
    for i in range(len(w)):
        c, nx = w[i], (w[i + 1] if i + 1 < len(w) else "")
        nn = w[i + 2] if i + 2 < len(w) else ""
        if pair == "chj":
            if c == "c" and nx == "h":
                rest = w[i + 2:]
                if rest == "e" and i + 3 == len(w):           # final « che » -> « ge », « je »
                    out += [(w[:i] + "ge", i + 2), (w[:i] + "je", i + 1)]
                else:
                    out.append((w[:i] + "j" + rest, i + 1))   # ch -> j
                    if nn and nn in SOFT:
                        out.append((w[:i] + "g" + rest, i + 1))   # ch -> g before e/i/y
                    elif nn and nn in HARD:
                        out.append((w[:i] + "ge" + rest, i + 2))  # ch -> ge before a/o/u (dropped by the fold check)
                return out
            if c == "j":
                return [(w[:i] + "ch" + w[i + 1:], i + 2)]
            if c == "g" and nx and nx in SOFT:
                if nx == "e" and nn and nn in HARD:              # « ge » before a/o/u -> ch
                    return [(w[:i] + "ch" + w[i + 2:], i + 2)]
                return [(w[:i] + "ch" + w[i + 1:], i + 2)]
        elif pair == "fv":
            if c == "p" and nx == "h":
                return [(w[:i] + "v" + w[i + 2:], i + 1)]
            if c == "f":
                return [(w[:i] + "v" + w[i + 1:], i + 1)]
            if c == "v":
                return [(w[:i] + "f" + w[i + 1:], i + 1)]
        elif pair == "kg":
            if c == "c" and nx != "h" and (nx == "" or nx not in SOFT):
                if nx and (nx in HARD or nx in "rl"):
                    return [(w[:i] + "g" + w[i + 1:], i + 1)]
            if c == "q" and nx == "u":
                if nn and nn in "ao":
                    return [(w[:i] + "g" + w[i + 2:], i + 1)]
                return [(w[:i] + "gu" + w[i + 2:], i + 2)]      # qu(e/i) -> gu (dropped by the fold check)
            if c == "k":
                return [(w[:i] + "g" + w[i + 1:], i + 1)]
            if c == "g" and nx != "n":
                if nx == "u" and nn and nn in "ei":
                    return [(w[:i] + "qu" + w[i + 2:], i + 2)]  # gu(e/i) -> qu (dropped by the fold check)
                if nx and (nx in HARD or nx in "rl"):
                    return [(w[:i] + "c" + w[i + 1:], i + 1)]
        elif pair in ("td", "pb"):
            a, b = ("t", "d") if pair == "td" else ("p", "b")
            sounded = nx != "" and (nx in VOWELS or nx in "rl") and not (pair == "pb" and c == "p" and nx == "h")
            if c in (a, b) and sounded:
                return [(w[:i] + (b if c == a else a) + w[i + 1:], i + 1)]
    return out


# ---------------------------------------------------------------------------------------------- the cases
cases, spelling, dropped = [], [], []
seen = set()


def add(cat, ctx, typed, intended):
    # each with and without the context; the order alternates from one typed form to the next, so that each split (even
    # ids = dev, odd = test) holds both kinds, half and half
    order = (ctx, None) if len(cases) % 4 == 0 else (None, ctx)
    for c in order:
        cases.append({"id": len(cases) + 1, "category": cat, "context": c, "typed": typed, "intended": intended})


def add_form(pair, typed, end, intended, ctx):
    key = (pair, typed, intended)
    if key in seen:
        return
    seen.add(key)
    if (pair, typed) in REAL_FORMS:          # a real word: in the -real category instead
        return
    if not one_swap(typed, intended, pair):
        dropped.append(f"{pair}: {typed} -> {intended} ({fold(typed)} / {fold(intended)})")
        return
    add(f"{pair}-full", ctx, typed, intended)
    for k in (4, 3):
        if end <= k < len(typed):
            # gated: the reachable prefixes; « -cut »: the others, reported apart (owner 2026-11-26)
            add(f"{pair}-p{k}" + ("" if reachable(typed[:k], intended, pair) else "-cut"), ctx, typed[:k], intended)
    spelling.append({"cat": f"dys-{pair}", "text": f"{ctx} {typed} ", "expect": "sounds", "target": intended})


PAIRS = ["chj", "fv", "kg", "td", "pb"]
for pair in PAIRS:
    for intended, ctx in WORDS[pair]:
        for typed, end in swaps(intended, pair):
            add_form(pair, typed, end, intended, ctx)
    for p, typed, intended, ctx in LIT:
        if p == pair:
            end = next((i + 1 for i in range(len(typed)) if typed[i] != intended[i:i + 1]), len(typed))
            add_form(pair, typed, end + 1, intended, ctx)
    for p, typed, intended, ctx in REAL:
        if p == pair:
            add(f"{pair}-real", ctx, typed, intended)
            spelling.append({"cat": f"dys-{pair}-real", "text": f"{ctx} {typed} ", "expect": "known", "target": None})
    for intended, ctx in WORDS[pair]:
        add(f"{pair}-guard", ctx, intended, intended)
        if len(intended) > 3:
            add(f"{pair}-guard", ctx, intended[:3], intended)
        spelling.append({"cat": "dys-guard", "text": f"{ctx} {intended} ", "expect": "known", "target": None})

for i, s in enumerate(spelling):
    s["id"] = i + 1

os.makedirs(OUT, exist_ok=True)
with open(os.path.join(OUT, "dys-sound-cases.json"), "w", encoding="utf-8", newline="\n") as f:
    f.write("[\n" + ",\n".join("  " + json.dumps(c, ensure_ascii=False) for c in cases) + "\n]\n")
with open(os.path.join(OUT, "dys-sound-spelling.json"), "w", encoding="utf-8", newline="\n") as f:
    f.write("[\n" + ",\n".join("  " + json.dumps({k: s[k] for k in ("id", "cat", "text", "expect", "target") if s[k] is not None},
                                                 ensure_ascii=False) for s in spelling) + "\n]\n")

counts = {}
for c in cases:
    counts[c["category"]] = counts.get(c["category"], 0) + 1
print(len(cases), "cases,", len(spelling), "spelling cases:", ", ".join(f"{k} {v}" for k, v in counts.items()))
if dropped:
    print("dropped by the fold check (not one swap in the key):\n  " + "\n  ".join(dropped))

if VERIFY:
    # read-only: the generated misspellings that are lexicon words (move them to REAL by hand; never changes the output)
    import sqlite3
    con = sqlite3.connect("file:" + VERIFY.replace("\\", "/") + "?mode=ro", uri=True)
    lex = {r[0].lower() for r in con.execute("SELECT ortho FROM words")}
    con.close()
    real = sorted({(c["category"], c["typed"], c["intended"]) for c in cases
                   if c["category"].endswith("-full") and c["typed"].lower() in lex})
    print(f"--verify: {len(real)} generated misspellings are lexicon words" + (":" if real else ""))
    for cat, t, i in real:
        print(f"  {cat}: {t} (for {i})")
