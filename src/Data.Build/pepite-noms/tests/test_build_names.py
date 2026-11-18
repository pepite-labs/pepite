#!/usr/bin/env python3
"""Unit tests of the pure transforms of build_names.py (plan 10C). Run: python -I -m unittest discover -s tests"""
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import build_names as bn  # noqa: E402


class Helpers(unittest.TestCase):
    def test_valid_name(self):
        for ok in ("Belgique", "La Louvière", "Jeanne d'Arc", "J. R. R. Tolkien", "Saint-Gilles", "Côte d'Ivoire", "Lé"):
            self.assertTrue(bn.valid_name(ok), ok)
        for bad in ("belgique", "SNCB", "Louis 14", "Paris (France)", "-Paris", "A", "", "X" * 41, "R.A.S. chinoise"):
            self.assertFalse(bn.valid_name(bad), bad)

    def test_title_case_and_clean(self):
        self.assertEqual(bn.title_case("JEAN-PIERRE"), "Jean-Pierre")
        self.assertEqual(bn.title_case("N'GOLO"), "N'Golo")
        self.assertEqual(bn.title_case("LÉA"), "Léa")
        self.assertEqual(bn.clean("  Côte d’Ivoire  "), "Côte d'Ivoire")

    def test_places(self):
        self.assertEqual(bn.strip_paren("Forest (Bruxelles-Capitale)"), "Forest")
        self.assertEqual(bn.province_name("Province du Hainaut"), "Hainaut")
        self.assertEqual(bn.province_name("Province de Liège"), "Liège")

    def test_subset_alias(self):
        self.assertTrue(bn.subset_alias("Napoléon", "Napoléon Ier"))
        self.assertTrue(bn.subset_alias("Jacques Cousteau", "Jacques-Yves Cousteau"))
        self.assertFalse(bn.subset_alias("Bonaparte", "Napoléon Ier"))            # a pseudonym is not a part
        self.assertFalse(bn.subset_alias("Eugene Delacroix", "Eugène Delacroix"))  # an accentless copy never becomes « correct »
        self.assertFalse(bn.subset_alias("Napoléon Ier", "Napoléon Ier"))

    def test_rang(self):
        self.assertEqual(bn.rang(100, 100), 1.0)
        self.assertEqual(bn.rang(25, 100), 0.5)
        self.assertEqual(bn.rang(0, 100), 0.01)

    def test_commons_licence_allow_list(self):
        self.assertEqual(bn.commons_licence("Public domain"), "domaine public")
        self.assertEqual(bn.commons_licence("CC0"), "CC0 1.0")
        self.assertIsNone(bn.commons_licence("CC BY-SA 3.0"))   # not GPL-compatible, and share-alike
        self.assertIsNone(bn.commons_licence("CC BY-SA 4.0"))   # share-alike would change the pack's licence
        self.assertIsNone(bn.commons_licence(None))


class Transforms(unittest.TestCase):
    def test_first_names_two_tiers(self):
        insee = [("1", "GABRIEL", "2020", "4000"), ("2", "LÉA", "2015", "3000"), ("2", "GERMAINE", "1930", "5000"),
                 ("1", "_PRENOMS_RARES", "2020", "9999"), ("1", "RARE", "2000", "5")]
        statbel = [("Geert", "30000"), ("Léa", "100")]
        old = bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED
        try:
            bn.PREDICTED_FIRST_NAMES = 3   # Geert (Belgian share), Gabriel, Léa; Germaine has no recent birth
            bn.ALLTIME_PREDICTED = 0       # edition 2026.1: no all-time promotion
            out = {e["nom"]: e for e in bn.first_names(insee, statbel)}
            bn.ALLTIME_PREDICTED = 2       # 2026.2: the all-time top 2 (Geert, Germaine) are predicted too
            out2 = {e["nom"]: e for e in bn.first_names(insee, statbel)}
        finally:
            bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED = old
        self.assertNotIn("Rare", out)                 # < 20 births
        self.assertNotIn("_Prenoms_Rares", out)
        self.assertEqual(out["Léa"].get("niveau"), None)
        self.assertEqual(out["Germaine"].get("niveau"), 2)   # no recent birth: recognised only
        self.assertIn("Geert", out)                          # Belgian only
        self.assertEqual(out2["Germaine"].get("niveau"), None)   # promoted by its all-time births
        self.assertNotIn("_promu", out2["Geert"])                # already predicted by its recent share: not « promoted »

    def test_countries_filter_and_flags(self):
        terr = {"BE": "Belgique", "CI": "Côte d’Ivoire", "EU": "Union européenne", "150": "Europe", "001": "Monde",
                "CD": "Congo-Kinshasa", "CD-alt-variant": "Congo (RDC)", "XK": "Kosovo",
                "HK": "R.A.S. chinoise de Hong Kong", "HK-alt-short": "Hong Kong"}
        out = {e["nom"]: e for e in bn.countries(terr, {"be", "ci"})}
        self.assertEqual(out["Belgique"]["image"], "images/pays/be.png")
        self.assertIn("Côte d'Ivoire", out)
        self.assertNotIn("Union européenne", out)
        self.assertEqual(out["Europe"]["categorie"], "continent")
        self.assertNotIn("Monde", out)
        self.assertNotIn("variantes", out["Congo-Kinshasa"])   # « Congo (RDC) »: brackets are not a name
        self.assertNotIn("image", out["Kosovo"])
        self.assertIn("Hong Kong", out)                       # the short form when the long one is not a name

    def test_belgium_exonym_shown_and_variants(self):
        ref = [("1", "03000", "Wallonische Region", "Région wallonne", "Waals Gewest"),
               ("2", "50000", "Provinz Hennegau", "Province du Hainaut", "Provincie Henegouwen"),
               ("4", "33011", "Ypern", "Ypres", "Ieper"),
               ("4", "21007", "Forest (Brüssel-Hauptstadt)", "Forest (Bruxelles-Capitale)", "Vorst (Brussel-Hoofdstad)")]
        wd = [{"nis": "33011", "fr": "Ypres", "population": "35000"}, {"nis": "21007", "fr": "Forest", "population": "56000"},
              {"nis": "03000", "fr": "Région wallonne", "aliases": "Wallonie|SPW"}]
        out = {e["nom"]: e for e in bn.belgium(ref, wd, {"50000": ("images/belgique/50000.png", "Generieke vlag van Henegouwen.svg")})}
        self.assertEqual(out["Ypres"]["variantes"], ["Ieper", "Ypern"])
        self.assertEqual(out["Forest"]["variantes"], ["Vorst"])
        self.assertEqual(out["Hainaut"]["variantes"], ["Henegouwen", "Hennegau"])
        self.assertIn("Wallonie", out["Région wallonne"]["variantes"])
        self.assertNotIn("SPW", out["Région wallonne"]["variantes"])
        self.assertEqual(out["Hainaut"]["image"], "images/belgique/50000.png")
        self.assertEqual(out["Forest"]["rang"], 1.0)

    def test_historical_refuses_living_people_and_missing_qids(self):
        rows = [{"item": "Q1", "fr": "Napoléon Ier", "aliases": "Napoléon|Bonaparte", "born": "1769", "died": "1821", "links": "288"},
                {"item": "Q2", "mul": "Homère", "links": "273"}]
        out = {e["nom"]: e for e in bn.historical(rows, ["Q1", "Q2"])}
        self.assertEqual(out["Napoléon Ier"]["variantes"], ["Napoléon"])
        self.assertIn("Homère", out)                     # no dates: antiquity
        with self.assertRaises(SystemExit):
            bn.historical([{"item": "Q3", "fr": "Quelqu'un Vivant", "born": "1990"}], ["Q3"])
        with self.assertRaises(SystemExit):
            bn.historical(rows, ["Q1", "Q2", "Q9"])

    def test_merge_priority_and_variants(self):
        e = bn.merge([{"nom": "Luxembourg", "categorie": "province-be", "rang": 0.7, "variantes": ["Luxemburg"]},
                      {"nom": "Luxembourg", "categorie": "pays", "rang": 0.6, "image": "images/pays/lu.png", "svg": ("flag-icons", "lu")},
                      {"nom": "Florence", "categorie": "prenom", "rang": 0.3, "niveau": 2},
                      {"nom": "Florence", "categorie": "ville", "rang": 0.2},
                      {"nom": "Ypres", "categorie": "commune-be", "rang": 0.2, "variantes": ["Ieper", "Luxembourg"]}])
        by = {x["nom"]: x for x in e}
        self.assertEqual(by["Luxembourg"]["categorie"], "pays")
        self.assertEqual(by["Luxembourg"]["rang"], 0.7)
        self.assertEqual(by["Luxembourg"]["image"], "images/pays/lu.png")
        self.assertEqual(by["Florence"]["categorie"], "ville")
        self.assertNotIn("niveau", by["Florence"])       # a predicted entry wins over a recognised one
        self.assertEqual(by["Ypres"]["variantes"], ["Ieper"])   # a variant that is another entry's name is dropped

    def test_diff_report(self):
        prev = [{"nom": "Léa", "categorie": "prenom"}, {"nom": "Zaïre", "categorie": "pays"}]
        cur = [{"nom": "Léa", "categorie": "prenom"}, {"nom": "Congo", "categorie": "pays"}]
        r = bn.diff_report(prev, cur, "2027.1")
        self.assertIn("Ajoutés (1) : Congo", r)
        self.assertIn("Retirés (1) : Zaïre", r)

    def test_attribution_lists_every_input(self):
        with open(bn.LOCK, encoding="utf-8") as f:
            lock = json.load(f)
        text = bn.attribution(lock)
        for needle in ("Source : Insee", "Statbel", "Unicode CLDR 49", "flag-icons 7.6.0", "Wikidata", "Wikimedia Commons", "CC BY 4.0"):
            self.assertIn(needle, text)
        for i in lock["inputs"]:
            self.assertTrue(i.get("sha256"), "input %s is not pinned" % i["id"])
        for s in lock["snapshots"]:
            self.assertTrue(s.get("sha256") and s.get("date"), "snapshot %s is not pinned" % s["id"])

    def test_selection_file(self):
        q = bn.selection_qids()
        self.assertTrue(300 <= len(q) <= 500, len(q))


class Review(unittest.TestCase):
    def test_figures_rank_below_every_predicted_first_name(self):
        firsts = [{"nom": "Adam", "rang": 0.4}, {"nom": "Adèle", "rang": 0.05}, {"nom": "Adalbert", "rang": 0.001, "niveau": 2}]
        figs = bn.below_first_names([{"nom": "Adolf Hitler", "rang": 1.0}, {"nom": "Ada Lovelace", "rang": 0.2}], firsts)
        self.assertTrue(all(f["rang"] < 0.05 for f in figs))
        self.assertGreater(figs[0]["rang"], figs[1]["rang"])

    def test_restricted_figures_are_recognised_only_and_last(self):
        rows = [{"item": "Q1", "fr": "Adolf Hitler", "born": "1889", "died": "1945", "links": "329"},
                {"item": "Q2", "fr": "Victor Hugo", "born": "1802", "died": "1885", "links": "269"}]
        out = {e["nom"]: e for e in bn.below_first_names(bn.historical(rows, ["Q1", "Q2"], {"Q1"}), [{"nom": "Adam", "rang": 0.05}])}
        self.assertTrue(out["Adolf Hitler"]["restreint"])
        self.assertEqual(out["Adolf Hitler"]["niveau"], 2)
        self.assertEqual(out["Adolf Hitler"]["rang"], 0.001)
        self.assertNotIn("restreint", out["Victor Hugo"])
        self.assertEqual(len(bn.selection_restricted()), 6)

    def test_places_rank_after_the_common_first_names(self):
        # owner 2026-11-15: « avec Jo » → Joseph before Johannesbourg; the places keep their own order
        firsts = [{"nom": "Joseph", "rang": 0.4}, {"nom": "Jonas", "rang": 0.3}, {"nom": "Joy", "rang": 0.2},
                  {"nom": "Josette", "rang": 0.001, "niveau": 2}]
        places = [{"nom": "Johannesbourg", "categorie": "ville", "rang": 1.0},
                  {"nom": "Jordanie", "categorie": "pays", "rang": 0.6},
                  {"nom": "Ypres", "categorie": "commune-be", "rang": 0.05},
                  {"nom": "Jeanne d'Arc", "categorie": "personnage", "rang": 0.3}]
        out = {e["nom"]: e["rang"] for e in bn.below_common_first_names(places, firsts, n=2)}
        self.assertLess(out["Johannesbourg"], 0.3)                # below the 2nd predicted first name
        self.assertLess(out["Jordanie"], 0.3)
        self.assertGreater(out["Jordanie"], out["Johannesbourg"])   # 0.8.3: a country heads the places band
        self.assertGreater(out["Johannesbourg"], out["Ypres"])
        self.assertGreaterEqual(out["Ypres"], 0.0105)              # never on the rare-name floor (D2)
        self.assertEqual(out["Jeanne d'Arc"], 0.3)                 # not a place: unchanged

    def test_belgian_first_names_ranked_by_recent_births(self):
        insee = [("1", "JOSEPH", "2020", "4000"), ("1", "JOHAN", "1975", "500")]
        statbel_population = [("Johan", "40000"), ("Jozef", "60000"), ("Noah", "100")]
        births = {"Noah": 9000, "Joseph": 300, "Johan": 50}
        old = bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED
        try:
            bn.PREDICTED_FIRST_NAMES = 3
            bn.ALLTIME_PREDICTED = 0   # the recent tier alone (the all-time promotion: test_alltime_promotion)
            out = {e["nom"]: e for e in bn.first_names(insee, statbel_population, births)}
        finally:
            bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED = old
        self.assertEqual(out["Jozef"].get("niveau"), 2)            # many adults, no recent birth: recognised only
        self.assertGreater(out["Joseph"]["rang"], out["Johan"]["rang"])
        self.assertGreater(out["Noah"]["rang"], out["Johan"]["rang"])

    def test_alltime_promotion(self):
        # edition 2026.2 (owner 2026-11-17): the all-time top names are predicted too, ranked below the pupils' names
        insee = [("1", "GABRIEL", "2020", "4000"), ("2", "LÉA", "2015", "3000"), ("2", "EMMA", "2020", "100"),
                 ("2", "GERMAINE", "1930", "50000"), ("1", "ROGER", "1940", "20000"), ("2", "JOSETTE", "1950", "400")]
        old = bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED
        try:
            bn.PREDICTED_FIRST_NAMES = 3   # Gabriel, Léa, Emma
            bn.ALLTIME_PREDICTED = 2       # Germaine, Roger
            firsts = bn.first_names(insee, [])
        finally:
            bn.PREDICTED_FIRST_NAMES, bn.ALLTIME_PREDICTED = old
        out = {e["nom"]: e for e in firsts}
        low = out["Emma"]["rang"]                                   # the lowest rang of the recent tier
        for n in ("Germaine", "Roger"):
            e = out[n]
            self.assertNotIn("niveau", e)                           # predicted
            self.assertEqual(e["rang"], max(low, round(bn.PROMOTED_SCALE * e["_promu"], 3)))   # no recent birth
            self.assertEqual(e["_promu"], bn.rang({"Germaine": 50000, "Roger": 20000}[n], 50000))
        self.assertEqual(out["Germaine"]["rang"], 0.5)              # 0.5 × all-time rang 1.0
        self.assertLess(out["Germaine"]["rang"], out["Léa"]["rang"])
        self.assertEqual(out["Josette"]["niveau"], 2)               # beyond the all-time top: recognised only
        # the places' cap and the figures' low are those of the recent tier alone (places and figures unchanged)
        recent = [e for e in firsts if "_promu" not in e]
        place = lambda: [{"nom": "Gap", "categorie": "ville", "rang": 0.5}, {"nom": "Gabon", "categorie": "pays", "rang": 0.6}]
        self.assertEqual(bn.below_common_first_names(place(), firsts, n=3), bn.below_common_first_names(place(), recent, n=3))
        fig = lambda: [{"nom": "Victor Hugo", "rang": 0.8}]
        self.assertEqual(bn.below_first_names(fig(), firsts), bn.below_first_names(fig(), recent))
        # a place or a figure of the same name keeps its 2026.1 rang and tier
        m = {x["nom"]: x for x in bn.merge([{"nom": "Nancy", "categorie": "ville", "rang": 0.05},
                                            {"nom": "Nancy", "categorie": "prenom", "rang": 0.2, "_promu": 0.4},
                                            {"nom": "Adolf Hitler", "categorie": "personnage", "rang": 0.001, "niveau": 2, "restreint": True},
                                            {"nom": "Adolf Hitler", "categorie": "prenom", "rang": 0.2, "_promu": 0.3}])}
        self.assertEqual(m["Nancy"]["rang"], 0.4)
        self.assertEqual(m["Adolf Hitler"]["niveau"], 2)
        self.assertEqual(m["Adolf Hitler"]["rang"], 0.3)

    def test_promu_key_never_written(self):
        firsts = [{"nom": "Roger", "categorie": "prenom", "rang": 0.2, "source": "insee", "_promu": 0.4},
                  {"nom": "Nancy", "categorie": "ville", "rang": 0.05}, {"nom": "Nancy", "categorie": "prenom", "rang": 0.2, "_promu": 0.4}]
        pub = bn.public_entries(bn.merge(firsts))
        self.assertNotIn("_promu", json.dumps(pub))
        self.assertEqual({e["nom"]: e["rang"] for e in pub}, {"Roger": 0.2, "Nancy": 0.4})
        with open(bn.__file__, encoding="utf-8") as f:
            self.assertIn("entries = public_entries(merge(entries))", f.read())   # the build writes only public entries

    def test_diff_report_tiers_and_rangs(self):
        prev = [{"nom": "Roger", "categorie": "prenom", "rang": 0.6, "niveau": 2}, {"nom": "Léa", "categorie": "prenom", "rang": 0.9}]
        cur = [{"nom": "Roger", "categorie": "prenom", "rang": 0.3}, {"nom": "Léa", "categorie": "prenom", "rang": 0.9}]
        r = bn.diff_report(prev, cur, "2026.2")
        self.assertIn("Passés au niveau prédit (1) : Roger", r)
        self.assertIn("Rang changé (1) : Roger 0.6 → 0.3", r)
        self.assertIn("| prenom | 1 |", r)

    def test_zip_is_deterministic(self):
        import tempfile, time
        with tempfile.TemporaryDirectory() as td:
            pack = os.path.join(td, "noms")
            os.makedirs(os.path.join(pack, "images"))
            for n, t in (("noms.json", "{}"), ("images/b.png", "x"), ("a.txt", "y")):
                with open(os.path.join(pack, n), "w") as f:
                    f.write(t)
            a, b = os.path.join(td, "a.zip"), os.path.join(td, "b.zip")
            bn.write_zip(a, pack, td)
            time.sleep(2.1)
            os.utime(os.path.join(pack, "a.txt"))
            bn.write_zip(b, pack, td)
            self.assertEqual(bn.sha256_file(a), bn.sha256_file(b))

    def test_unpinned_input_is_refused(self):
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            p = os.path.join(td, "x")
            with open(p, "w") as f:
                f.write("x")
            with self.assertRaises(SystemExit):
                bn.checked(p, None)
            with self.assertRaises(SystemExit):
                bn.checked(p, "0" * 64)
            self.assertEqual(bn.checked(p, bn.sha256_file(p)), p)


if __name__ == "__main__":
    unittest.main()
