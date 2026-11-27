#!/usr/bin/env python3
"""The owner's review page for the « mots exclus » candidates (plan offensive-words, step 0.4).

Reads ``<work>/exclusions/candidates.tsv`` and writes one static HTML file (no network request, no external file; the
same practice as the voice listening pages) next to it: rows grouped by category, the proposed decision pre-selected,
one « exclure / garder » control and a note per row, and an « Exporter les décisions » button that saves a JSON file
(``pepite-exclusions-review``, read by ``build_exclusions.py decisions``) or a TSV. The choices are kept in the
browser's local storage between visits. The page holds the words, so it stays in the work folder, never in the
repository. The console gets counts only.

    python -I make_review.py --candidates <work>/exclusions/candidates.tsv --out <work>/exclusions/review.html \
        [--batches <work>/exclusions/review-batches]

``--batches`` also writes plain-text files of 50 rows (``lot-001.txt``…: proposed « exclude » rows first, by category
then frequency) for a review on a phone; the owner answers with the ids to change, read by
``build_exclusions.py decisions-from-text``. ``--answers FILE`` (repeatable) shows the answers already given as the
current decision in the page and the batches.
"""
import argparse
import csv
import hashlib
import json
import os
import re
import sys

CAT_NAMES = {
    "R": "Injures ethniques, racistes ou religieuses",
    "H": "Injures homophobes ou transphobes",
    "S": "Injures contre les femmes (sexisme)",
    "D": "Injures validistes (handicap)",
    "X": "Sexuel / obscène",
    "V": "Vulgaire non sexuel (gardé)",
    "I": "Insultes générales",
    "G": "Drogues",
}
CAT_RULES = {   # the policy as of the final review (owner, 2026-11-27; plan offensive-words 0.3)
    "R": "injure exclue si elle domine (premier sens ou au moins la moitié des sens ; étiquette injurieuse, jamais « péjoratif » seul), si tous les sens sont signalés ou si LDNOOBW le liste ; mot ordinaire au sens blessant mineur gardé",
    "H": "injure exclue si elle domine (premier sens ou au moins la moitié des sens ; étiquette injurieuse, jamais « péjoratif » seul), si tous les sens sont signalés ou si LDNOOBW le liste ; mot ordinaire au sens blessant mineur gardé",
    "S": "injure exclue si elle domine (premier sens ou au moins la moitié des sens ; étiquette injurieuse, jamais « péjoratif » seul), si tous les sens sont signalés ou si LDNOOBW le liste ; mot ordinaire au sens blessant mineur gardé",
    "D": "exclu si le sens blessant domine ; mot ordinaire au sens blessant mineur gardé",
    "X": "exclu si le sens sexuel domine (actes sexuels, sexe, pornographie) ; termes anatomiques corrects jamais exclus",
    "V": "gros mots non sexuels, scatologie : gardés (propriétaire, 2026-11-27)",
    "I": "exclu si le sens d’insulte domine ; mots ordinaires et insultes familières non sexuelles gardés ; un registre familier seul n’est jamais une raison d’exclure",
    "G": "exclu si le sens drogue domine (argot de la drogue ; termes neutres, médicaux ou de prévention gardés à la revue)",
}
FIELDS = ["id", "lemma", "cat", "senses_total", "senses_flagged", "first_sense_flagged", "lexique_freq", "in_lexicon",
          "ldnoobw", "decision", "reason", "source", "signals"]

PAGE = r"""<!doctype html>
<html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Revue des mots exclus</title>
<style>
:root{--bg:#fbfaf7;--fg:#1d1d1f;--muted:#666;--line:#ddd;--ex:#b3261e;--exbg:#fbe9e7;--kp:#1b6e3a;--kpbg:#e6f4ea;--chg:#f3c623}
@media (prefers-color-scheme: dark){:root{--bg:#18181a;--fg:#eee;--muted:#aaa;--line:#333;--ex:#ff8a80;--exbg:#3a1d1b;--kp:#8fd9a8;--kpbg:#16301f}}
body{background:var(--bg);color:var(--fg);font:14px/1.4 system-ui,sans-serif;margin:0 16px 80px}
h1{font-size:20px;margin:16px 0 4px} h2{font-size:16px;margin:0}
.muted{color:var(--muted)} table{border-collapse:collapse;width:100%} th,td{border-bottom:1px solid var(--line);padding:3px 6px;text-align:left;vertical-align:top}
th{position:sticky;top:46px;background:var(--bg)} .num{text-align:right;font-variant-numeric:tabular-nums}
.bar{position:sticky;top:0;z-index:2;background:var(--bg);padding:8px 0;border-bottom:1px solid var(--line);display:flex;gap:8px;flex-wrap:wrap;align-items:center}
details{margin:12px 0;border:1px solid var(--line);border-radius:6px;padding:6px 8px} summary{cursor:pointer}
tr.exclude td.w{color:var(--ex);font-weight:600} tr.keep td.w{color:var(--kp)}
tr.changed td:first-child{box-shadow:inset 4px 0 var(--chg)}
.seg label{padding:1px 6px;border:1px solid var(--line);border-radius:4px;cursor:pointer;white-space:nowrap}
.seg input{display:none} .seg input[value=exclude]:checked+span{background:var(--exbg);color:var(--ex)}
.seg input[value=keep]:checked+span{background:var(--kpbg);color:var(--kp)} .seg span{padding:1px 4px;border-radius:3px}
input.note{width:14em;background:transparent;color:var(--fg);border:1px solid var(--line)} button{cursor:pointer}
.why{font-size:12px;color:var(--muted);max-width:28em} .sig{font-size:11px;color:var(--muted)}
.blur td.w span{filter:blur(5px)} .blur td.w:hover span{filter:none}
#summary td,#summary th{position:static}
</style></head><body>
<h1>Revue des mots exclus</h1>
<p class="muted">Candidats tirés des étiquettes du Wiktionnaire (extraction kaikki.org du 2026-11-02), limités aux mots du
lexique de Pépite. Une décision par ligne : <b>exclure</b> (jamais proposé à l’élève) ou <b>garder</b> (liste autorisée
revue). La décision proposée est pré-cochée ; une ligne modifiée est marquée en jaune. Les choix restent dans ce
navigateur ; « Exporter » enregistre le fichier à rendre. Fichier : <code>__CANDS__</code> (SHA-256 <code>__SHA_SHORT__</code>…).</p>
<table id="summary"><thead><tr><th>Catégorie</th><th class="num">Candidats</th><th class="num">Proposés exclus</th>
<th class="num">Proposés gardés</th><th class="num">Exclus (actuel)</th><th class="num">Gardés (actuel)</th><th class="num">Modifiés</th></tr></thead><tbody></tbody></table>
<div class="bar">
<button id="export-json">Exporter les décisions (JSON)</button>
<button id="export-tsv">Exporter (TSV)</button>
<label><input type="checkbox" id="blur"> masquer les mots (survol pour lire)</label>
<label>Afficher <select id="filter"><option value="all">tout</option><option value="exclude">exclus</option>
<option value="keep">gardés</option><option value="changed">modifiés</option></select></label>
<label>Chercher <input id="q" size="12"></label>
<button id="reset" title="Revenir aux décisions proposées">Tout remettre aux propositions</button>
<span id="status" class="muted"></span>
</div>
<div id="cats"></div>
<script>
const DATA = __DATA__;
const META = __META__;
const KEY = "pepite-exclusions-review:" + META.sha256;
let state = {};
try { state = JSON.parse(localStorage.getItem(KEY) || "{}") || {}; } catch (e) { state = {}; }
function save() { try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) {} }
function dec(r) { return (state[r.id] && state[r.id].decision) || r.decision; }
function note(r) { return (state[r.id] && state[r.id].note) || ""; }
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;"}[c])); }
const root = document.getElementById("cats");
for (const c of META.cats) {
  const rows = DATA.filter(r => r.cat === c);
  const d = document.createElement("details"); d.open = rows.length <= 200; d.dataset.cat = c;
  d.innerHTML = `<summary><h2 style="display:inline">${c} — ${esc(META.names[c])}</h2> <span class="muted">(${rows.length} ; règle : ${esc(META.rules[c])})</span>
  <span style="margin-left:12px"><button data-all="exclude">tout exclure</button> <button data-all="keep">tout garder</button>
  <button data-all="proposed">propositions</button></span></summary>
  <table><thead><tr><th>id</th><th>mot</th><th>décision</th><th>proposé</th><th class="num">sens signalés</th><th class="num">fréq.</th>
  <th>LDNOOBW</th><th>raison proposée</th><th>note</th></tr></thead><tbody></tbody></table>`;
  const tb = d.querySelector("tbody");
  for (const r of rows) {
    const tr = document.createElement("tr"); tr.dataset.id = r.id;
    tr.innerHTML = `<td>${r.id}</td><td class="w"><span>${esc(r.lemma)}</span>${r.in_lexicon === "form" ? ' <span class="sig">(forme)</span>' : ""}
      <a class="sig" href="https://fr.wiktionary.org/wiki/${encodeURIComponent(r.lemma)}" target="_blank" rel="noopener noreferrer">wikt</a></td>
      <td class="seg"><label><input type="radio" name="d-${r.id}" value="exclude"><span>exclure</span></label>
      <label><input type="radio" name="d-${r.id}" value="keep"><span>garder</span></label></td>
      <td>${r.decision === "exclude" ? "exclure" : "garder"}</td>
      <td class="num">${r.senses_flagged}/${r.senses_total}${r.first_sense_flagged === "1" ? " (1er)" : ""}</td>
      <td class="num">${esc(r.lexique_freq)}</td><td>${r.ldnoobw === "1" ? "oui" : ""}</td>
      <td class="why">${esc(r.reason)}<div class="sig">${esc(r.signals)}</div></td>
      <td><input class="note" value="${esc(note(r))}"></td>`;
    tb.appendChild(tr);
  }
  root.appendChild(d);
}
const byId = Object.fromEntries(DATA.map(r => [r.id, r]));
function paint() {
  const f = document.getElementById("filter").value, q = document.getElementById("q").value.trim().toLowerCase();
  for (const tr of document.querySelectorAll("tr[data-id]")) {
    const r = byId[tr.dataset.id], v = dec(r);
    tr.querySelector(`input[value=${v}]`).checked = true;
    tr.className = v + (v !== r.decision ? " changed" : "");
    let show = f === "all" || (f === "changed" ? v !== r.decision : v === f);
    if (q && !r.lemma.toLowerCase().includes(q) && r.id.toLowerCase() !== q) show = false;
    tr.style.display = show ? "" : "none";
  }
  const tb = document.querySelector("#summary tbody"); tb.innerHTML = "";
  let tot = [0,0,0,0,0,0];
  for (const c of META.cats) {
    const rs = DATA.filter(r => r.cat === c);
    const v = [rs.length, rs.filter(r => r.decision === "exclude").length, rs.filter(r => r.decision === "keep").length,
      rs.filter(r => dec(r) === "exclude").length, rs.filter(r => dec(r) === "keep").length, rs.filter(r => dec(r) !== r.decision).length];
    v.forEach((x, i) => tot[i] += x);
    tb.insertAdjacentHTML("beforeend", `<tr><td>${c} — ${esc(META.names[c])}</td>${v.map(x => `<td class="num">${x}</td>`).join("")}</tr>`);
  }
  tb.insertAdjacentHTML("beforeend", `<tr><th>Total</th>${tot.map(x => `<th class="num">${x}</th>`).join("")}</tr>`);
}
root.addEventListener("change", e => {
  const tr = e.target.closest("tr[data-id]"); if (!tr) return;
  const id = tr.dataset.id; state[id] = state[id] || {};
  if (e.target.type === "radio") state[id].decision = e.target.value;
  save(); paint();
});
root.addEventListener("input", e => {
  if (!e.target.classList.contains("note")) return;
  const id = e.target.closest("tr").dataset.id; state[id] = state[id] || {}; state[id].note = e.target.value; save();
});
root.addEventListener("click", e => {
  const b = e.target.closest("button[data-all]"); if (!b) return; e.preventDefault();
  const c = b.closest("details").dataset.cat;
  for (const r of DATA.filter(r => r.cat === c)) {
    state[r.id] = state[r.id] || {};
    if (b.dataset.all === "proposed") delete state[r.id].decision; else state[r.id].decision = b.dataset.all;
  }
  save(); paint();
});
document.getElementById("filter").onchange = paint;
document.getElementById("q").oninput = paint;
document.getElementById("blur").onchange = e => document.body.classList.toggle("blur", e.target.checked);
document.getElementById("reset").onclick = () => { if (confirm("Revenir à toutes les décisions proposées ?")) { state = {}; save(); location.reload(); } };
function download(name, text, type) {
  const a = document.createElement("a"); a.href = URL.createObjectURL(new Blob([text], {type}));
  a.download = name; document.body.appendChild(a); a.click(); a.remove();
  document.getElementById("status").textContent = "Enregistré : " + name;
}
function stamp() { return new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-"); }
document.getElementById("export-json").onclick = () => {
  const out = {format: "pepite-exclusions-review", version: 1, candidates_sha256: META.sha256, exported: new Date().toISOString(),
    decisions: DATA.map(r => ({id: r.id, decision: dec(r), proposed: r.decision, cat: r.cat, note: note(r)}))};
  download("exclusions-decisions-" + stamp() + ".json", JSON.stringify(out, null, 1), "application/json");
};
document.getElementById("export-tsv").onclick = () => {
  const clean = s => String(s).replace(/[\t\r\n]+/g, " ");
  const lines = ["id\tdecision\tcat\tnote"].concat(DATA.map(r => [r.id, dec(r), r.cat, clean(note(r))].join("\t")));
  download("exclusions-decisions-" + stamp() + ".tsv", lines.join("\n") + "\n", "text/tab-separated-values");
};
paint();
</script></body></html>
"""


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()


def js(obj):
    return json.dumps(obj, ensure_ascii=True, separators=(",", ":")).replace("</", "<\\/")


def apply_answers(rows, answer_files):
    """The owner's text answers already given (« garder: C0055 ; exclure: C1234 ») shown as the current decision, so
    the page and the batches never ask again; returns the number of rows changed."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # python -I does not add the script folder
    import build_exclusions as B
    over = {}
    for p in answer_files or []:
        with open(p, encoding="utf-8") as f:
            over.update(B.parse_overrides(f.read()))
    n = 0
    for r in rows:
        d = over.get(r["id"])
        if not d:
            continue
        if d != r["decision"]:
            n += 1
            r["reason"] = "owner: %s (proposed %s) — %s" % ("garder" if d == "keep" else "exclure",
                                                           "garder" if r["decision"] == "keep" else "exclure",
                                                           r["reason"])
        else:
            r["reason"] = "owner: confirmed — " + r["reason"]
        r["decision"] = d
    return n


def build(cands_path, out_path, answer_files=None):
    with open(cands_path, encoding="utf-8", newline="") as f:
        rows = [{k: r[k] for k in FIELDS} for r in csv.DictReader(f, delimiter="\t", quoting=csv.QUOTE_NONE)]
    build.changed = apply_answers(rows, answer_files)
    sha = sha256_file(cands_path)
    cats = [c for c in "RHSDXVIG" if any(r["cat"] == c for r in rows)]
    meta = {"sha256": sha, "cats": cats, "names": CAT_NAMES, "rules": CAT_RULES}
    html = (PAGE.replace("__DATA__", js(rows)).replace("__META__", js(meta))
            .replace("__CANDS__", os.path.basename(cands_path)).replace("__SHA_SHORT__", sha[:12]))
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    return rows, sha


BATCH_HEAD = """Revue des mots exclus — lot {n}/{total} (lignes {first}–{last} sur {rows})
candidates.tsv SHA-256 {sha}
Colonnes : id | catégorie | mot | proposition | sens signalés/total | LDNOOBW 0/1 | raison
Réponse : seulement les changements, par exemple « garder: C0055, C0089 ; exclure: C1234 ».
Les autres lignes gardent la proposition.

"""


def batch_order(rows, seen=()):
    """Proposed « exclude » rows first, then « keep »; within each, the rows the owner has not seen yet first, then
    category R, H, S, D, X, V, I, G, then most frequent."""
    return sorted(rows, key=lambda r: (r["decision"] != "exclude", r["id"] in seen, "RHSDXVIG".index(r["cat"]),
                                       -float(r["lexique_freq"] or 0), r["id"]))


def read_ids(paths):
    """Row ids listed in files (owner answers, ``reviewed-ids.txt``): every ``C0123`` token."""
    out = set()
    for p in paths or []:
        with open(p, encoding="utf-8") as f:
            out.update(re.findall(r"\bC\d{4,}\b", f.read()))
    return out


def write_batches(rows, sha, out_dir, size=50, seen=()):
    """Plain UTF-8 text files of ``size`` rows for a review on a phone (the owner cannot open the HTML page there)."""
    os.makedirs(out_dir, exist_ok=True)
    for f in os.listdir(out_dir):
        if f.startswith("lot-") and f.endswith(".txt"):
            os.remove(os.path.join(out_dir, f))
    rows = batch_order(rows, seen)
    total = (len(rows) + size - 1) // size
    for n in range(total):
        part = rows[n * size:(n + 1) * size]
        lines = [BATCH_HEAD.format(n=n + 1, total=total, first=n * size + 1, last=n * size + len(part),
                                   rows=len(rows), sha=sha)]
        for r in part:
            lines.append("%s | %s | %s | %s | %s/%s | %s | %s\n" % (
                r["id"], r["cat"], r["lemma"], "exclure" if r["decision"] == "exclude" else "garder",
                r["senses_flagged"], r["senses_total"], r["ldnoobw"],
                ("déjà vu — " if r["id"] in seen else "") + r["reason"].replace("\n", " ")))
        with open(os.path.join(out_dir, "lot-%03d.txt" % (n + 1)), "w", encoding="utf-8", newline="\n") as f:
            f.write("".join(lines))
    return total


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--candidates", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--batches", help="also write text files of 50 rows here (review on a phone)")
    p.add_argument("--batch-size", type=int, default=50)
    p.add_argument("--answers", action="append", help="an owner text answer already given (repeatable)")
    p.add_argument("--seen", action="append", help="a file of ids the owner has already seen (repeatable); with the "
                                                     "answers' ids, they come after the unseen rows in the batches")
    a = p.parse_args(argv)
    rows, sha = build(a.candidates, a.out, a.answers)
    if a.answers:
        print("owner answers applied: %d rows changed" % build.changed)
    print("review page: %d rows, candidates sha256 %s, %d bytes" % (len(rows), sha, os.path.getsize(a.out)))
    if a.batches:
        seen = read_ids((a.answers or []) + (a.seen or []))
        n = write_batches(rows, sha, a.batches, a.batch_size, seen)
        left = sum(1 for r in rows if r["decision"] == "exclude" and r["id"] not in seen)
        print("text batches: %d files of up to %d rows; proposed « exclude » rows not seen yet: %d" % (
            n, a.batch_size, left))
    return 0


if __name__ == "__main__":
    sys.exit(main())
