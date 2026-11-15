#!/usr/bin/env python3
"""Debug helper: collect BiDi-order mismatches against Blink for a seed and delta-minimise each one."""
import random
import sys

from playwright.sync_api import sync_playwright

import ar_tables as T
import arabic_ref as R
import oracle_chromium as O


def check_items(page, items):
    """items: list of (s, dir). Returns list of bool ok."""
    res = page.evaluate("items => measure(items)", [{'s': s, 'dir': d} for (s, d) in items])
    out = []
    for (s, d), xs in zip(items, res):
        cps = R.to_cps(s)
        para = {'rtl': 1, 'ltr': 0, 'auto': None}[d]
        visual, _l, _p = R.reorder(cps, para)
        order = [i for (i, _c) in visual]
        seq = []
        for i in order:
            if xs[i] is None:
                continue
            if xs[i][1] - xs[i][0] <= 0.5:
                continue
            seq.append(i)
        ok = True
        for a, b in zip(seq, seq[1:]):
            ca = (xs[a][0] + xs[a][1]) / 2.0
            cb = (xs[b][0] + xs[b][1]) / 2.0
            if ca > cb + 0.6:
                ok = False
                break
        out.append(ok)
    return out


def minimise(page, s, d):
    cur = s
    changed = True
    while changed:
        changed = False
        cands = [cur[:i] + cur[i + 1:] for i in range(len(cur))]
        if not cands:
            break
        oks = check_items(page, [(c, d) for c in cands])
        for c, ok in zip(cands, oks):
            if not ok:
                cur = c
                changed = True
                break
    return cur


def describe(s):
    return ' '.join('%s:%04X' % (T.bidi_class(ord(c)), ord(c)) for c in s)


def main():
    seed = int(sys.argv[1]) if len(sys.argv) > 1 else 22
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 2500
    with sync_playwright() as p:
        b = p.chromium.launch()
        page = b.new_page()
        rng = random.Random(seed)
        n, bad = O.oracle_bidi(page, rng, count)
        print('checked', n, 'bad', len(bad))
        seen = set()
        for (s, d, _order) in bad:
            m = minimise(page, s, d)
            key = (m, d)
            if key in seen:
                continue
            seen.add(key)
            print(d, repr(m))
            print('    ', describe(m))
        b.close()


if __name__ == '__main__':
    main()
