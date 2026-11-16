"""Liaison checks on the phonemes each engine actually synthesised (plan 05, D25; TODO « The voice does not
make French liaisons »).

A check is (item, pair, kind, consonant, left, right). On the *normalised* phoneme string of the item (stress, length,
syllable and word separators removed, close vowel variants merged, see ``normalise``):

- ``left + consonant + right`` found  -> liaison realised;
- ``left + right`` found              -> liaison not made (or the optional 7th field, when the left word keeps
                                        a final consonant of its own: « neuf ans » /nœfɑ̃/);
- neither                             -> the engine said something else there (« n/a »).

``kind``: ``todo`` = the pairs of the TODO list; ``obligatory`` = other obligatory liaisons of the test text;
``facultative`` = optional liaisons (reported, not scored); ``forbidden`` = a liaison here is an error (h aspiré, « et »).

When the engine reports phoneme timings, the duration of the liaison consonant is also read, as evidence that the
consonant is in the audio and not only in the front-end string.
"""

import re
import unicodedata

N = "̃"  # combining tilde (nasal vowel)
UN = f"(?:[øe]{N}|y)"  # « un »: œ̃ / ɛ̃, or denasalised

CHECKS = [
    ("L01", "les amis", "todo", "z", "le", "ami"),
    ("L02", "un enfant", "todo", "n", UN, f"a{N}fa{N}"),
    ("L03", "ils ont", "todo", "z", "il", f"o{N}fin"),
    ("L04", "deux heures", "todo", "z", "dø", "øʁ"),
    ("L05", "petit ami", "todo", "t", "pə?ti", "ami"),
    ("L06", "c'est un", "todo", "t", "se", UN + "b"),
    ("L06", "bon exemple", "obligatory", "n", f"bo{N}?", "eɡz"),
    ("L07", "nous avons", "obligatory", "z", "nu", f"avo{N}"),
    ("L07", "des idées", "obligatory", "z", "de", "ide"),
    ("L08", "mon ami", "obligatory", "n", f"mo{N}?", "ami"),
    ("L09", "trois enfants", "obligatory", "z", "tʁwa", f"a{N}fa{N}"),
    ("L10", "grand homme", "obligatory", "t", f"ɡʁa{N}", "om"),
    ("S05", "aux élèves", "obligatory", "z", "o", "elev"),
    ("S07", "nous allons", "obligatory", "z", "nu", f"alo{N}"),
    ("S11", "les enfants", "obligatory", "z", "le", f"a{N}fa{N}"),
    ("S15", "nous irons", "obligatory", "z", "nu", f"iʁo{N}"),
    ("H03", "on est", "obligatory", "n", f"o{N}?", f"eko{N}"),
    ("H03", "ils ont (gagné)", "obligatory", "z", "il", f"o{N}ɡ"),
    ("N03", "neuf ans", "obligatory", "v", "nø", f"a{N}", f"nøfa{N}"),  # without liaison: « neuf » keeps its /f/
    ("H02", "sont allés", "facultative", "t", f"so{N}", "ale"),
    ("S07", "allons à", "facultative", "z", f"alo{N}", "ala"),
    ("L11", "les héros", "forbidden", "z", "le", "eʁo"),
    ("L12", "(chat) et un", "forbidden", "t", "ʃae", UN + "ʃ"),
    # A liaison consonant before a consonant is always wrong (front-ends that pronounce latent consonants).
    # « petit » + consonant does not occur in the test text.
    ("S09", "les (mains)", "forbidden", "z", "le", f"me{N}"),
    ("H02", "ils (sont)", "forbidden", "z", "il", f"so{N}"),
]

_DROP = set("ˈˌ-ʲː‿.,;:!?|¡¿ _^$'\"«»()") | {"̩", "̯", "̆"}
_MAP = {"ɛ": "e", "ɑ": "a", "ɔ": "o", "œ": "ø", "ɐ": "a", "g": "ɡ", "r": "ʁ", "ʀ": "ʁ", "ɜ": "e", "ɒ": "o", "ʊ": "u"}


def normalise_chars(ph: str) -> list[str]:
    """NFD, then per input character: dropped ('') or mapped; keeps one output entry per input character."""
    out = []
    for c in unicodedata.normalize("NFD", ph):
        out.append("" if c in _DROP else _MAP.get(c, c))
    return out


def normalise(ph: str) -> str:
    return "".join(normalise_chars(ph))


def check(phonemes: dict[str, str], timed: dict[str, list] | None = None) -> list[dict]:
    """Run every check on {item id: phoneme string}; ``timed`` = {item id: [[phoneme, start, end], ...]}."""
    results = []
    for item, pair, kind, cons, left, right, *plain in CHECKS:
        ph = phonemes.get(item)
        res = {"item": item, "pair": pair, "kind": kind, "consonant": cons, "status": "missing", "dur_ms": None}
        if ph is None:
            results.append(res)
            continue
        norm = normalise(ph)
        with_l = re.search(f"{left}{cons}{right}", norm)
        without = re.search(plain[0] if plain else f"{left}{right}", norm)
        res["status"] = "made" if with_l else ("not made" if without else "n/a")
        if with_l and timed and item in timed:
            res["dur_ms"] = consonant_duration(timed[item], left, cons, right)
        results.append(res)
    return results


def consonant_duration(spans: list, left: str, cons: str, right: str) -> float | None:
    """Duration of the liaison consonant in the timed phoneme list (ms), or None if it cannot be located."""
    chars, owner = [], []
    for k, (ph, _s, _e) in enumerate(spans):
        for c in normalise_chars(ph):
            if c:
                chars.append(c)
                owner.append(k)
    m = re.search(f"({left})({cons})({right})", "".join(chars))
    if not m:
        return None
    k = owner[m.start(2)]
    return round((spans[k][2] - spans[k][1]) * 1000, 1)


def score(results: list[dict]) -> dict:
    def made(kinds):
        sel = [r for r in results if r["kind"] in kinds]
        return sum(r["status"] == "made" for r in sel), len(sel)

    todo = made({"todo"})
    expected = made({"todo", "obligatory"})
    forb = [r for r in results if r["kind"] == "forbidden"]
    fac = made({"facultative"})
    return {"todo": todo, "expected": expected, "facultative": fac,
            "forbidden_respected": (sum(r["status"] == "not made" for r in forb), len(forb)),
            "missed": [r["pair"] for r in results if r["kind"] in ("todo", "obligatory") and r["status"] != "made"],
            "wrong": [r["pair"] for r in forb if r["status"] == "made"]}
