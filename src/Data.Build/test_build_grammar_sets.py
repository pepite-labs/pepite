"""Unit tests for build_grammar_sets.py — run: python -I -m unittest discover -s src/Data.Build -p
test_build_grammar_sets.py (from the repo root). 21 invented sentences and tests/french_fixture.lex (French.lex lines,
MPL-2.0); nothing is downloaded."""
import json
import os
import random
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_grammar_sets as S  # noqa: E402
import build_spelling_ref as SR  # noqa: E402

FIXTURE = os.path.join(HERE, "tests", "french_fixture.lex")
# invented pronunciations in the Lexique style: « petite » does not sound like « petit », « mangée » sounds like « mangé »
PHON = {"maison": "mEz§", "maisons": "mEz§", "petit": "p@ti", "petite": "p@tit", "petits": "p@ti", "mange": "m@Z",
        "mangent": "m@Z", "mangez": "m@Ze", "mangé": "m@Ze", "mangée": "m@Ze", "manger": "m@Ze", "allée": "ale",
        "aller": "ale", "vais": "vE", "est": "E", "et": "e", "a": "a", "à": "a", "sont": "s§", "les": "le", "il": "il",
        "ils": "il", "gris": "gRi", "le": "l@", "vite": "vit", "porte": "pORt", "portes": "pORt", "portent": "pORt"}
SENTENCES = [
    "Les enfants mangent vite à la maison.",
    "Il est parti et il mange une pomme.",
    "Les petits chats dorment dans les maisons.",
    "Elle a mangé le gâteau de sa grand-mère.",
    "Nous allons aller au parc demain matin.",
    "Le petit garçon a un chapeau gris.",
    "Ils mangent leur goûter sous le grand arbre.",
    "La maison de Léa est au bout de la rue.",
    "Je vais manger avec mes amis ce soir.",
    "Il a une petite sœur et un grand frère.",
    "Les oiseaux chantent et le soleil brille.",
    "Ils sont allés à la mer pendant les vacances.",
    "Mon chien est très gentil avec les petits.",
    "Paris est une très grande ville de France.",
    "Elle mange une soupe chaude avant de dormir.",
    "Le chat a sauté sur la table de la cuisine.",
    "Peut-être que la pluie va bientôt tomber sur la ville.",
    "Le maître est content de nos dessins de maisons.",
    "Les fleurs du jardin sont belles au printemps.",
    "Il faut manger des fruits et des légumes.",
    "Les voisins portent des cartons et ferment les portes.",
]


def morph():
    rows = [(f, l, t.split("/", 1)[0]) for f, l, t in SR.read_lex(FIXTURE)]
    return S.Morph(rows, PHON, set(PHON))


class SiblingTests(unittest.TestCase):
    def test_same_sound_rule(self):
        m = morph()
        self.assertEqual(m.siblings("maisons"), [("maison", "num")])
        self.assertEqual(m.siblings("petite"), [])                       # petit sounds different: never injected
        self.assertEqual(m.siblings("petits"), [("petit", "num")])
        self.assertEqual(m.siblings("mangent"), [("mange", "person")])
        self.assertEqual(m.siblings("mangé"), sorted([("mangée", "qgn"), ("manger", "qy2p"), ("mangez", "qy2p")]))
        self.assertEqual(m.siblings("mangez"), sorted([("mangé", "qy2p"), ("mangée", "qy2p"), ("manger", "qy2p")]))
        self.assertEqual(m.siblings("est"), [("et", "family")])
        self.assertEqual(m.siblings("à"), [("a", "family")])
        self.assertEqual(m.siblings("vite"), [])

    def test_a_noun_that_is_also_a_verb_is_never_guessed(self):
        m = morph()
        self.assertEqual(m.siblings("portes"), [])     # noun plural or verb 2s: which class is a guess
        self.assertEqual(m.siblings("manger"), [])     # infinitive and noun
        self.assertEqual(m.siblings("portent"), [("porte", "person"), ("portes", "person")])

    def test_classify(self):
        self.assertEqual(S.classify(":N:f:p", ":N:f:s"), "num")
        self.assertEqual(S.classify(":A:f:s", ":A:m:s"), "gender")
        self.assertEqual(S.classify(":V1_it_x__a:Ip:Sp:3p", ":V1_it_x__a:Ip:Sp:1s:3s"), "person")
        self.assertEqual(S.classify(":V1_it_x__a:Q:A:m:s", ":V1_it_x__a:Y"), "qy2p")
        self.assertEqual(S.classify(":V1_it_x__a:Ip:2p", ":V1_it_x__a:Y"), "qy2p")
        self.assertEqual(S.classify(":V1_i__e_e_:Q:f:s", ":V1_i__e_e_:Q:m:s"), "qgn")
        self.assertIsNone(S.classify(":N:e:s", ":N:e:s"))


class InjectionTests(unittest.TestCase):
    def setUp(self):
        self.saved = (S.N_SYNTH, S.N_CLEAN_OWN, S.N_HELDOUT)
        S.N_SYNTH, S.N_CLEAN_OWN, S.N_HELDOUT = 12, 5, 2

    def tearDown(self):
        S.N_SYNTH, S.N_CLEAN_OWN, S.N_HELDOUT = self.saved

    def sets(self, seed=S.SEED):
        own = [("invented", "GPL-3.0-or-later", s) for s in SENTENCES]
        return S.make_sets(random.Random(seed), morph(), own, ["Une phrase de sous-titres pour le test."],
                           ["Une phrase encyclopédique pour le test ici."])

    def test_deterministic(self):
        self.assertEqual(json.dumps(self.sets()), json.dumps(self.sets()))
        self.assertNotEqual(json.dumps(self.sets()), json.dumps(self.sets(1)))

    def test_spans_exact_and_check_passes(self):
        synth, clean = self.sets()
        self.assertGreater(len(synth), 5)
        self.assertEqual(sum(1 for r in clean if r["src"].startswith("heldout")), 2)
        for r in synth:
            self.assertTrue(r["errors"])
            for e in r["errors"]:
                self.assertEqual(r["text"][e["start"]:e["end"]], e["typed"])
                self.assertNotEqual(e["typed"].lower(), e["gold"].lower())
                self.assertEqual(PHON.get(e["typed"].lower()) == PHON.get(e["gold"].lower()) or e["class"] == "family", True)
            self.assertNotIn("Pari ", r["text"])            # a capitalised name is never touched
            if "Peut-être" in r["gold"]:
                self.assertIn("Peut-être", r["text"])        # a part of a compound is never touched
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "s.jsonl")
            S.write_jsonl(p, synth + clean)
            problems, counts, n = S.check(p)
            self.assertEqual(problems, [])
            self.assertEqual(n, len(synth) + len(clean))
            self.assertEqual(sum(counts.values()), sum(len(r["errors"]) for r in synth))

    def test_check_finds_a_bad_span_and_a_duplicate(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "s.jsonl")
            good = {"id": "a", "src": "x", "licence": "y", "text": "Il et là.", "gold": "Il est là.",
                    "errors": [{"start": 3, "end": 5, "typed": "et", "gold": "est", "class": "family"}]}
            bad = dict(good, id="b", errors=[{"start": 2, "end": 5, "typed": "et", "gold": "est", "class": "family"}])
            S.write_jsonl(p, [good, bad])
            problems, _, _ = S.check(p)
            self.assertTrue(any("bad span" in x for x in problems))
            self.assertTrue(any("duplicate text" in x for x in problems))

    def test_sentence_filter(self):
        self.assertTrue(S.ok_sentence("Les enfants mangent vite à la maison."))
        self.assertFalse(S.ok_sentence("En 1789, la Révolution commence en France."))   # digits
        self.assertFalse(S.ok_sentence("Il dort."))                                       # fewer than 5 words
        self.assertFalse(S.ok_sentence("Le règne de César (av. J.-C.) est long et dur."))
        self.assertEqual(S.sentences("== Titre ==\nLe chat dort sur le lit. Il rêve de souris et de lait."),
                         ["Le chat dort sur le lit.", "Il rêve de souris et de lait."])


if __name__ == "__main__":
    unittest.main()
