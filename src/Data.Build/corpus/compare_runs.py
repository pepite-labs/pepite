#!/usr/bin/env python3
"""Compare the Eval runs of A1 and D1 and write the verdict of the light corpus experiment (plan corpus-light 0.6).

    python -I compare_runs.py <runs-dir> [--a A1] [--d D1] [--ctx v090] [--out <runs-dir>/../results.md]

Layout read (one folder per database label, written by the Phase 1 commands):

    <runs-dir>/<label>/{E1,E2,E3,W,S-unseen}.dump   Predict.Eval --dump: index<TAB>context<TAB>word<TAB>letters typed
    <runs-dir>/<label>/cases.log                    Predict.Eval <db> cases.json
    <runs-dir>/<label>/personal.log                 Predict.Eval <db> cases-personal.json --user personal-fixture.json
    <runs-dir>/<label>/spelling.log                 Predict.Eval <db> spelling-cases.json --spelling
    <runs-dir>/<label>/ginvented.log                Predict.Eval <db> g-invented.jsonl --grammar-predict
    <runs-dir>/<label>/grammar-test.log             Predict.Eval <db> grammar-cases.json --grammar-predict --split test
    <runs-dir>/<label>/db_checks.json               db_checks.py --json

Metric: KSS(E) = mean of the KSS of E1, E2 and E3 (equal weights); Δ = D1 − A1 in points, paired on the same words
(the `words` table is identical, so both runs evaluate the same words in the same order: checked). 95 % interval: 1 000
bootstrap resamples of the sentence blocks of each set (seed below), the same resample for A1 and D1.
"""
import argparse
import json
import os
import random
import re
import sys

DECISION = ("E1", "E2", "E3")
CONTEXT = ("W", "S-unseen")
SEED = 20261115
RESAMPLES = 1000
OCR_MAX = 2e-4            # « OCR n-gram mass about 0 »: strict share (db_checks) at most 0.02 % of any n-gram table
PS_MARGIN = 0.005         # passé-simple mass ≤ A1 + 0.5 pt


def read_dump(path):
    """[(index, word, letters typed, sentence block)] of a Predict.Eval --dump file. The dump has no sentence id: a word
    starts a new block unless its context (the last 3 words before it) continues the previous word's context + word
    (<continues>). A gap of 3 or more skipped tokens also starts a block: blocks may be finer than sentences."""
    out, block, prev = [], -1, None
    with open(path, encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) != 4:
                continue
            ctx = p[1].split()
            if prev is None or not continues(prev[0], prev[1], ctx):
                block += 1
            out.append((int(p[0]), p[2], int(p[3]), block))
            prev = (ctx, p[2])
    return out


def continues(prev_ctx, prev_word, ctx):
    """Is <ctx> (the last 3 tokens before a word) the continuation of the line whose previous evaluated word was
    <prev_word> after <prev_ctx>? Within a line the context is the line so far (up to 3 tokens), so it holds the previous
    word with the previous context's tail before it."""
    full = prev_ctx + [prev_word]
    if len(ctx) < 3 and (len(ctx) <= len(prev_ctx) or ctx[:len(full)] != full):
        return False
    for k in range(len(ctx)):
        if ctx[k] == prev_word and k + 1 <= len(full) and ctx[:k + 1] == full[-(k + 1):]:
            return True
    return False


def pair(a, d, name="set"):
    """[(letters, used A, used D)] for two dumps of the same held-out set; stops if they do not evaluate the same words."""
    if len(a) != len(d):
        raise SystemExit("%s: %d words in A, %d in D — not the same evaluated words" % (name, len(a), len(d)))
    out = []
    for (ia, wa, ua, block), (idd, wd, ud, _) in zip(a, d):
        if ia != idd or wa != wd:
            raise SystemExit("%s: word %d differs (%s / %s)" % (name, ia, wa, wd))
        out.append((len(wa), ua, ud, block))
    return out


def kss(rows, col):
    letters = sum(r[0] for r in rows)
    return 100.0 * (1 - sum(r[col] for r in rows) / letters) if letters else 0.0


def pooled(sets, col):
    return sum(kss(sets[s], col) for s in DECISION) / len(DECISION)


def bootstrap(sets, n=RESAMPLES, seed=SEED):
    """95 % percentile interval of Δ KSS(E) (D − A, points), resampling the sentence blocks of each set with replacement
    (the words of one sentence are not independent; review 2026-11-16)."""
    rng = random.Random(seed)
    sums = {}
    for s in DECISION:
        b = {}
        for i, r in enumerate(sets[s]):
            t = b.setdefault(r[3] if len(r) > 3 else i, [0, 0, 0])
            t[0] += r[0]
            t[1] += r[1]
            t[2] += r[2]
        sums[s] = list(b.values())
    deltas = []
    for _ in range(n):
        d = 0.0
        for s in DECISION:
            pick = rng.choices(sums[s], k=len(sums[s]))
            letters = sum(x[0] for x in pick)
            d += 100.0 * (sum(x[1] for x in pick) - sum(x[2] for x in pick)) / letters
        deltas.append(d / len(DECISION))
    deltas.sort()
    return deltas[int(0.025 * n)], deltas[int(0.975 * n) - 1]


# ---------------------------------------------------------------- log parsing

def _read(path):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def num(s):
    """A number as Predict.Eval prints it: some tables use the machine's culture (« 0,70 » on a French Windows)."""
    return float(s.replace(",", "."))


def parse_cases(text):
    m = re.search(r"^overall\s+(\d+)\s+([\d.,]+)\s+([\d.,]+)\s+([\d.,]+)", text or "", re.M)
    return {"n": int(m.group(1)), "top1": num(m.group(2)), "hit8": num(m.group(3))} if m else None


def parse_personal(text):
    m = re.search(r"within 2 letters ([\d.,]+); spelling pairs \(« Mes fautes »\) top-1 ([\d.,]+)", text or "")
    return {"names2": num(m.group(1)), "fautes": num(m.group(2))} if m else None


def parse_spelling(text):
    m = re.search(r"gates: wrong reds (\d+)", text or "")
    return {"wrong_reds": int(m.group(1))} if m else None


def parse_ginvented(text):
    m = re.search(r"^all\s+(\d+)\s+([\d.,]+)\s+([\d.,]+)\s+([\d.,]+)", text or "", re.M)
    return {"n": int(m.group(1)), "top1": num(m.group(2)), "hit8": num(m.group(3))} if m else None


def parse_grammar_test(text):
    rows = re.findall(r"^(\S+)\s+(\d+)\s+.*\s([\d.,]+)\s*$", text or "", re.M)
    return {r[0]: num(r[2]) for r in rows if r[0] in ("all", "pooled", "overall")} or None


def parse_kss_log(text):
    m = re.search(r"keystroke savings \(KSS\): ([\d.,]+)%", text or "")
    return num(m.group(1)) if m else None


def load_label(runs, label):
    d = os.path.join(runs, label)
    res = {"dumps": {}, "logs": {}}
    for s in DECISION + CONTEXT:
        p = os.path.join(d, "%s.dump" % s)
        if os.path.exists(p):
            res["dumps"][s] = read_dump(p)
    res["cases"] = parse_cases(_read(os.path.join(d, "cases.log")))
    res["personal"] = parse_personal(_read(os.path.join(d, "personal.log")))
    res["spelling"] = parse_spelling(_read(os.path.join(d, "spelling.log")))
    res["ginvented"] = parse_ginvented(_read(os.path.join(d, "ginvented.log")))
    res["grammar_test"] = parse_grammar_test(_read(os.path.join(d, "grammar-test.log")))
    p = os.path.join(d, "db_checks.json")
    res["db"] = None
    if os.path.exists(p):
        with open(p, encoding="utf-8") as f:
            res["db"] = json.load(f)
    return res


def max_share(db, key):
    if not db:
        return None
    vals = [t[key] for t in db["tables"].values() if key in t]
    return max(vals) if vals else None


def total_share(db, key):
    if not db:
        return None
    m = [(t[key] * t["mass"], t["mass"]) for t in db["tables"].values() if key in t]
    return sum(x for x, _ in m) / sum(y for _, y in m) if m and sum(y for _, y in m) else None


# ---------------------------------------------------------------- gates and verdict

def gates(A, D, la="A1", ld="D1"):
    """[(name, passed or None when not measured, detail)]; la / ld name the baseline and the candidate in the text."""
    out = []

    def add(name, ok, detail):
        out.append((name.replace("A1", la), ok, detail.replace("D1", ld).replace("A1", la)))

    ca, cd = A.get("cases"), D.get("cases")
    add("cases.json hit@8 = 1.00, top-1 ≥ A1 − 0.02",
        None if not (ca and cd) else (cd["hit8"] >= 1.0 and cd["top1"] >= ca["top1"] - 0.02 - 1e-9),
        "D1 top-1 %s hit@8 %s; A1 top-1 %s" % (cd and cd["top1"], cd and cd["hit8"], ca and ca["top1"]))
    pd = D.get("personal")
    add("cases-personal ≥ 0.474, « Mes fautes » = 1.000",
        None if not pd else (pd["names2"] >= 0.474 - 1e-9 and pd["fautes"] >= 1.0 - 1e-9),
        "D1 %s / %s" % (pd and pd["names2"], pd and pd["fautes"]))
    sd = D.get("spelling")
    add("--spelling: 0 wrong reds", None if not sd else sd["wrong_reds"] == 0, "D1 %s" % (sd and sd["wrong_reds"]))
    ocr = max_share(D.get("db"), "ocr_strict_share")
    add("OCR n-gram mass ≈ 0 (strict list, ≤ %.2f %% of any table)" % (100 * OCR_MAX), None if ocr is None else ocr <= OCR_MAX,
        "D1 %s %% (with « ii » / « ia »: %s %%)" % (None if ocr is None else round(100 * ocr, 4),
                                                  None if not D.get("db") else round(100 * max_share(D["db"], "ocr_share"), 4)))
    ga, gd = A.get("ginvented"), D.get("ginvented")
    add("G-invented top-1 and hit@8 ≥ A1 − 2 pts",
        None if not (ga and gd) else (gd["top1"] >= ga["top1"] - 0.02 - 1e-9 and gd["hit8"] >= ga["hit8"] - 0.02 - 1e-9),
        "D1 %s / %s; A1 %s / %s" % (gd and gd["top1"], gd and gd["hit8"], ga and ga["top1"], ga and ga["hit8"]))
    pa, pdd = total_share(A.get("db"), "passe_simple_share"), total_share(D.get("db"), "passe_simple_share")
    add("passé-simple mass ≤ A1 + 0.5 pt (else owner review, book cap 15 %)",
        None if pa is None or pdd is None else pdd <= pa + PS_MARGIN,
        "D1 %s %%, A1 %s %%" % (None if pdd is None else round(100 * pdd, 3), None if pa is None else round(100 * pa, 3)))
    return out


def verdict(delta, ci, delta_e3, gate_list):
    """GO-D, TIE→D or D-LOSES (plan decision rule). A gate that was not measured counts as failed: no verdict on
    missing evidence. Δ ≥ +0.5 with an interval that includes 0 is not a win: it falls to TIE→D when ΔE3 allows."""
    failed = [g for g in gate_list if g[1] is not True]
    if delta <= -1.0 or delta_e3 <= -1.0 or failed:
        return "D-LOSES"
    if delta >= 0.5 and ci[0] > 0:
        return "GO-D"
    return "TIE→D"


def report(runs, a_label, d_label, ctx_label):
    A, D = load_label(runs, a_label), load_label(runs, d_label)
    C = load_label(runs, ctx_label) if ctx_label and os.path.isdir(os.path.join(runs, ctx_label)) else None
    for s in DECISION:
        if s not in A["dumps"] or s not in D["dumps"]:
            raise SystemExit("missing %s.dump for %s or %s" % (s, a_label, d_label))
    paired = {s: pair(A["dumps"][s], D["dumps"][s], s) for s in DECISION + CONTEXT if s in A["dumps"] and s in D["dumps"]}
    ka, kd = pooled(paired, 1), pooled(paired, 2)
    delta = kd - ka
    ci = bootstrap(paired)
    de3 = kss(paired["E3"], 2) - kss(paired["E3"], 1)
    gl = gates(A, D, a_label, d_label)
    v = verdict(delta, ci, de3, gl)
    L = ["# Light corpus experiment — results", "",
         "Verdict: **%s**" % v, "",
         "KSS(E) = mean of E1, E2, E3 (equal weights), paired on the same words. Δ = %s − %s." % (d_label, a_label), "",
         "| Set | words | %s KSS | %s KSS | Δ (pt) |%s" % (a_label, d_label, " %s KSS |" % ctx_label if C else ""),
         "|---|---|---|---|---|%s" % ("---|" if C else "")]
    for s in DECISION + CONTEXT:
        if s not in paired:
            continue
        rows = paired[s]
        extra = ""
        if C:
            cr = C["dumps"].get(s)
            extra = " %s |" % ("%.2f" % kss([(len(w), u, u) for _, w, u, _b in cr], 1) if cr else "—")
        L.append("| %s%s | %d | %.2f | %.2f | %+.2f |%s" % (s, "" if s in DECISION else " (context)", len(rows),
                                                          kss(rows, 1), kss(rows, 2), kss(rows, 2) - kss(rows, 1), extra))
    L += ["| **KSS(E)** | | **%.2f** | **%.2f** | **%+.2f** |%s" % (ka, kd, delta, " |" if C else ""), "",
          "95 %% bootstrap interval of Δ KSS(E) (%d resamples of the sentence blocks, seed %d): [%+.2f, %+.2f] pt."
          % (RESAMPLES, SEED, ci[0], ci[1]), "ΔE3 = %+.2f pt." % de3, "",
          "## Gates (%s)" % d_label, "", "| Gate | Result | Detail |", "|---|---|---|"]
    for name, ok, detail in gl:
        L.append("| %s | %s | %s |" % (name, {True: "pass", False: "FAIL", None: "not measured"}[ok], detail))
    L += ["", "## Other measures", ""]
    for lab, R in ((a_label, A), (d_label, D)) + (((ctx_label, C),) if C else ()):
        L.append("- %s: cases %s; personal %s; spelling %s; G-invented %s; grammar-cases test %s; DB %s"
                 % (lab, R["cases"], R["personal"], R["spelling"], R["ginvented"], R["grammar_test"],
                    None if not R["db"] else "%.1f MB" % (R["db"]["bytes"] / 1048576)))
    L += ["", "Decision rule (plan corpus-light): GO-D when Δ ≥ +0.5 pt with an interval above 0 and every gate passes; "
          "TIE→D when −1.0 < Δ (< +0.5, or ≥ +0.5 without a significant interval) and ΔE3 > −1.0 pt and every gate passes; "
          "D-LOSES otherwise (a gate not measured counts as failed). W, S-unseen and 0.9.0 are context only."]
    return "\n".join(L) + "\n", v


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("runs")
    ap.add_argument("--a", default="A1")
    ap.add_argument("--d", default="D1")
    ap.add_argument("--ctx", default="v090")
    ap.add_argument("--out")
    a = ap.parse_args(argv)
    text, v = report(a.runs, a.a, a.d, a.ctx)
    out = a.out or os.path.join(os.path.dirname(os.path.abspath(a.runs)), "results.md")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    sys.stdout.buffer.write(text.encode("utf-8"))
    sys.stdout.buffer.write(("verdict %s -> %s\n" % (v, out)).encode("utf-8"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
