#!/usr/bin/env python3
"""
Unicode data tables for the Arabic engine, derived from Python's `unicodedata` (Unicode 15.1 for
Python 3.13) - nothing is downloaded and nothing is typed in by hand except the documented
approximations for characters that have no presentation forms.

The same builders feed:
  * arabic_ref.py       - the Python reference engine (validated against Chromium)
  * gen_cs_tables.py    - Source/ArabicAutoTranslation/Arabic/Core/ArabicTables.cs (the C# engine)
so reference and port are guaranteed to use identical data.
"""
import unicodedata as ud

UNICODE_VERSION = ud.unidata_version

# Bidi class ids. The C# enum (BidiClass) uses exactly this order.
CLASSES = ['L', 'R', 'AL', 'EN', 'ES', 'ET', 'AN', 'CS', 'NSM', 'BN', 'B', 'S', 'WS', 'ON',
           'LRE', 'LRO', 'RLE', 'RLO', 'PDF', 'LRI', 'RLI', 'FSI', 'PDI']
CID = {c: i for i, c in enumerate(CLASSES)}

# DerivedBidiClass.txt "@missing" defaults for unassigned code points
_DEFAULT_RANGES = [
    (0x0590, 0x05FF, 'R'), (0x0600, 0x07BF, 'AL'), (0x07C0, 0x085F, 'R'), (0x0860, 0x08FF, 'AL'),
    (0x20A0, 0x20CF, 'ET'), (0xFB1D, 0xFB4F, 'R'), (0xFB50, 0xFDCF, 'AL'), (0xFDF0, 0xFDFF, 'AL'),
    (0xFE70, 0xFEFF, 'AL'), (0x10800, 0x10CFF, 'R'), (0x10D00, 0x10D3F, 'AL'), (0x10D40, 0x10EBF, 'R'),
    (0x10EC0, 0x10EFF, 'AL'), (0x10F00, 0x10F2F, 'R'), (0x10F30, 0x10F6F, 'AL'), (0x10F70, 0x10FFF, 'R'),
    (0x1E800, 0x1EC6F, 'R'), (0x1EC70, 0x1ECBF, 'AL'), (0x1ECC0, 0x1ECFF, 'R'), (0x1ED00, 0x1ED4F, 'AL'),
    (0x1ED50, 0x1EDFF, 'R'), (0x1EE00, 0x1EEFF, 'AL'), (0x1EF00, 0x1EFFF, 'R'),
]


def _default_class(cp):
    if 0xFDD0 <= cp <= 0xFDEF or (cp & 0xFFFE) == 0xFFFE:
        return 'BN'
    if 0xE0000 <= cp <= 0xE0FFF or cp == 0x2065 or 0xFFF0 <= cp <= 0xFFF8:
        return 'BN'
    for a, b, c in _DEFAULT_RANGES:
        if a <= cp <= b:
            return c
    return 'L'


def bidi_class_name(cp):
    c = ud.bidirectional(chr(cp))
    if c == '':
        return _default_class(cp)
    return c


def build_bidi_ranges():
    """[(start, end, class_id)] for every maximal run of code points whose class is not L."""
    out = []
    cur_c = None
    cur_s = 0
    for cp in range(0x110000):
        c = bidi_class_name(cp)
        if c == 'L':
            c = None
        if c != cur_c:
            if cur_c is not None:
                out.append((cur_s, cp - 1, CID[cur_c]))
            cur_c = c
            cur_s = cp
    if cur_c is not None:
        out.append((cur_s, 0x10FFFF, CID[cur_c]))
    return out


def _swap_name(name):
    n = name
    if 'LEFT-TO-RIGHT' in n or 'RIGHT-TO-LEFT' in n:
        return None
    pairs = [('LEFT', '\0A'), ('RIGHT', 'LEFT'), ('\0A', 'RIGHT'),
             ('LESS-THAN', '\0B'), ('GREATER-THAN', 'LESS-THAN'), ('\0B', 'GREATER-THAN')]
    for a, b in pairs:
        n = n.replace(a, b)
    return n if n != name else None


def build_mirror_pairs():
    """{cp: mirrored cp} for Bidi_Mirrored characters whose partner can be found by name."""
    out = {}
    for cp in range(0x110000):
        ch = chr(cp)
        if not ud.mirrored(ch):
            continue
        name = ud.name(ch, '')
        if not name:
            continue
        sw = _swap_name(name)
        if sw is None:
            continue
        try:
            other = ord(ud.lookup(sw))
        except KeyError:
            continue
        if ud.mirrored(chr(other)):
            out[cp] = other
    # the partner relation must be symmetric
    for a, b in list(out.items()):
        if out.get(b) != a:
            del out[a]
    return out


def build_bracket_pairs():
    """{open cp: close cp} for the Bidi_Paired_Bracket pairs (Ps/Pe punctuation with name partners)."""
    out = {}
    for cp in range(0x110000):
        ch = chr(cp)
        if ud.category(ch) != 'Ps':
            continue
        name = ud.name(ch, '')
        sw = _swap_name(name) if name else None
        if sw is None:
            continue
        try:
            other = ord(ud.lookup(sw))
        except KeyError:
            continue
        if ud.category(chr(other)) == 'Pe':
            out[cp] = other
    return out


# canonical equivalents used by BD16 (U+2329/232A are canonically equivalent to U+3008/3009)
BRACKET_CANON = {0x2329: 0x3008, 0x232A: 0x3009}


def build_mark_ranges():
    """[(start, end)] of Mn / Me code points (cluster extenders for the cluster-preserving reorder)."""
    out = []
    cur_s = None
    for cp in range(0x110000):
        is_mark = ud.category(chr(cp)) in ('Mn', 'Me')
        if is_mark and cur_s is None:
            cur_s = cp
        elif not is_mark and cur_s is not None:
            out.append((cur_s, cp - 1))
            cur_s = None
    if cur_s is not None:
        out.append((cur_s, 0x10FFFF))
    return out


# ------------------------------------------------------------------------------------------------
# Arabic joining / presentation forms
# ------------------------------------------------------------------------------------------------
JT = {'U': 0, 'D': 1, 'R': 2, 'L': 3, 'C': 4, 'T': 5}
JT_NAMES = {v: k for k, v in JT.items()}

LAM = 0x0644
LAMALEF_ALEFS = (0x0622, 0x0623, 0x0625, 0x0627)

# Approximation for letters that have NO presentation forms in Unicode (they are shaped by font GSUB
# only). We only need their joining behaviour so that their neighbours pick the right form.
# Arabic Supplement (0750-077F): right-joining letters; every other letter there is dual-joining.
_SUPPL_R = {0x0759, 0x075A, 0x075B, 0x076B, 0x076C, 0x0771, 0x0773, 0x0774, 0x0778, 0x0779}
_NONJOINING_CF = {0x0600, 0x0601, 0x0602, 0x0603, 0x0604, 0x0605, 0x06DD, 0x070F, 0x08E2}


def build_forms():
    """({base: {tag: cp}}, {alef: {tag: cp}}) from single-character / lam-alef decompositions."""
    forms = {}
    lamalef = {}
    for cp in list(range(0xFB50, 0xFE00)) + list(range(0xFE70, 0xFF00)):
        d = ud.decomposition(chr(cp))
        if not d.startswith('<'):
            continue
        tag, rest = d[1:].split('> ', 1)
        if tag not in ('isolated', 'final', 'initial', 'medial'):
            continue
        parts = [int(x, 16) for x in rest.split()]
        if len(parts) == 1:
            slot = forms.setdefault(parts[0], {})
            assert tag not in slot, ('duplicate form', hex(parts[0]), tag)
            slot[tag] = cp
        elif len(parts) == 2 and parts[0] == LAM and parts[1] in LAMALEF_ALEFS:
            slot = lamalef.setdefault(parts[1], {})
            assert tag not in slot
            slot[tag] = cp
    return forms, lamalef


FORMS, LAMALEF = build_forms()


def joining_type(cp):
    """Joining type letter for cp, derived as documented in the module header."""
    if cp == 0x200D:
        return 'C'
    if cp == 0x200C:
        return 'U'
    if cp == 0x0640:
        return 'C'
    if cp in _NONJOINING_CF:
        return 'U'
    f = FORMS.get(cp)
    if f is not None:
        if 'initial' in f or 'medial' in f:
            return 'D'
        if 'final' in f:
            return 'R'
        return 'U'
    cat = ud.category(chr(cp))
    if cat in ('Mn', 'Me', 'Cf'):
        return 'T'
    if 0x0750 <= cp <= 0x077F and cat in ('Lo', 'Lm'):
        return 'R' if cp in _SUPPL_R else 'D'
    if 0x08A0 <= cp <= 0x08C9 and cat in ('Lo', 'Lm'):
        return 'D'
    return 'U'


def build_join_table():
    """[(cp, type_id)] for every code point in the Arabic ranges / joiners whose joining type is not U."""
    out = []
    ranges = [(0x0600, 0x06FF), (0x0750, 0x077F), (0x08A0, 0x08FF), (0x200C, 0x200F), (0x202A, 0x202E),
              (0x2066, 0x2069), (0xFB50, 0xFDFF), (0xFE70, 0xFEFF)]
    for a, b in ranges:
        for cp in range(a, b + 1):
            t = joining_type(cp)
            if t != 'U':
                out.append((cp, JT[t]))
    return out


def build_form_rows():
    """[(base, isolated, final, initial, medial)] with 0 for a missing form, sorted by base."""
    rows = []
    for base in sorted(FORMS):
        f = FORMS[base]
        rows.append((base, f.get('isolated', 0), f.get('final', 0), f.get('initial', 0), f.get('medial', 0)))
    return rows


def build_lamalef_rows():
    return [(a, LAMALEF[a].get('isolated', 0), LAMALEF[a].get('final', 0)) for a in sorted(LAMALEF)]


def build_unshape_pairs():
    """[(presentation cp, base cp)] for every single-form presentation character (used to un-shape input)."""
    out = []
    for base in sorted(FORMS):
        for tag in ('isolated', 'final', 'initial', 'medial'):
            if tag in FORMS[base]:
                out.append((FORMS[base][tag], base))
    return sorted(out)


def build_unshape_lamalef():
    """[(presentation cp, alef cp)] for the lam-alef ligature presentation characters."""
    out = []
    for a in sorted(LAMALEF):
        for tag in ('isolated', 'final'):
            if tag in LAMALEF[a]:
                out.append((LAMALEF[a][tag], a))
    return sorted(out)


# ------------------------------------------------------------------------------------------------
# lookup helpers used by the reference engine (the C# engine has equivalents on the same data)
# ------------------------------------------------------------------------------------------------
_BIDI_RANGES = None
_MARK_RANGES = None
_MIRROR = None
_BRACKETS = None
_JOIN = None


def _ensure():
    global _BIDI_RANGES, _MARK_RANGES, _MIRROR, _BRACKETS, _JOIN
    if _BIDI_RANGES is None:
        _BIDI_RANGES = build_bidi_ranges()
        _MARK_RANGES = build_mark_ranges()
        _MIRROR = build_mirror_pairs()
        _BRACKETS = build_bracket_pairs()
        _JOIN = dict(build_join_table())


def _bsearch(ranges, cp):
    lo, hi = 0, len(ranges) - 1
    while lo <= hi:
        mid = (lo + hi) // 2
        r = ranges[mid]
        if cp < r[0]:
            hi = mid - 1
        elif cp > r[1]:
            lo = mid + 1
        else:
            return r
    return None


def bidi_class(cp):
    """Bidi class NAME of cp, from the generated range table (so the table itself is what is tested)."""
    _ensure()
    r = _bsearch(_BIDI_RANGES, cp)
    return 'L' if r is None else CLASSES[r[2]]


def is_mark(cp):
    _ensure()
    return _bsearch(_MARK_RANGES, cp) is not None


def mirror_of(cp):
    _ensure()
    return _MIRROR.get(cp, cp)


def bracket_close(cp):
    """Close bracket if cp is an open bracket (canonicalised), else 0."""
    _ensure()
    c = _BRACKETS.get(cp)
    return 0 if c is None else c


def is_close_bracket_of(open_cp, close_cp):
    return True  # placeholder to keep API symmetrical; see arabic_ref.bracket_info


def brackets():
    _ensure()
    return _BRACKETS


def join_type(cp):
    _ensure()
    t = _JOIN.get(cp)
    return 'U' if t is None else JT_NAMES[t]


def is_arabic_letter_range(cp):
    return (0x0600 <= cp <= 0x06FF or 0x0750 <= cp <= 0x077F or 0x08A0 <= cp <= 0x08FF
            or 0xFB50 <= cp <= 0xFDFF or 0xFE70 <= cp <= 0xFEFF)


if __name__ == '__main__':
    r = build_bidi_ranges()
    print('unicode', UNICODE_VERSION, 'bidi ranges', len(r))
    print('mirror pairs', len(build_mirror_pairs()), 'bracket pairs', len(build_bracket_pairs()))
    print('mark ranges', len(build_mark_ranges()), 'join rows', len(build_join_table()))
    print('forms', len(build_form_rows()), 'lamalef', len(build_lamalef_rows()))
