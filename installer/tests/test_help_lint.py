"""Unit tests of help_lint.py (plan « aide intégrée », step 1; stdlib only):
python -I -m unittest discover -s installer/tests -p test_help_lint.py
One small valid help is built in a temporary folder, then each test breaks one rule."""
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))
import help_lint as hl  # noqa: E402

REPO_AIDE = os.path.normpath(os.path.join(HERE, "..", "..", "aide"))

NAV = ('<nav class="menu" aria-label="Pages de l\'aide"><ul>'
       '<li><a href="index.html">Sommaire</a></li><li><a href="page.html">Une page</a></li>'
       '<li><a href="adulte.html">Pour les adultes</a></li></ul></nav>')


def pg(body, **kw):
    """page.html with its #voix anchor kept (index.html and the stub link to it)."""
    if 'id="voix"' not in body:
        body += '<p id="voix">.</p>'
    return page("Une page", body, **kw)


def page(title, body, public="enfant", head_extra="", csp=hl.CSP, nav=NAV):
    return ('<!doctype html>\n<html lang="fr">\n<head>\n<meta charset="utf-8">\n'
            f'<meta http-equiv="Content-Security-Policy" content="{csp}">\n'
            f'<meta name="pepite-public" content="{public}">\n<title>{title} — Aide de Pépite</title>\n'
            f'<link rel="stylesheet" href="style.css">\n<script src="prefs.js" defer></script>{head_extra}\n</head>\n'
            f'<body>\n{nav}\n<main id="contenu">\n{body}\n</main>\n</body>\n</html>\n')


STUB = ('<!doctype html>\n<html lang="fr">\n<head>\n<meta charset="utf-8">\n'
        f'<meta http-equiv="Content-Security-Policy" content="{hl.CSP}">\n'
        '<meta http-equiv="refresh" content="0; url=../page.html#voix">\n<title>Voix — Aide de Pépite</title>\n'
        '</head>\n<body>\n<h1>Voix</h1>\n<p><a href="../page.html#voix">Ouvrir la page</a></p>\n</body>\n</html>\n')

CSS = ('@font-face { font-family: "Andika"; src: url("fonts/Andika/Andika-Regular.ttf"); }\n'
       'body { font-size: 1.25rem; }\nfooter { font-size: 0.8rem; }\n')


class HelpLintTest(unittest.TestCase):
    def setUp(self):
        self.app = tempfile.mkdtemp(prefix="help-lint-")
        self.root = os.path.join(self.app, "aide")
        self.write("index.html", page("Sommaire", '<h1>Aide</h1><p>Choisis une page : <a href="page.html#voix">la voix</a>.</p>'
                                      '<img src="img/x.svg" alt="Un dessin.">'))
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">La voix</h2>'
                                     '<p>Tu entends le mot. Clique sur <b>Écouter</b>.</p>'
                                     '<svg viewBox="0 0 10 10" role="img" aria-labelledby="t"><title id="t">Un rond.</title>'
                                     '<circle cx="5" cy="5" r="4"/></svg>'))
        self.write("adulte.html", page("Pour les adultes", '<h1>Pour les adultes</h1><p>Vous installez Pépite. '
                                       'Voir <a href="../THIRD-PARTY-NOTICES.txt">les licences</a>.</p>', public="adulte"))
        self.write("aller/page-voix.html", STUB)
        self.write("style.css", CSS)
        self.write("prefs.js", "(function () { 'use strict'; }());\n")
        self.write("img/x.svg", '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"/>')
        self.write("README.md", "notes " * 300_000)   # not shipped: never counted

    def tearDown(self):
        shutil.rmtree(self.app, ignore_errors=True)

    def write(self, rel, text, root=None):
        path = os.path.join(root or self.root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)

    def problems(self, stage=False):
        return hl.check(self.root, stage)

    def assertOne(self, needle, stage=False):
        probs = self.problems(stage)
        self.assertEqual(len(probs), 1, probs)
        self.assertIn(needle, probs[0])

    # ------------------------------------------------------------------ the valid fixture
    def test_valid_help_passes(self):
        self.assertEqual(self.problems(), [])

    def test_stage_needs_the_linked_app_files_and_fonts(self):
        probs = self.problems(stage=True)
        self.assertEqual(len(probs), 2, probs)
        self.assertTrue(any("THIRD-PARTY-NOTICES.txt missing" in p for p in probs), probs)
        self.assertTrue(any("style.css: url(fonts/Andika/Andika-Regular.ttf) not found" in p for p in probs), probs)
        self.write("THIRD-PARTY-NOTICES.txt", "x", root=self.app)
        self.write("fonts/Andika/Andika-Regular.ttf", "x")
        self.assertEqual(self.problems(stage=True), [])

    # ------------------------------------------------------------------ document shell
    def test_doctype(self):
        self.write("page.html", pg("<h1>Une page</h1>").replace("<!doctype html>", "<html5>"))
        self.assertOne("doctype")

    def test_lang(self):
        self.write("page.html", pg("<h1>Une page</h1>").replace('lang="fr"', 'lang="en"'))
        self.assertOne('lang="fr"')

    def test_charset(self):
        self.write("page.html", pg("<h1>Une page</h1>").replace('<meta charset="utf-8">', ""))
        self.assertOne("charset")

    def test_csp_exact(self):
        self.write("page.html", pg("<h1>Une page</h1>", csp="default-src 'self'"))
        self.assertOne("Content-Security-Policy")

    # ------------------------------------------------------------------ headings
    def test_title(self):
        self.write("page.html", pg("<h1>Une page</h1>").replace("<title>Une page — Aide de Pépite</title>", "<title> </title>"))
        self.assertOne("<title>")

    def test_one_h1(self):
        self.write("page.html", pg("<h1>Une</h1><h1>Deux</h1>"))
        self.assertOne("2 <h1>")

    def test_no_skipped_level(self):
        self.write("page.html", pg("<h1>Une page</h1><h3>Trop bas</h3>"))
        self.assertOne("h1 → h3")

    def test_unique_ids(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">A</h2><p id="voix">B</p>'))
        self.assertOne("duplicate id")

    # ------------------------------------------------------------------ text alternatives
    def test_img_alt(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><img src="img/x.svg">'))
        self.assertOne("no alt")

    def test_svg_title(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><svg role="img"><circle r="1"/></svg>'))
        self.assertOne("without a <title>")

    # ------------------------------------------------------------------ forbidden markup
    def test_inline_style(self):
        self.write("page.html", pg('<h1 style="color:red">Une page</h1><h2 id="voix">V</h2>'))
        self.assertOne("inline style")

    def test_style_element(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2>', head_extra="<style>p{}</style>"))
        self.assertOne("<style>")

    def test_inline_script(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><script>alert(1)</script>'))
        self.assertOne("inline <script>")

    def test_event_handler(self):
        self.write("page.html", pg('<h1 onclick="x()">Une page</h1><h2 id="voix">V</h2>'))
        self.assertOne("onclick")

    def test_forbidden_elements(self):
        for tag in ("form", "iframe", "object", "embed", "base"):
            self.write("page.html", pg(f'<h1>Une page</h1><h2 id="voix">V</h2><{tag}></{tag}>'))
            self.assertOne(f"<{tag}>")

    # ------------------------------------------------------------------ links
    def test_absolute_links(self):
        for href in ("https://example.org", "file:///C/x", "//example.org/x", "/index.html", "mailto:x"):
            self.write("page.html", pg(f'<h1>Une page</h1><h2 id="voix">V</h2><a href="{href}">x</a>'))
            self.assertOne("is not relative")

    def test_missing_target(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><a href="nulle-part.html">x</a>'))
        self.assertOne("not found")

    def test_missing_anchor(self):
        self.write("index.html", page("Sommaire", '<h1>Aide</h1><a href="page.html#ailleurs">x</a>'))
        self.assertOne("anchor #ailleurs not found")

    def test_missing_same_page_anchor(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><a href="#rien">x</a>'))
        self.assertOne("anchor #rien")

    def test_parent_folder_only_for_the_allowed_files(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><a href="../prediction.sqlite">x</a>'))
        self.assertOne("leaves the help folder")
        for ok in ("../COPYING", "../DEPLOIEMENT.txt", "../LISEZ-MOI.txt"):
            self.write("page.html", pg(f'<h1>Une page</h1><h2 id="voix">V</h2><a href="{ok}">x</a>'))
            self.assertEqual(self.problems(), [], ok)

    def test_query_string_is_ignored(self):
        self.write("index.html", page("Sommaire", '<h1>Aide</h1><a href="page.html?od=1#voix">x</a>'))
        self.assertEqual(self.problems(), [])

    # ------------------------------------------------------------------ text
    def test_email(self):
        self.write("adulte.html", page("Pour les adultes", "<h1>Adultes</h1><p>Écrivez à quelqu.un@example.org</p>", public="adulte"))
        self.assertOne("e-mail")

    def test_drive_path(self):
        self.write("adulte.html", page("Pour les adultes", "<h1>Adultes</h1><p>Dans C:\\Program Files</p>", public="adulte"))
        self.assertOne("drive path")

    def test_banned_wording(self):
        for text in ("une IA", "the AI", "une intelligence artificielle", "un réseau de neurones", "un modèle neuronal",
                     "l'apprentissage automatique", "du machine learning"):
            self.write("adulte.html", page("Pour les adultes", f"<h1>Adultes</h1><p>Pépite n'est pas {text}.</p>", public="adulte"))
            self.assertOne("banned wording")
        self.write("adulte.html", page("Pour les adultes", "<h1>Adultes</h1><p>Via la voix ; ia en minuscules.</p>", public="adulte"))
        self.assertEqual(self.problems(), [])

    def test_vous_on_a_pupil_page(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><p>Vous écrivez.</p>'))
        self.assertOne("pupils")
        # quoted interface text is allowed
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2><p>Windows dit <q>Windows a protégé '
                                     'votre ordinateur : vous</q> et <b>Vous</b>.</p>'))
        self.assertEqual(self.problems(), [])

    def test_tu_on_an_adult_page(self):
        for w in ("tu", "ton", "ta", "tes", "te", "toi", "t'aide", "t’aide"):
            self.write("adulte.html", page("Pour les adultes", f"<h1>Adultes</h1><p>Ici {w} lis.</p>", public="adulte"))
            self.assertOne("adults")
        self.write("adulte.html", page("Pour les adultes", "<h1>Adultes</h1><p>L'enfant voit <q>Qui es-tu ?</q> "
                                       "et <kbd>tu</kbd>. Les tests, le bouton, la tâche, sa tête, cette étape, l'été, "
                                       "d'autres textes, Statbel, état.</p>", public="adulte"))
        self.assertEqual(self.problems(), [])

    def test_public_meta_required(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2>', public="tous"))
        self.assertOne("pepite-public")

    # ------------------------------------------------------------------ site
    def test_page_missing_from_the_navigation(self):
        self.write("autre.html", page("Autre", "<h1>Autre</h1>"))
        self.assertOne("autre.html: not in the navigation")

    def test_navigation_identical(self):
        self.write("page.html", pg('<h1>Une page</h1><h2 id="voix">V</h2>',
                                     nav=NAV.replace("Une page</a>", "Autre nom</a>")))
        self.assertOne("navigation differs")

    def test_stub_must_redirect_to_an_existing_anchor(self):
        self.write("aller/page-voix.html", STUB.replace("#voix", "#rien"))
        probs = self.problems()
        self.assertTrue(probs and all("#rien" in p or "does not reach" in p for p in probs), probs)

    def test_stub_needs_a_visible_link(self):
        self.write("aller/page-voix.html", STUB.replace('<a href="../page.html#voix">', '<a href="../index.html">'))
        self.assertOne("no visible link")

    def test_stub_needs_a_refresh(self):
        self.write("aller/page-voix.html", STUB.replace('http-equiv="refresh"', 'name="x"'))
        self.assertOne("refresh")

    def test_small_font_size(self):
        self.write("style.css", CSS + "small { font-size: 0.7rem; }\n.x { font-size: 12px; }\n")
        probs = self.problems()
        self.assertEqual(len(probs), 2, probs)
        self.assertTrue(all("below 0.8 rem" in p for p in probs))

    def test_css_url_outside_the_help(self):
        self.write("style.css", CSS + '@font-face { font-family: "X"; src: url("../fonts/X.ttf"); }\n')
        self.assertOne("leaves the help folder")

    def test_sizes(self):
        with open(os.path.join(self.root, "img", "big.png"), "wb") as f:
            f.write(b"\0" * (hl.MAX_IMAGE + 1))
        self.assertOne("img/big.png")
        for i in range(4):
            with open(os.path.join(self.root, "img", f"b{i}.png"), "wb") as f:
                f.write(b"\0" * hl.MAX_IMAGE)
        self.assertTrue(any("weighs" in p for p in self.problems()))

    def test_fonts_are_not_counted(self):
        os.makedirs(os.path.join(self.root, "fonts", "Andika"), exist_ok=True)
        with open(os.path.join(self.root, "fonts", "Andika", "big.ttf"), "wb") as f:
            f.write(b"\0" * (hl.MAX_TOTAL + 1))
        self.assertEqual(self.problems(), [])

    # ------------------------------------------------------------------ the repository's own help
    @unittest.skipUnless(os.path.isfile(os.path.join(REPO_AIDE, "index.html")), "the help (freeware/aide) is not written yet")
    def test_repository_help_is_clean(self):
        self.assertEqual(hl.check(REPO_AIDE), [])


if __name__ == "__main__":
    unittest.main()
