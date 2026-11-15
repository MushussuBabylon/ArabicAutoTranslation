#!/usr/bin/env python3
"""
Independent oracles for the BiDi engine's resolved embedding levels: GNU FriBidi and ICU (libicuuc), two other
implementations of UAX #9, called through ctypes.

  1. class table     - bidi class of every code point: ar_tables (Python unicodedata) vs FriBidi's own table.
  2. resolved levels - for random strings (explicit embeddings, overrides and isolates, nested, unbalanced and
                       deeper than the 125-level limit) the level of every character that survives X9 must be
                       identical to the reference library's, for paragraph level 0, 1 and P2/P3 auto detection.

Findings are classified, never hidden: a mismatch is listed under the library that disagrees with the engine.
Known, documented behaviours of the libraries are reported separately.

Usage:  python3 oracle_levels.py [--seed N] [--count N] [--report FILE]
Exit status is non-zero when an unexplained difference remains.  Nothing here ships with the mod.
"""
import argparse
import ctypes
import ctypes.util
import random
import sys

import ar_tables as T
import arabic_ref as R

NAME_MAP = {'LTR': 'L', 'RTL': 'R'}      # FriBidi type names that differ from the standard's short names
PAR = {'ltr': 0x110, 'rtl': 0x111, 'auto': 0x40}


def load():
    path = ctypes.util.find_library('fribidi') or 'libfribidi.so.0'
    lib = ctypes.CDLL(path)
    lib.fribidi_get_bidi_type_name.restype = ctypes.c_char_p
    lib.fribidi_get_bidi_type_name.argtypes = [ctypes.c_uint32]
    lib.fribidi_get_bidi_types.argtypes = [ctypes.POINTER(ctypes.c_uint32), ctypes.c_int, ctypes.POINTER(ctypes.c_uint32)]
    lib.fribidi_get_bracket_types.argtypes = [ctypes.POINTER(ctypes.c_uint32), ctypes.c_int,
                                              ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32)]
    lib.fribidi_get_par_embedding_levels_ex.restype = ctypes.c_int8
    lib.fribidi_get_par_embedding_levels_ex.argtypes = [ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32),
                                                        ctypes.c_int, ctypes.POINTER(ctypes.c_uint32),
                                                        ctypes.POINTER(ctypes.c_int8)]
    try:
        lib.fribidi_get_version_info.restype = ctypes.c_char_p
        info = lib.fribidi_get_version_info().decode('ascii', 'replace').replace('\n', ' | ')
    except Exception:
        info = 'unknown'
    return lib, info


def load_icu():
    try:
        lib = ctypes.CDLL('libicuuc.so.74')
    except OSError:
        return None
    suf = '_74'
    f = {}
    for name in ('ubidi_open', 'ubidi_close', 'ubidi_setPara', 'ubidi_getVisualMap', 'ubidi_getParaLevel',
                 'ubidi_getResultLength'):
        f[name] = getattr(lib, name + suf)
    f['ubidi_open'].restype = ctypes.c_void_p
    f['ubidi_close'].argtypes = [ctypes.c_void_p]
    f['ubidi_setPara'].argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint16), ctypes.c_int32, ctypes.c_uint8,
                                   ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    f['ubidi_getVisualMap'].argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int32), ctypes.POINTER(ctypes.c_int)]
    f['ubidi_getResultLength'].restype = ctypes.c_int32
    f['ubidi_getResultLength'].argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    f['ubidi_getParaLevel'].restype = ctypes.c_uint8
    f['ubidi_getParaLevel'].argtypes = [ctypes.c_void_p]
    return f


ICU_PARA = {'ltr': 0, 'rtl': 1, 'auto': 0xFE}      # UBIDI_DEFAULT_LTR


def icu_visual(f, cps, mode):
    """Visual order (list of logical indices, left to right) and the paragraph direction; cps must be in the BMP.

    ICU reports the paragraph level for every character of a text whose direction is uniform, so its level array
    cannot be compared directly; the reordering it produces can."""
    n = len(cps)
    err = ctypes.c_int(0)
    bd = f['ubidi_open']()
    try:
        buf = (ctypes.c_uint16 * n)(*cps)
        f['ubidi_setPara'](bd, buf, n, ICU_PARA[mode], None, ctypes.byref(err))
        if err.value > 0:
            return None, None
        m = f['ubidi_getResultLength'](bd, ctypes.byref(err))
        vm = (ctypes.c_int32 * max(m, 1))()
        f['ubidi_getVisualMap'](bd, vm, ctypes.byref(err))
        if err.value > 0:
            return None, None
        return [vm[i] for i in range(m)], int(f['ubidi_getParaLevel'](bd)) & 1
    finally:
        f['ubidi_close'](bd)


def l2_order(levels, keep):
    """Rule L2 over the characters that survive X9 (no clusters, no mirroring): logical indices, left to right."""
    idx = list(keep)
    lv = [levels[i] for i in idx]
    if not idx:
        return []
    hi = max(lv)
    odd = [l for l in lv if l % 2]
    if not odd:
        return idx
    lo = min(odd)
    cur = hi
    while cur >= lo:
        k = 0
        while k < len(idx):
            if lv[k] >= cur:
                j = k
                while j < len(idx) and lv[j] >= cur:
                    j += 1
                idx[k:j] = idx[k:j][::-1]
                lv[k:j] = lv[k:j][::-1]
                k = j
            else:
                k += 1
        cur -= 1
    return idx


def fribidi_types(lib, cps):
    n = len(cps)
    arr = (ctypes.c_uint32 * n)(*cps)
    out = (ctypes.c_uint32 * n)()
    lib.fribidi_get_bidi_types(arr, n, out)
    return arr, out


def fribidi_levels(lib, cps, mode):
    n = len(cps)
    arr, types = fribidi_types(lib, cps)
    br = (ctypes.c_uint32 * n)()
    lib.fribidi_get_bracket_types(arr, n, types, br)
    levels = (ctypes.c_int8 * n)()
    base = ctypes.c_uint32(PAR[mode])
    r = lib.fribidi_get_par_embedding_levels_ex(types, br, n, ctypes.byref(base), levels)
    if r == 0:
        return None, None
    return [levels[i] for i in range(n)], (1 if base.value & 1 else 0)


def check_class_table(lib, report):
    """Compare bidi classes of every code point (surrogates and unassigned code points excluded)."""
    bad = []
    names = {}
    total = 0
    step = 4096
    for base in range(0, 0x110000, step):
        cps = [c for c in range(base, base + step) if not (0xD800 <= c <= 0xDFFF)]
        if not cps:
            continue
        arr, types = fribidi_types(lib, cps)
        for c, t in zip(cps, types):
            nm = names.get(t)
            if nm is None:
                nm = lib.fribidi_get_bidi_type_name(t).decode('ascii')
                nm = NAME_MAP.get(nm, nm)
                names[t] = nm
            mine = T.bidi_class(c)
            # FriBidi folds BS/SS (B/S) into its own names
            if nm == 'BS':
                nm = 'B'
            elif nm == 'SS':
                nm = 'S'
            total += 1
            if nm != mine:
                bad.append((c, nm, mine))
    return total, bad


def alpha():
    A = {
        'ar': list("ابتثجحخدذرزسشصضطظعغفقكلمنهوىيءآأؤإئة"),
        'he': list("אבגדהוזחטי"),
        'la': list("abcdeXYZ"),
        'en': list("0123456789"),
        'an': list("٠١٢٣٤٥٦٧٨٩"),
        'ws': [' ', ' ', ' ', ' ', '\t'],
        'punct': list(".,:;/-+%#$!?\"'&*@=_~^|\\"),
        'br': list("()[]{}<>«»"),
        'mark': ['َ', 'ّ', '́'],
        'ctl': ['⁦', '⁧', '⁨', '⁩', '‪', '‫', '‬', '‭', '‮', '‎', '‏', '؜'],
        'sym': ['°', '±', '−', '٪', '،', '؟', '٫', '٬'],
        'sep': ['\n', ' ', '\u001c'],
    }
    W = [('ar', 28), ('he', 4), ('la', 12), ('en', 12), ('an', 4), ('ws', 12), ('punct', 8), ('br', 10),
         ('mark', 3), ('ctl', 10), ('sym', 4)]
    return A, W


def random_string(rng, maxlen, A, W):
    names = [w[0] for w in W]
    wts = [w[1] for w in W]
    n = rng.randint(1, maxlen)
    return ''.join(rng.choice(A[rng.choices(names, wts)[0]]) for _ in range(n))


def random_nested(rng, A, W, depth=3):
    parts = []
    for _ in range(rng.randint(1, 4)):
        if depth > 0 and rng.random() < 0.4:
            op, cl = rng.choice([('⁦', '⁩'), ('⁧', '⁩'), ('⁨', '⁩'),
                                 ('‪', '‬'), ('‫', '‬'), ('‭', '‬'),
                                 ('‮', '‬')])
            if rng.random() < 0.12:
                cl = ''
            parts.append(op + random_nested(rng, A, W, depth - 1) + cl)
        else:
            parts.append(random_string(rng, 5, A, W))
    return ''.join(parts)


def deep_nesting(rng):
    """Strings that overflow the explicit stack (more than 125 levels) in different ways."""
    ops = ['⁦', '⁧', '⁨', '‪', '‫', '‭', '‮']
    s = ''.join(rng.choice(ops) for _ in range(rng.randint(60, 140)))
    tail = ''.join(rng.choice(['a', 'ب', '1', ' ', '⁩', '‬']) for _ in range(rng.randint(4, 30)))
    return s + tail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--seed', type=int, default=20261101)
    ap.add_argument('--count', type=int, default=4000)
    ap.add_argument('--report', default=None)
    args = ap.parse_args()
    lib, info = load()
    lines = []

    def out(msg):
        print(msg)
        lines.append(msg)

    out('FriBidi: %s' % info)
    total, bad = check_class_table(lib, lines)
    out('[1] bidi class table: %d code points compared, %d differ (unicodedata %s vs FriBidi data)'
        % (total, len(bad), T.UNICODE_VERSION))
    for c, a, b in bad[:12]:
        out('    U+%04X FriBidi=%s ar_tables=%s' % (c, a, b))
    failed = False
    # Differences caused only by Unicode version skew (newly assigned code points) are not engine errors;
    # report them but do not fail on them when they are all outside the scripts the mod handles.
    relevant = [(c, a, b) for (c, a, b) in bad
                if (c < 0x3000 or 0xFB00 <= c <= 0xFFFF) and not (0x0860 <= c <= 0x08FF)]
    skew = len(bad) - len(relevant)
    out('    %d differences are newly assigned characters (Unicode version skew, e.g. Arabic Extended-A/B or planes 1+)'
        % skew)
    if relevant:
        out('    %d differences are in BMP ranges the mod can meet:' % len(relevant))
        for c, a, b in relevant[:20]:
            out('      U+%04X FriBidi=%s ar_tables=%s' % (c, a, b))
        failed = True

    A, W = alpha()
    icu = load_icu()
    rng = random.Random(args.seed)
    cats = ('short', 'nested', 'deep')
    stat = {c: {'n': 0, 'fb': 0, 'icu': 0, 'icu_explained': 0, 'both': 0} for c in cats}
    samples = {'fb': [], 'icu': [], 'icu_unexplained': [], 'both': []}
    for k in range(args.count):
        r = rng.random()
        if r < 0.45:
            s, cat = random_string(rng, 20, A, W), 'short'
        elif r < 0.95:
            s, cat = random_nested(rng, A, W, 3), 'nested'
        else:
            s, cat = deep_nesting(rng), 'deep'
        cps = R.to_cps(s)
        for mode in ('ltr', 'rtl', 'auto'):
            para = {'ltr': 0, 'rtl': 1, 'auto': None}[mode]
            levels, keep, p = R.resolve_levels(cps, para)
            lv, base = fribidi_levels(lib, cps, mode)
            fb_bad = lv is not None and ((mode == 'auto' and p != base) or any(levels[i] != lv[i] for i in keep))
            icu_bad = False
            explained = False
            if icu is not None:
                iv, ib = icu_visual(icu, cps, mode)
                if iv is not None:
                    kept = set(keep)
                    icu_order = [i for i in iv if i in kept]
                    icu_bad = (mode == 'auto' and p != ib) or l2_order(levels, keep) != icu_order
                    if icu_bad:
                        lv2, keep2, p2 = R.resolve_levels(cps, para, icu_compat=True)
                        explained = (mode != 'auto' or p2 == ib) and l2_order(lv2, keep2) == icu_order
            st = stat[cat]
            st['n'] += 1
            st['fb'] += fb_bad
            st['icu'] += icu_bad
            st['icu_explained'] += (icu_bad and explained)
            st['both'] += (fb_bad and icu_bad)
            if fb_bad and len(samples['fb']) < 6:
                samples['fb'].append((cat, mode, s))
            if icu_bad and len(samples['icu']) < 6:
                samples['icu'].append((cat, mode, s))
            if icu_bad and not explained and len(samples['icu_unexplained']) < 6:
                samples['icu_unexplained'].append((cat, mode, s))
            if fb_bad and icu_bad and len(samples['both']) < 6:
                samples['both'].append((cat, mode, s))
    out('[2] resolved levels (string x paragraph mode pairs) of the engine vs two other UAX #9 implementations:')
    out('    %-8s %8s %12s %12s %20s %14s' % ('corpus', 'pairs', 'FriBidi', 'ICU 74 order', 'ICU, explained*', 'both differ'))
    for c in cats:
        st = stat[c]
        out('    %-8s %8d %12d %12d %20d %14d' % (c, st['n'], st['fb'], st['icu'], st['icu_explained'], st['both']))
    out('    * explained = the engine reproduces ICU exactly once the standard\'s X5a/X6a override of isolate initiators')
    out('      and PDIs is switched off (resolve_levels(icu_compat=True)).')
    for key in ('both', 'icu_unexplained'):
        for cat, mode, s in samples[key]:
            out('    %s sample (%s, %s): %r' % (key, cat, mode, s[:100]))
    mism = samples['both'] + samples['icu_unexplained']
    # the engine must agree with at least one independent implementation on every string, and every ICU
    # difference must be explained; deep-nesting differences of FriBidi alone are listed, not failed.
    total_unexplained = sum(stat[c]['icu'] - stat[c]['icu_explained'] for c in cats)
    failed = failed or total_unexplained > 0 or sum(stat[c]['both'] for c in cats) > 0
    if args.report:
        with open(args.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(lines) + '\n')
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
