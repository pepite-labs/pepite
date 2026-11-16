"""Build the lesson-domain evaluation set of plan 10A (personal word lists).

20 French Wikipedia articles on school topics: the first half of each article is the "lesson" a teacher would paste into
« Nouvelle leçon… » (Predict.Eval runs the app's LessonExtractor on it), the second half is the test text. Predict.Eval
then measures keystroke savings on the test half without and with the lesson list (--lesson-set, --oov-typed).

Wikipedia is not in the model's training data (Leipzig news, Tatoeba, Vikidia, public-domain children's books). The plain-text extracts are fetched
from the MediaWiki API (text CC BY-SA 4.0; evaluation data only, never redistributed nor committed); each article's
revision id and the SHA-256 of its text are written to manifest.json, so a later run can tell whether the set changed.
Only the first 20 000 characters of an article are used (long articles would dominate the set); headings are dropped.

usage: python -I build_lesson_set.py <out_dir> [--chars 20000]
"""
import argparse
import hashlib
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

TOPICS = [
    "Volcan", "Dinosauria", "Système solaire", "Égypte antique", "Château fort", "Photosynthèse", "Abeille domestique",
    "Glacier", "Baleine", "Révolution française", "Gladiateur", "Vikings", "Squelette humain", "Électricité",
    "Cycle de l'eau", "Forêt tropicale", "Sahara", "Manchot empereur", "Préhistoire", "Énergie éolienne",
]
API = "https://fr.wikipedia.org/w/api.php"
AGENT = "lesson-set-builder/1.0 (evaluation of a word predictor)"


def fetch(title):
    q = urllib.parse.urlencode({"action": "query", "prop": "extracts|revisions", "rvprop": "ids", "explaintext": 1,
                                "redirects": 1, "titles": title, "format": "json", "formatversion": 2})
    req = urllib.request.Request(API + "?" + q, headers={"User-Agent": AGENT})
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                page = json.load(r)["query"]["pages"][0]
            return page["title"], page["revisions"][0]["revid"], page["extract"]
        except urllib.error.HTTPError as e:
            if e.code != 429 or attempt == 5:
                raise
            time.sleep(15 * (attempt + 1))   # rate limited: wait and try again


def clean(text):
    lines = []
    for line in text.splitlines():
        line = line.strip()
        if not line or re.match(r"^=+.*=+$", line):
            continue
        lines.append(line)
    return lines


def halves(lines, chars):
    kept, n = [], 0
    for line in lines:
        if n >= chars:
            break
        kept.append(line)
        n += len(line)
    total, acc = sum(len(x) for x in kept), 0
    for i, line in enumerate(kept):
        acc += len(line)
        if acc >= total / 2:
            return kept[:i + 1], kept[i + 1:]
    return kept, []


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("out")
    ap.add_argument("--chars", type=int, default=20000)
    a = ap.parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    manifest = []
    for i, topic in enumerate(TOPICS, 1):
        title, revid, text = fetch(topic)
        lesson, test = halves(clean(text), a.chars)
        ascii_title = unicodedata.normalize("NFKD", title.lower()).encode("ascii", "ignore").decode()
        name = f"{i:02d}-" + re.sub(r"[^a-z0-9]+", "-", ascii_title).strip("-")
        for suffix, part in ((".lesson.txt", lesson), (".test.txt", test)):
            with open(os.path.join(a.out, name + suffix), "w", encoding="utf-8", newline="\n") as f:
                f.write("\n".join(part) + "\n")
        manifest.append({"file": name, "title": title, "revid": revid,
                         "sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
                         "lesson_chars": sum(map(len, lesson)), "test_chars": sum(map(len, test))})
        print(f"{name}: {title} rev {revid}, lesson {manifest[-1]['lesson_chars']} + test {manifest[-1]['test_chars']} chars")
        time.sleep(3)
    with open(os.path.join(a.out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump({"source": "fr.wikipedia.org (CC BY-SA 4.0), plain-text extracts", "articles": manifest}, f,
                  ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
