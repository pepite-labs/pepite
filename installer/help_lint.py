#!/usr/bin/env python3
"""Help lint (plan « aide intégrée », step 1): the rules every page of the embedded help (``aide\\``) must keep. Stdlib
only (``html.parser``); run with ``python -I``.

    python -I installer/help_lint.py <aide dir> [--stage]

``<aide dir>`` is the source folder ``freeware/aide`` or the ``aide`` folder of a build / publish (``--stage``: the
files the help links outside its folder — ``../THIRD-PARTY-NOTICES.txt``… — and the fonts must then exist too).

Checked on every page (``*.html``) and redirect stub (``aller/*.html``):

- **document shell**: ``<!doctype html>``, ``<html lang="fr">``, ``<meta charset="utf-8">`` and exactly the CSP of
  :data:`CSP` (local files only, no inline script or style);
- **headings**: a ``<title>``, one ``<h1>``, no skipped level (h2 → h4), unique ``id``\\ s;
- **text alternatives**: every ``<img>`` has ``alt``; every ``<svg role="img">`` has a ``<title>``;
- **forbidden markup**: no ``style`` attribute or element, no inline script, no ``on*=`` handler, no
  ``form iframe object embed base``;
- **links** (``href`` / ``src``, and ``url()`` in ``style.css``): relative only (no scheme, no ``//``, no ``/``); the
  target file and its ``#anchor`` exist; a link may leave the help folder only for :data:`PARENT_ALLOWED`;
- **text**: no e-mail address, no drive path, none of :data:`BANNED` (Pépite is never presented as « IA »); a page
  ``<meta name="pepite-public" content="enfant">`` never says « vous », an ``adulte`` page never says « tu / ton / ta /
  tes / te / toi / t' » — quoted interface text (inside ``b q kbd samp``) excepted.

And on the whole help: every page is in the navigation, the navigation is identical on every page, every stub
redirects to an existing page and anchor and shows the same link, ``font-size`` in ``style.css`` is at least 0.8 rem,
the help weighs at most :data:`MAX_TOTAL` bytes without the fonts and every image at most :data:`MAX_IMAGE`.

Exit code 0 = clean, 1 = problems (listed), 2 = bad arguments.
"""
import argparse
import os
import posixpath
import re
import sys
from html.parser import HTMLParser

CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
       "form-action 'none'; base-uri 'none'")
# files of the app folder the help may link (« ../x »), and the app's own fonts folder
PARENT_ALLOWED = ("THIRD-PARTY-NOTICES.txt", "COPYING", "DEPLOIEMENT.txt", "LISEZ-MOI.txt")
PARENT_ALLOWED_DIRS = ("fonts/",)
MAX_TOTAL = 1_200_000
MAX_IMAGE = 300_000
MIN_FONT_REM = 0.8
STUB_DIR = "aller"
NOT_SHIPPED = ("README.md",)          # contributor notes: in the source folder only
FORBIDDEN_TAGS = {"form", "iframe", "object", "embed", "base", "style"}
QUOTED_UI = {"b", "q", "kbd", "samp"}
VOID = {"area", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param"}
BANNED = (
    (re.compile(r"\bIA\b"), "« IA »"),
    (re.compile(r"\bAI\b"), "« AI »"),
    (re.compile(r"intelligence\s+artificielle", re.I), "« intelligence artificielle »"),
    (re.compile(r"neuron", re.I), "« neurone / neuronal »"),
    (re.compile(r"apprentissage\s+automatique", re.I), "« apprentissage automatique »"),
    (re.compile(r"machine\s+learning", re.I), "« machine learning »"),
)
EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")
DRIVE = re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]")
SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
VOUS = re.compile(r"\bvous\b", re.I)
TU = re.compile(r"\b(?:tu|ton|ta|tes|te|toi)\b|\bt['’]", re.I)
CSS_URL = re.compile(r"url\(\s*['\"]?([^'\")]+)['\"]?\s*\)")
CSS_FONT_SIZE = re.compile(r"font-size\s*:\s*([0-9]*\.?[0-9]+)\s*(rem|em|px|%)", re.I)
REFRESH = re.compile(r"^\s*0\s*;\s*url\s*=\s*(\S+)\s*$", re.I)


class Page(HTMLParser):
    """One HTML file, parsed once: what the rules need."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.doctype = None
        self.html_lang = None
        self.charset = False
        self.csp = []
        self.public = None
        self.refresh = None
        self.title = ""
        self.headings = []          # levels in document order
        self.ids = []
        self.links = []             # (attribute value, tag)
        self.problems = []
        self.text = []              # (text, quoted) of the visible text and the text alternatives
        self.nav = []               # [(href, label)] of <nav class="menu">
        self._stack = []
        self._in_title = False
        self._svg_img = []          # one entry per open <svg role="img">: has a <title>?
        self._nav_depth = None
        self._nav_href = None
        self._nav_text = []

    # ------------------------------------------------------------------ helpers
    def _quoted(self):
        return any(t in QUOTED_UI for t in self._stack)

    def _in(self, tag):
        return tag in self._stack

    # ------------------------------------------------------------------ parser callbacks
    def handle_decl(self, decl):
        if self.doctype is None:
            self.doctype = decl

    def handle_starttag(self, tag, attrs):
        self._start(tag, attrs)
        if tag not in VOID:
            self._stack.append(tag)

    def handle_startendtag(self, tag, attrs):
        self._start(tag, attrs)

    def _start(self, tag, attrs):
        a = {k.lower(): (v if v is not None else "") for k, v in attrs}
        for k in a:
            if k.startswith("on"):
                self.problems.append(f"<{tag}> has an event handler {k}=")
            if k == "style":
                self.problems.append(f"<{tag}> has an inline style")
        if tag in FORBIDDEN_TAGS and not self._in("svg"):
            self.problems.append(f"forbidden element <{tag}>")
        if "id" in a:
            self.ids.append(a["id"])
        if tag == "html":
            self.html_lang = a.get("lang")
        elif tag == "meta":
            if a.get("charset", "").lower() == "utf-8":
                self.charset = True
            equiv = a.get("http-equiv", "").lower()
            if equiv == "content-security-policy":
                self.csp.append(a.get("content", ""))
            elif equiv == "refresh":
                self.refresh = a.get("content", "")
            if a.get("name") == "pepite-public":
                self.public = a.get("content")
        elif tag == "title" and not self._in("svg"):
            self._in_title = True
        elif tag == "title" and self._svg_img:
            self._svg_img[-1] = True
        elif tag == "svg":
            if a.get("role") == "img":
                self._svg_img.append(False)
            else:
                self._svg_img.append(None)
        elif tag == "img":
            if "alt" not in a:
                self.problems.append(f"<img src=\"{a.get('src', '?')}\"> has no alt")
            else:
                self.text.append((a["alt"], False))
        elif tag == "script":
            if "src" not in a:
                self.problems.append("inline <script>")
        elif re.fullmatch(r"h[1-6]", tag) and not self._in("svg"):
            self.headings.append(int(tag[1]))
        elif tag == "nav" and "menu" in a.get("class", "").split():
            self._nav_depth = len(self._stack)
        if tag in ("a", "link"):
            if "href" in a:
                self.links.append((a["href"], tag))
        elif tag in ("img", "script", "source"):
            if "src" in a:
                self.links.append((a["src"], tag))
        elif tag == "use" and ("href" in a or "xlink:href" in a):
            self.links.append((a.get("href") or a.get("xlink:href"), tag))
        for k in ("title", "aria-label"):
            if k in a and tag != "title":
                self.text.append((a[k], self._quoted()))
        if self._nav_depth is not None and tag == "a":
            self._nav_href = a.get("href")
            self._nav_text = []

    def handle_endtag(self, tag):
        if tag == "title" and not self._in("svg"):
            self._in_title = False
        if tag == "svg" and self._svg_img:
            if self._svg_img.pop() is False:
                self.problems.append("<svg role=\"img\"> without a <title>")
        if self._nav_depth is not None and tag == "a" and self._nav_href is not None:
            self.nav.append((self._nav_href, " ".join("".join(self._nav_text).split())))
            self._nav_href = None
        if tag in self._stack:
            while self._stack:
                if self._stack.pop() == tag:
                    break
        if self._nav_depth is not None and tag == "nav" and len(self._stack) <= self._nav_depth:
            self._nav_depth = None

    def handle_data(self, data):
        if self._in_title:
            self.title += data
        if self._in("script") or self._in("style"):
            return
        self.text.append((data, self._quoted()))
        if self._nav_href is not None:
            self._nav_text.append(data)


def parse(path):
    with open(path, encoding="utf-8") as f:
        raw = f.read()
    p = Page()
    p.feed(raw)
    p.close()
    p.raw = raw
    return p


def split_link(value):
    """(path, fragment) of a relative link (the query string, which prefs.js adds, is ignored)."""
    path, _, frag = value.partition("#")
    return path.split("?", 1)[0], frag


def check_link(name, value, page_dir, root, stage, pages, problems):
    """One href / src of the file ``name`` (relative to the help folder, POSIX). Returns the resolved help-relative
    path (or None)."""
    if SCHEME.match(value) or value.startswith("/") or value.startswith("\\"):
        problems.append(f"{name}: link « {value[:60]} » is not relative")
        return None
    path, frag = split_link(value)
    if not path:                                   # « #anchor » in the same page
        if frag and frag not in pages[name].ids:
            problems.append(f"{name}: anchor #{frag} not found")
        return None
    target = posixpath.normpath(posixpath.join(page_dir, path))
    if target.startswith("../"):
        outside = target[3:]
        if not (outside in PARENT_ALLOWED or outside.startswith(PARENT_ALLOWED_DIRS)) or outside.startswith("../"):
            problems.append(f"{name}: link « {value[:60]} » leaves the help folder")
        elif stage and not os.path.exists(os.path.join(os.path.dirname(root), *outside.split("/"))):
            problems.append(f"{name}: link « {value[:60]} »: {outside} missing next to the help")
        return None
    if target.startswith("fonts/") and not stage:
        return target                              # copied at build time
    if not os.path.isfile(os.path.join(root, *target.split("/"))):
        problems.append(f"{name}: link « {value[:60]} »: {target} not found")
        return None
    if frag:
        if target in pages:
            if frag not in pages[target].ids:
                problems.append(f"{name}: link « {value[:60]} »: anchor #{frag} not found in {target}")
        else:
            problems.append(f"{name}: link « {value[:60]} »: an anchor on a non-page")
    return target


def check_text(name, page, problems):
    whole = " ".join(t for t, _ in page.text)
    for m in EMAIL.finditer(page.raw):
        problems.append(f"{name}: e-mail address « {m.group(0)[:3]}… »")
    for m in DRIVE.finditer(page.raw):
        problems.append(f"{name}: drive path « {page.raw[m.start():m.start() + 12]} »")
    for rx, label in BANNED:
        if rx.search(whole) or rx.search(page.title):
            problems.append(f"{name}: banned wording {label}")
    free = " ".join(t for t, quoted in page.text if not quoted) + " " + page.title
    if page.public == "enfant":
        for m in VOUS.finditer(free):
            problems.append(f"{name}: « {m.group(0)} » on a page for pupils (« tu »)")
    elif page.public == "adulte":
        for m in TU.finditer(free):
            problems.append(f"{name}: « {m.group(0)} » on a page for adults (« vous »)")


def check_shell(name, page, problems):
    if (page.doctype or "").strip().lower() != "doctype html" or not page.raw.lstrip("﻿ \r\n\t").lower().startswith("<!doctype html>"):
        problems.append(f"{name}: no <!doctype html> first")
    if page.html_lang != "fr":
        problems.append(f"{name}: <html lang=\"fr\"> missing")
    if not page.charset:
        problems.append(f"{name}: <meta charset=\"utf-8\"> missing")
    if page.csp != [CSP]:
        problems.append(f"{name}: the Content-Security-Policy must be exactly the help's one")
    if not page.title.strip():
        problems.append(f"{name}: empty or missing <title>")
    if page.headings.count(1) != 1:
        problems.append(f"{name}: {page.headings.count(1)} <h1> (one expected)")
    prev = 0
    for lv in page.headings:
        if lv > prev + 1:
            problems.append(f"{name}: heading level skipped (h{prev} → h{lv})")
        prev = lv
    seen = set()
    for i in page.ids:
        if i in seen:
            problems.append(f"{name}: duplicate id « {i} »")
        seen.add(i)
    problems.extend(f"{name}: {p}" for p in page.problems)


def check_css(root, stage, problems):
    path = os.path.join(root, "style.css")
    if not os.path.isfile(path):
        problems.append("style.css missing")
        return
    with open(path, encoding="utf-8") as f:
        css = f.read()
    for m in CSS_FONT_SIZE.finditer(css):
        v, unit = float(m.group(1)), m.group(2).lower()
        rem = v if unit in ("rem", "em") else v / 16 if unit == "px" else v / 100
        if rem < MIN_FONT_REM - 1e-9:
            problems.append(f"style.css: font-size {m.group(1)}{unit} below {MIN_FONT_REM} rem")
    for m in CSS_URL.finditer(css):
        u = m.group(1)
        if SCHEME.match(u) or u.startswith("/"):
            problems.append(f"style.css: url({u[:60]}) is not relative")
            continue
        target = posixpath.normpath(u.split("?", 1)[0].split("#", 1)[0])
        if target.startswith("../"):
            problems.append(f"style.css: url({u[:60]}) leaves the help folder (Firefox refuses parent-folder fonts)")
        elif (stage or not target.startswith("fonts/")) and not os.path.isfile(os.path.join(root, *target.split("/"))):
            problems.append(f"style.css: url({u[:60]}) not found")


def check(root, stage=False):
    """Every problem of the help in ``root`` (empty = clean)."""
    problems = []
    if not os.path.isfile(os.path.join(root, "index.html")):
        return ["index.html missing"]
    names = sorted(f for f in os.listdir(root) if f.endswith(".html"))
    stub_dir = os.path.join(root, STUB_DIR)
    stubs = sorted(STUB_DIR + "/" + f for f in os.listdir(stub_dir) if f.endswith(".html")) if os.path.isdir(stub_dir) else []
    pages = {}
    for n in names + stubs:
        try:
            pages[n] = parse(os.path.join(root, *n.split("/")))
        except (UnicodeDecodeError, OSError) as e:
            problems.append(f"{n}: unreadable ({type(e).__name__})")
    for n, page in pages.items():
        check_shell(n, page, problems)
        check_text(n, page, problems)
        is_stub = n.startswith(STUB_DIR + "/")
        if not is_stub and page.public not in ("enfant", "adulte"):
            problems.append(f"{n}: <meta name=\"pepite-public\" content=\"enfant|adulte\"> missing")
        for value, _tag in page.links:
            check_link(n, value, posixpath.dirname(n), root, stage, pages, problems)
        if is_stub:
            m = REFRESH.match(page.refresh or "")
            if not m:
                problems.append(f"{n}: no <meta http-equiv=\"refresh\" content=\"0; url=…\">")
                continue
            target = m.group(1)
            own = []
            reached = check_link(n, target, posixpath.dirname(n), root, stage, pages, own)
            if own or reached not in pages:
                problems.append(f"{n}: the redirect « {target} » does not reach a help page or its anchor")
            if not any(v == target for v, t in page.links if t == "a"):
                problems.append(f"{n}: no visible link to « {target} »")
    # navigation: the same on every page, and every page in it
    navs = {n: [(split_link(h)[0], label) for h, label in p.nav] for n, p in pages.items() if not n.startswith(STUB_DIR + "/")}
    if navs:
        ref = navs.get("index.html") or next(iter(navs.values()))
        for n, nav in navs.items():
            if nav != ref:
                problems.append(f"{n}: the navigation differs from index.html")
        in_nav = {h for h, _ in ref}
        for n in navs:
            if n not in in_nav:
                problems.append(f"{n}: not in the navigation")
    check_css(root, stage, problems)
    # sizes
    total = 0
    for dirpath, dirs, files in os.walk(root):
        rel = os.path.relpath(dirpath, root).replace(os.sep, "/")
        if rel == "fonts" or rel.startswith("fonts/"):
            continue
        for f in files:
            if rel == "." and f in NOT_SHIPPED:
                continue
            size = os.path.getsize(os.path.join(dirpath, f))
            total += size
            if rel == "img" or rel.startswith("img/"):
                if size > MAX_IMAGE:
                    problems.append(f"{rel}/{f}: {size} bytes (at most {MAX_IMAGE})")
    if total > MAX_TOTAL:
        problems.append(f"the help weighs {total} bytes without the fonts (at most {MAX_TOTAL})")
    return problems


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("aide")
    p.add_argument("--stage", action="store_true", help="a built help: linked app files and fonts must exist")
    a = p.parse_args(argv)
    if not os.path.isdir(a.aide):
        print(f"no help folder: {a.aide}", file=sys.stderr)
        return 2
    problems = check(a.aide, a.stage)
    for pr in problems:
        print("HELP: " + pr)
    print(f"help lint: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
