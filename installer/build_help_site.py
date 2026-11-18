#!/usr/bin/env python3
"""Build the online copy of the embedded help for GitHub Pages (plan 13, A10). Stdlib only; run with ``python -I``.

    python -I installer/build_help_site.py --out <site> --fonts <fonts dir> --latest 0.10 \\
        --version 0.10=<source dir> [--version 0.11=<source dir> ...] [--replace]

Each ``<source dir>`` is a source tree of the repository (the checkout, or ``git archive`` of a release tag): its
``aide/`` (the help pages) and the files the help links as ``../<file>`` — ``THIRD-PARTY-NOTICES.txt``, ``COPYING``,
``installer/DEPLOIEMENT.txt`` and ``LISEZ-MOI.txt``, rendered here from ``installer/LISEZ-MOI.txt.in`` as
``build_installer.ps1`` renders it for a public build (version = the csproj ``Version``, notes =
``installer/notes/<version>.txt``, the pierre credit when ``voices.json`` says pierre is public). So the site needs no
published release (plan 13: it may go live right after the first public push). ``--fonts`` is the fonts folder of the
data release (``fonts-<month>.zip``).

The site mirrors the installed app folder, one folder per **minor** version (a patch release replaces its minor's
folder), so the help files stay the same and ``help_lint --stage`` checks them as in an installed copy:

    <site>/index.html                 redirect to <latest>/aide/index.html
    <site>/<minor>/index.html         redirect to aide/index.html
    <site>/<minor>/aide/...           the help, its fonts in aide/fonts/ (as the csproj copies them)
    <site>/<minor>/THIRD-PARTY-NOTICES.txt, COPYING, DEPLOIEMENT.txt, LISEZ-MOI.txt

The only transform of the pages: a banner on every page (« Aide en ligne de Pépite <minor> — l'aide installée avec
Pépite fonctionne sans Internet. »), a paragraph on the privacy page (GitHub Pages logs the visits; no audience
measurement, no cookie from us), and the banner's style rule appended to ``style.css`` (the CSP forbids inline style).
The CSP, the relative links and the no-external-link rule are untouched; ``help_lint.check(stage=True)`` must pass on
every version folder. Exit code 0 = built and clean, 1 = a problem (listed), 2 = bad arguments.
"""
import argparse
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import help_lint  # noqa: E402

COPIED = (("THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.txt"), ("COPYING", "COPYING"),
          ("installer/DEPLOIEMENT.txt", "DEPLOIEMENT.txt"))
TEXTS = ("THIRD-PARTY-NOTICES.txt", "COPYING", "DEPLOIEMENT.txt", "LISEZ-MOI.txt")
FONT_EXT = (".ttf", ".otf")
BANNER_BEFORE = '<header class="haut">'
CSS = """
/* Aide en ligne (installer/build_help_site.py) : le bandeau de la copie web. */
.en-ligne { margin: 0; padding: 0.5rem 1rem; background: var(--sable); color: var(--encre); font-size: 1rem;
            border-bottom: 2px solid var(--biscuit); text-align: center; }
"""
PRIVACY = ('<p>Cette copie en ligne de l\'aide est hébergée par GitHub Pages, qui enregistre les '
           'visites dans ses journaux. Nous n\'y ajoutons ni mesure d\'audience ni cookie. L\'aide installée avec Pépite '
           's\'ouvre sans Internet.</p>')
REDIRECT = """<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="{csp}">
<meta name="referrer" content="no-referrer">
<meta http-equiv="refresh" content="0; url={target}">
<title>Aide de Pépite</title>
</head>
<body>
<p><a href="{target}">Ouvrir l'aide de Pépite {minor}</a></p>
</body>
</html>
"""


class SiteError(Exception):
    pass


def banner(minor):
    return ('<p class="en-ligne">Aide en ligne de Pépite %s — l\'aide installée avec Pépite fonctionne sans '
            'Internet.</p>' % minor)


def transform_page(html, minor, privacy=False):
    """The page with the online banner (and the privacy paragraph); raises SiteError when a marker is missing."""
    if html.count(BANNER_BEFORE) != 1:
        raise SiteError("no single %s to put the banner before" % BANNER_BEFORE)
    html = html.replace(BANNER_BEFORE, banner(minor) + "\n" + BANNER_BEFORE, 1)
    if privacy:
        if "</h1>" not in html:
            raise SiteError("privacy page without </h1>")
        html = html.replace("</h1>", "</h1>\n" + PRIVACY, 1)
    return html


def app_version(src):
    """The ``<Version>`` of src/Predict.Keyboard/Predict.Keyboard.csproj in a source tree."""
    root = ET.parse(os.path.join(src, "src", "Predict.Keyboard", "Predict.Keyboard.csproj")).getroot()
    for v in root.iter("Version"):
        if v.text and re.match(r"^\d+\.\d+\.\d+$", v.text.strip()):
            return v.text.strip()
    raise SiteError("%s: no x.y.z Version in Predict.Keyboard.csproj" % src)


def lisez_moi(src):
    """LISEZ-MOI.txt of a public build, as build_installer.ps1 renders it (CRLF line ends)."""
    version = app_version(src)

    def read(*rel):
        with open(os.path.join(src, *rel), encoding="utf-8-sig") as f:
            return f.read()
    with open(os.path.join(src, "src", "Data.Build", "voices.json"), encoding="utf-8") as f:
        pierre = json.load(f)["voices"].get("pierre", {}).get("public", False)
    t = read("installer", "LISEZ-MOI.txt.in")
    if "{ATTESTATION}" in t:                                 # 0.10.2+: the gh attestation check of a public build
        t = t.replace("{ATTESTATION}", read("installer", "notes", "attestation.txt").rstrip() + "\n")
    t = (t.replace("{VERSION}", version).replace("{SUFFIXE}", "").replace("{PRIVE}", " - VERSION DE TEST")
         .replace("{NOTES}", read("installer", "notes", version + ".txt").rstrip()))
    credit = read("installer", "notes", "credit-pierre.txt").rstrip() + "\n" if pierre else ""
    t = t.replace("{CREDIT_PIERRE}", credit)
    if "{DONNEES}" in t:                                     # 0.10.2+: the data release of that tree (the credits' link)
        with open(os.path.join(src, "src", "Data.Build", "data-release.lock.json"), encoding="utf-8") as f:
            tag = json.load(f).get("tag")
        if not isinstance(tag, str) or not re.fullmatch(r"data-\d{4}\.\d{2}(\.\d+)?", tag):   # as build_installer.ps1
            raise SiteError("%s: data-release.lock.json tag %r is not data-YYYY.MM[.n]" % (src, tag))
        t = t.replace("{DONNEES}", tag)
    return t.replace("\r\n", "\n").replace("\n", "\r\n")


def build_version(src, fonts, dest, minor):
    aide_src = os.path.join(src, "aide")
    if not os.path.isfile(os.path.join(aide_src, "index.html")):
        raise SiteError("%s: no aide/index.html" % src)
    if ".".join(app_version(src).split(".")[:2]) != minor:
        raise SiteError("%s: version %s is not in the minor %s" % (src, app_version(src), minor))
    for rel, name in COPIED:
        p = os.path.join(src, *rel.split("/"))
        if not os.path.isfile(p):
            raise SiteError("%s: %s missing" % (src, rel))
        shutil.copyfile(p, os.path.join(dest, name))
    try:
        text = lisez_moi(src)
    except (OSError, KeyError, ValueError) as e:
        raise SiteError("%s: LISEZ-MOI.txt not rendered (%s)" % (src, e))
    with open(os.path.join(dest, "LISEZ-MOI.txt"), "w", encoding="utf-8", newline="") as f:
        f.write(text)
    aide = os.path.join(dest, "aide")
    shutil.copytree(aide_src, aide, ignore=shutil.ignore_patterns(*help_lint.NOT_SHIPPED, "fonts"))
    for d, _, fs in os.walk(fonts):                    # as Predict.Keyboard.csproj: *.ttf, *.otf, OFL.txt
        for f in fs:
            if f.lower().endswith(FONT_EXT) or f == "OFL.txt":
                rel = os.path.relpath(os.path.join(d, f), fonts)
                target = os.path.join(aide, "fonts", rel)
                os.makedirs(os.path.dirname(target), exist_ok=True)
                shutil.copyfile(os.path.join(d, f), target)
    for f in sorted(os.listdir(aide)):
        if f.endswith(".html"):
            p = os.path.join(aide, f)
            with open(p, encoding="utf-8") as h:
                html = h.read()
            try:
                html = transform_page(html, minor, privacy=(f == "vie-privee.html"))
            except SiteError as e:
                raise SiteError("%s/aide/%s: %s" % (minor, f, e))
            with open(p, "w", encoding="utf-8", newline="\n") as h:
                h.write(html)
    with open(os.path.join(aide, "style.css"), "a", encoding="utf-8", newline="\n") as h:
        h.write(CSS)
    with open(os.path.join(dest, "index.html"), "w", encoding="utf-8", newline="\n") as h:
        h.write(REDIRECT.format(csp=help_lint.CSP, target="aide/index.html", minor=minor))
    return ["%s/aide: %s" % (minor, p) for p in help_lint.check(aide, stage=True)]


def build(out, fonts, versions, latest):
    if latest not in versions:
        raise SiteError("--latest %s is not one of the --version minors" % latest)
    os.makedirs(out)
    problems = []
    for minor, src in sorted(versions.items()):
        dest = os.path.join(out, minor)
        os.makedirs(dest)
        problems += build_version(src, fonts, dest, minor)
    with open(os.path.join(out, "index.html"), "w", encoding="utf-8", newline="\n") as h:
        h.write(REDIRECT.format(csp=help_lint.CSP, target="%s/aide/index.html" % latest, minor=latest))
    open(os.path.join(out, ".nojekyll"), "w").close()   # serve the files as they are
    return problems


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--fonts", required=True)
    ap.add_argument("--latest", required=True, help="the minor version / redirects to (x.y)")
    ap.add_argument("--version", action="append", default=[], help="<x.y>=<source dir> (repeatable)")
    ap.add_argument("--replace", action="store_true")
    a = ap.parse_args(argv)
    versions = {}
    for v in a.version:
        minor, sep, src = v.partition("=")
        if not sep or not re.match(r"^\d+\.\d+$", minor) or minor in versions:
            print("bad --version %r (expected x.y=<dir>, once per minor)" % v, file=sys.stderr)
            return 2
        versions[minor] = src
    if not versions or not os.path.isdir(a.fonts):
        print("give at least one --version and an existing --fonts folder", file=sys.stderr)
        return 2
    if os.path.exists(a.out):
        if not a.replace:
            print("%s exists (--replace)" % a.out, file=sys.stderr)
            return 2
        shutil.rmtree(a.out)
    try:
        problems = build(a.out, a.fonts, versions, a.latest)
    except (SiteError, OSError, ET.ParseError) as e:
        print("PROBLEM  %s" % e, file=sys.stderr)
        return 1
    for p in problems:
        print("HELP LINT: %s" % p)
    print("help site %s: %s, latest %s, %d problem(s)" % (a.out, ", ".join(sorted(versions)), a.latest, len(problems)))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
