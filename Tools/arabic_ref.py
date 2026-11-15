#!/usr/bin/env python3
"""
Reference implementation of the Arabic display engine (logical text -> display-ready string for an
LTR-only renderer such as Unity IMGUI).  The C# engine in Source/ArabicAutoTranslation/Arabic/Core is a
line-by-line port of this file; golden vectors for the C# tests are generated from it, and this file is
validated against Chromium's own BiDi / HarfBuzz output (Tools/oracle_chromium.py).

Stages for one line:
    tokenize (placeholders / rich-text tags are atoms or zero-width anchors)
 -> shape  (contextual forms, lam-alef ligatures, in LOGICAL order)
 -> UAX#9  (resolve levels, reorder clusters, mirror brackets)
 -> assemble (atoms expanded, rich-text spans re-wrapped around the visual extents of their text)

Everything operates on code points, never on UTF-16 units, and never reverses a string by hand.
"""
import re
import ar_tables as T

# ------------------------------------------------------------------------------------------------
# constants
# ------------------------------------------------------------------------------------------------
LRI, RLI, FSI, PDI = 0x2066, 0x2067, 0x2068, 0x2069
LRM, RLM, ALM = 0x200E, 0x200F, 0x061C
ZWNJ, ZWJ = 0x200C, 0x200D
TATWEEL = 0x0640
ATOM_BASE = 0xF0000            # supplementary private use area A: bidi class L, no joining
MAX_DEPTH = 125

ISOLATE_INITIATORS = ('LRI', 'RLI', 'FSI')
REMOVED_BY_X9 = ('RLE', 'LRE', 'RLO', 'LRO', 'PDF', 'BN')
NEUTRAL_OR_ISOLATE = ('B', 'S', 'WS', 'ON', 'FSI', 'LRI', 'RLI', 'PDI')

_BRACKET_CANON = T.BRACKET_CANON


def _canon(cp):
    return _BRACKET_CANON.get(cp, cp)


# ------------------------------------------------------------------------------------------------
# helpers
# ------------------------------------------------------------------------------------------------
def to_cps(s):
    return [ord(c) for c in s]


def from_cps(cps):
    return ''.join(chr(c) for c in cps)


def has_arabic_letters(s):
    """True when the text contains a strong right-to-left letter (Arabic / Hebrew etc.)."""
    for ch in s:
        c = ord(ch)
        if T.bidi_class(c) in ('R', 'AL'):
            return True
    return False


# ------------------------------------------------------------------------------------------------
# presentation-form handling
# ------------------------------------------------------------------------------------------------
_UNSHAPE = dict(T.build_unshape_pairs())
_UNSHAPE_LAM = dict(T.build_unshape_lamalef())


def unshape(s):
    """Map presentation forms (FB50-FDFF / FE70-FEFF) back to base letters. Idempotent on logical text."""
    out = []
    for ch in s:
        c = ord(ch)
        if c in _UNSHAPE:
            out.append(chr(_UNSHAPE[c]))
        elif c in _UNSHAPE_LAM:
            out.append(chr(T.LAM))
            out.append(chr(_UNSHAPE_LAM[c]))
        else:
            out.append(ch)
    return ''.join(out)


def has_presentation_forms(s):
    for ch in s:
        c = ord(ch)
        if c in _UNSHAPE or c in _UNSHAPE_LAM:
            return True
    return False


# ------------------------------------------------------------------------------------------------
# shaping (logical -> logical order with presentation forms)
# ------------------------------------------------------------------------------------------------
def shape(cps):
    """
    Returns (out_cps, owners) where owners[k] = (first, last_exclusive) source indices of out char k.
    Contextual forms come from Unicode's own presentation-form decompositions; joining behaviour from
    the joining types in ar_tables.  Transparent marks are skipped when looking for neighbours.
    """
    n = len(cps)
    jt = [T.join_type(c) for c in cps]
    out = []
    owners = []
    i = 0
    while i < n:
        c = cps[i]
        t = jt[i]
        if t != 'D' and t != 'R':
            out.append(c)
            owners.append((i, i + 1))
            i += 1
            continue

        p = i - 1
        while p >= 0 and jt[p] == 'T':
            p -= 1
        prev_joins = p >= 0 and jt[p] in ('D', 'L', 'C')

        if c == T.LAM and i + 1 < n and cps[i + 1] in T.LAMALEF:
            slot = T.LAMALEF[cps[i + 1]]
            lig = slot.get('final') if prev_joins else slot.get('isolated')
            if lig:
                out.append(lig)
                owners.append((i, i + 2))
                i += 2
                continue

        q = i + 1
        while q < n and jt[q] == 'T':
            q += 1
        next_joins = (t == 'D') and q < n and jt[q] in ('D', 'R', 'C')

        if prev_joins and next_joins:
            form = 'medial'
        elif prev_joins:
            form = 'final'
        elif next_joins:
            form = 'initial'
        else:
            form = 'isolated'

        f = T.FORMS.get(c, {})
        g = f.get(form)
        if not g:
            if form == 'medial':
                g = f.get('final')
            elif form == 'initial' or form == 'final':
                g = f.get('isolated')
        out.append(g if g else c)
        owners.append((i, i + 1))
        i += 1
    return out, owners


# ------------------------------------------------------------------------------------------------
# UAX #9
# ------------------------------------------------------------------------------------------------
def _dir_of(t):
    """Direction used by N0/N1 for a resolved type: L -> 'L', R/EN/AN -> 'R', otherwise None."""
    if t == 'L':
        return 'L'
    if t == 'R' or t == 'EN' or t == 'AN':
        return 'R'
    return None


def _first_strong_level(types, start, end, match_pdi):
    i = start
    while i < end:
        t = types[i]
        if t == 'L':
            return 0
        if t == 'R' or t == 'AL':
            return 1
        if t in ISOLATE_INITIATORS:
            if match_pdi[i] == -1:
                return None
            i = match_pdi[i]
        i += 1
    return None


def resolve_levels(cps, para_level=None, icu_compat=False):
    """
    Runs UAX #9 rules P2-I2 and L1 over one line / paragraph.
    Returns (levels, keep, para) where keep lists the indices that survive X9 (in logical order) and
    levels[i] is the resolved embedding level of every kept index.

    icu_compat=True reproduces ICU's (and therefore Blink's) one known deviation from X5a/X6a: isolate
    initiators and PDIs are NOT reset to L/R inside a directional override, and brackets inside an override
    still pair up (BD14 / BD15 look at the current type, which an override changes).  It exists only so that the
    oracles can classify mismatches; the shipped C# engine follows the standard.
    """
    n = len(cps)
    orig = [T.bidi_class(c) for c in cps]
    types = list(orig)

    # BD9: matching isolate initiators / PDIs
    match_pdi = [-1] * n
    match_init = [-1] * n
    st = []
    for i in range(n):
        if orig[i] in ISOLATE_INITIATORS:
            st.append(i)
        elif orig[i] == 'PDI':
            if st:
                j = st.pop()
                match_pdi[j] = i
                match_init[i] = j

    # P2 / P3
    if para_level is None:
        fl = _first_strong_level(orig, 0, n, match_pdi)
        para = 0 if fl is None else fl
    else:
        para = para_level

    # X1 - X8
    levels = [para] * n
    stack = [(para, 'ON', False)]          # (level, override, isolate)
    overflow_isolate = 0
    overflow_embedding = 0
    valid_isolate = 0
    for i in range(n):
        t = orig[i]
        top = stack[-1]
        if t in ('RLE', 'LRE', 'RLO', 'LRO'):
            levels[i] = top[0]
            if t == 'RLE' or t == 'RLO':
                new_level = top[0] + 1 if top[0] % 2 == 0 else top[0] + 2
            else:
                new_level = top[0] + 2 if top[0] % 2 == 0 else top[0] + 1
            if new_level <= MAX_DEPTH and overflow_isolate == 0 and overflow_embedding == 0:
                ov = 'ON'
                if t == 'RLO':
                    ov = 'R'
                elif t == 'LRO':
                    ov = 'L'
                stack.append((new_level, ov, False))
            elif overflow_isolate == 0:
                overflow_embedding += 1
        elif t in ISOLATE_INITIATORS:
            levels[i] = top[0]
            if top[1] != 'ON' and not icu_compat:
                types[i] = top[1]
            rtl = (t == 'RLI')
            if t == 'FSI':
                end = match_pdi[i] if match_pdi[i] != -1 else n
                rtl = (_first_strong_level(orig, i + 1, end, match_pdi) == 1)
            if rtl:
                new_level = top[0] + 1 if top[0] % 2 == 0 else top[0] + 2
            else:
                new_level = top[0] + 2 if top[0] % 2 == 0 else top[0] + 1
            if new_level <= MAX_DEPTH and overflow_isolate == 0 and overflow_embedding == 0:
                valid_isolate += 1
                stack.append((new_level, 'ON', True))
            else:
                overflow_isolate += 1
        elif t == 'PDI':
            if overflow_isolate > 0:
                overflow_isolate -= 1
            elif valid_isolate == 0:
                pass
            else:
                overflow_embedding = 0
                while not stack[-1][2]:
                    stack.pop()
                stack.pop()
                valid_isolate -= 1
            top = stack[-1]
            levels[i] = top[0]
            if top[1] != 'ON' and not icu_compat:
                types[i] = top[1]
        elif t == 'PDF':
            levels[i] = top[0]
            if overflow_isolate > 0:
                pass
            elif overflow_embedding > 0:
                overflow_embedding -= 1
            elif (not top[2]) and len(stack) >= 2:
                stack.pop()
        elif t == 'B':
            levels[i] = para
        elif t == 'BN':
            levels[i] = top[0]
        else:
            levels[i] = top[0]
            if top[1] != 'ON':
                types[i] = top[1]

    # X9
    keep = [i for i in range(n) if orig[i] not in REMOVED_BY_X9]
    if not keep:
        return levels, keep, para
    keep_pos = {}
    for p, i in enumerate(keep):
        keep_pos[i] = p

    # X10: level runs and isolating run sequences
    runs = []
    run_of = {}
    for i in keep:
        if runs and levels[runs[-1][-1]] == levels[i]:
            runs[-1].append(i)
        else:
            runs.append([i])
        run_of[i] = len(runs) - 1
    run_first = {}
    for ri, run in enumerate(runs):
        run_first[run[0]] = ri

    x_levels = list(levels)             # embedding levels after X1-X8 (I1/I2 below must not leak into sos/eos)
    sequences = []
    for ri, run in enumerate(runs):
        first = run[0]
        if orig[first] == 'PDI' and match_init[first] != -1:
            init = match_init[first]
            if init in run_of and runs[run_of[init]][-1] == init:
                continue
        seq = list(run)
        cur = run
        while True:
            last = cur[-1]
            if orig[last] in ISOLATE_INITIATORS and match_pdi[last] != -1 and match_pdi[last] in run_first:
                cur = runs[run_first[match_pdi[last]]]
                seq.extend(cur)
            else:
                break
        sequences.append(seq)

    for seq in sequences:
        level = x_levels[seq[0]]
        pos0 = keep_pos[seq[0]]
        prev_level = x_levels[keep[pos0 - 1]] if pos0 > 0 else para
        sos = 'R' if max(level, prev_level) % 2 == 1 else 'L'
        last = seq[-1]
        posl = keep_pos[last]
        if orig[last] in ISOLATE_INITIATORS and match_pdi[last] == -1:
            next_level = para
        elif posl + 1 < len(keep):
            next_level = x_levels[keep[posl + 1]]
        else:
            next_level = para
        eos = 'R' if max(level, next_level) % 2 == 1 else 'L'
        e = 'R' if level % 2 == 1 else 'L'

        t = [types[i] for i in seq]
        pre_w1 = list(t)
        m = len(seq)

        # W1
        prev = sos
        for k in range(m):
            if t[k] == 'NSM':
                t[k] = 'ON' if prev in ('LRI', 'RLI', 'FSI', 'PDI') else prev
            prev = t[k]
        # W2
        last_strong = sos
        for k in range(m):
            if t[k] in ('L', 'R', 'AL'):
                last_strong = t[k]
            elif t[k] == 'EN' and last_strong == 'AL':
                t[k] = 'AN'
        # W3
        for k in range(m):
            if t[k] == 'AL':
                t[k] = 'R'
        # W4
        for k in range(1, m - 1):
            if t[k] == 'ES' and t[k - 1] == 'EN' and t[k + 1] == 'EN':
                t[k] = 'EN'
            elif t[k] == 'CS':
                if t[k - 1] == 'EN' and t[k + 1] == 'EN':
                    t[k] = 'EN'
                elif t[k - 1] == 'AN' and t[k + 1] == 'AN':
                    t[k] = 'AN'
        # W5
        k = 0
        while k < m:
            if t[k] == 'ET':
                j = k
                while j < m and t[j] == 'ET':
                    j += 1
                before = k > 0 and t[k - 1] == 'EN'
                after = j < m and t[j] == 'EN'
                if before or after:
                    for x in range(k, j):
                        t[x] = 'EN'
                k = j
            else:
                k += 1
        # W6
        for k in range(m):
            if t[k] in ('ES', 'ET', 'CS'):
                t[k] = 'ON'
        # W7
        last_strong = sos
        for k in range(m):
            if t[k] == 'L' or t[k] == 'R':
                last_strong = t[k]
            elif t[k] == 'EN' and last_strong == 'L':
                t[k] = 'L'

        # N0: paired brackets
        stack_b = []
        pairs = []
        for k in range(m):
            if (orig[seq[k]] != 'ON') if icu_compat else (t[k] != 'ON'):
                continue                        # BD14 / BD15: only characters whose *current* type is ON pair up
            cp = cps[seq[k]]
            close = T.brackets().get(cp)
            cp_c = _canon(cp)
            if close is not None:
                if len(stack_b) == 63:
                    break
                stack_b.append((_canon(close), k))
            elif cp_c in _CLOSE_SET:
                for s in range(len(stack_b) - 1, -1, -1):
                    if stack_b[s][0] == cp_c:
                        pairs.append((stack_b[s][1], k))
                        del stack_b[s:]
                        break
        pairs.sort()
        for (o, c) in pairs:
            found_e = False
            found_opp = False
            for k in range(o + 1, c):
                s = _dir_of(t[k])
                if s is None:
                    continue
                if s == e:
                    found_e = True
                    break
                found_opp = True
            if found_e:
                newd = e
            elif found_opp:
                ctx = sos
                for k in range(o - 1, -1, -1):
                    s = _dir_of(t[k])
                    if s is not None:
                        ctx = s
                        break
                opp = 'L' if e == 'R' else 'R'
                newd = opp if ctx == opp else e
            else:
                continue
            t[o] = newd
            t[c] = newd
            for br in (o, c):
                k = br + 1
                while k < m and pre_w1[k] == 'NSM':
                    t[k] = newd
                    k += 1

        # N1 / N2
        k = 0
        while k < m:
            if t[k] in NEUTRAL_OR_ISOLATE:
                j = k
                while j < m and t[j] in NEUTRAL_OR_ISOLATE:
                    j += 1
                lead = _dir_of(t[k - 1]) if k > 0 else sos
                trail = _dir_of(t[j]) if j < m else eos
                newd = lead if lead == trail else e
                for x in range(k, j):
                    t[x] = newd
                k = j
            else:
                k += 1

        # I1 / I2
        for k in range(m):
            i = seq[k]
            if level % 2 == 0:
                if t[k] == 'R':
                    levels[i] = level + 1
                elif t[k] == 'AN' or t[k] == 'EN':
                    levels[i] = level + 2
            else:
                if t[k] == 'L' or t[k] == 'EN' or t[k] == 'AN':
                    levels[i] = level + 1

    # L1
    j = len(keep) - 1
    while j >= 0 and orig[keep[j]] in ('WS', 'FSI', 'LRI', 'RLI', 'PDI'):
        levels[keep[j]] = para
        j -= 1
    for p in range(len(keep)):
        if orig[keep[p]] in ('S', 'B'):
            levels[keep[p]] = para
            q = p - 1
            while q >= 0 and orig[keep[q]] in ('WS', 'FSI', 'LRI', 'RLI', 'PDI'):
                levels[keep[q]] = para
                q -= 1
    return levels, keep, para


_CLOSE_SET = set(_canon(c) for c in T.brackets().values())


_NO_MARK_BASE = ('S', 'B', 'LRI', 'RLI', 'FSI', 'PDI')


def _can_carry_marks(cp):
    """Segment separators, paragraph separators and isolate formatting characters are not clusters' bases."""
    return T.bidi_class(cp) not in _NO_MARK_BASE


def reorder(cps, para_level=None, icu_compat=False):
    """
    L2 + L4 over clusters (a base character plus its following Mn/Me marks stay together, base first).
    A mark that follows a tab, a paragraph separator or an isolate initiator / PDI starts a cluster of its own
    and keeps its own resolved level (exactly what the standard prescribes for such an orphan).
    Returns (visual, levels, para): visual is a list of (source index, output code point) in left-to-right
    drawing order.
    """
    levels, keep, para = resolve_levels(cps, para_level, icu_compat)
    clusters = []
    for i in keep:
        if clusters and T.is_mark(cps[i]) and _can_carry_marks(cps[clusters[-1][-1]]):
            clusters[-1].append(i)
        else:
            clusters.append([i])
    cl_levels = [levels[c[0]] for c in clusters]
    for c in clusters:
        for i in c:
            levels[i] = levels[c[0]]
    m = len(clusters)
    if m:
        highest = max(cl_levels)
        odd = [l for l in cl_levels if l % 2 == 1]
        if odd:
            lowest_odd = min(odd)
            lvl = highest
            while lvl >= lowest_odd:
                k = 0
                while k < m:
                    if cl_levels[k] >= lvl:
                        j = k
                        while j < m and cl_levels[j] >= lvl:
                            j += 1
                        clusters[k:j] = clusters[k:j][::-1]
                        cl_levels[k:j] = cl_levels[k:j][::-1]
                        k = j
                    else:
                        k += 1
                lvl -= 1
    visual = []
    for ci, c in enumerate(clusters):
        for idx, i in enumerate(c):
            cp = cps[i]
            if idx == 0 and cl_levels[ci] % 2 == 1:
                cp = T.mirror_of(cp)
            visual.append((i, cp))
    return visual, levels, para


# ------------------------------------------------------------------------------------------------
# tokenizer
# ------------------------------------------------------------------------------------------------
RICH_PAIRED = ('b', 'i', 'size', 'color', 'material')

_RE_TOKEN = re.compile(
    r'(?P<brace>\{[A-Za-z0-9_.\-:,#]+\})'
    r'|(?P<square>\[[A-Za-z_][A-Za-z0-9_.\-]*(?:\([^\[\]\r\n]*\))?\])'
    r'|(?P<close></(?:b|i|size|color|material)>)'
    r'|(?P<open><(?:b|i)>|<(?:size|color|material)=[^<>\r\n]*>)'
    r'|(?P<quad><quad[^<>\r\n]*>)'
    r'|(?P<angle><[A-Za-z][A-Za-z0-9_]*>)',
    re.I)

_RE_LINE = re.compile(r'(\r\n|\n|\r|\u0085|\u2029|\u001c|\u001d|\u001e|\\n)')   # every paragraph separator + the text "\n"


class Tok(object):
    __slots__ = ('kind', 'text', 'name', 'lead')

    def __init__(self, kind, text, name=None):
        self.kind = kind          # 'text' | 'atom' | 'open' | 'close'
        self.text = text
        self.name = name          # tag name for 'open' / 'close'
        self.lead = ''            # sign that travels inside the isolate of an 'atom'


def tokenize(line):
    toks = []
    pos = 0
    for m in _RE_TOKEN.finditer(line):
        if m.start() > pos:
            toks.append(Tok('text', line[pos:m.start()]))
        raw = m.group(0)
        g = m.lastgroup
        if g == 'open':
            name = raw[1:].split('=')[0].rstrip('>').lower()
            toks.append(Tok('open', raw, name))
        elif g == 'close':
            toks.append(Tok('close', raw, raw[2:-1].lower()))
        else:
            toks.append(Tok('atom', raw))
        pos = m.end()
    if pos < len(line):
        toks.append(Tok('text', line[pos:]))
    return toks


# ------------------------------------------------------------------------------------------------
# number / unit isolation (optional, documented deviation: expressed with explicit LRI/PDI isolates)
# ------------------------------------------------------------------------------------------------
_DIGITS = '0-9٠-٩۰-۹'
_RE_GLUE = re.compile(
    r'(?<![' + _DIGITS + r'])([+\-−]?)([' + _DIGITS + r']+(?:[.,][' + _DIGITS + r']+)*)'
    r'(%|‰|°[CFKcfk]?)?')
_RE_UNIT_AFTER_ATOM = re.compile(r'^(%|‰|°[CFKcfk]?)')


def _is_word_like(cp):
    """Letters / digits as seen by the bidi algorithm (strong or numeric classes); table driven on purpose."""
    return T.bidi_class(cp) in ('L', 'R', 'AL', 'EN', 'AN')


def _glue_numbers(s):
    """Wrap sign/number/unit-symbol groups of a text piece in LRI..PDI (as code point list)."""
    out = []
    pos = 0
    for m in _RE_GLUE.finditer(s):
        sign, unit = m.group(1), m.group(3)
        if not sign and not unit:
            continue
        out.extend(to_cps(s[pos:m.start()]))
        out.append(LRI)
        out.extend(to_cps(m.group(0)))
        out.append(PDI)
        pos = m.end()
    out.extend(to_cps(s[pos:]))
    return out


def _is_ltr_phrase(inner):
    """True when the text has no right-to-left / Arabic-digit characters and at least one letter or digit."""
    has_alnum = False
    for ch in inner:
        cls = T.bidi_class(ord(ch))
        if cls == 'R' or cls == 'AL' or cls == 'AN':
            return False
        if cls == 'L' or cls == 'EN':
            has_alnum = True
    return has_alnum


def _bracket_phrase_spans(s):
    """[(start, end_exclusive)] of ( .. ) / [ .. ] segments whose content is left-to-right only."""
    spans = []
    n = len(s)
    i = 0
    while i < n:
        ch = s[i]
        if ch == '(' or ch == '[':
            close = ')' if ch == '(' else ']'
            depth = 0
            end = -1
            j = i
            while j < n:
                if s[j] == ch:
                    depth += 1
                elif s[j] == close:
                    depth -= 1
                    if depth == 0:
                        end = j
                        break
                j += 1
            if end > 0 and _is_ltr_phrase(s[i + 1:end]):
                spans.append((i, end + 1))
                i = end + 1
                continue
        i += 1
    return spans


def _isolate_text(s, opts):
    """Logical code points of a text token, with LTR phrases / numbers wrapped in explicit isolates."""
    if not opts.ltr_isolates:
        return to_cps(s)
    out = []
    pos = 0
    for (a, b) in _bracket_phrase_spans(s):
        out.extend(_glue_numbers(s[pos:a]))
        out.append(LRI)
        out.extend(to_cps(s[a:b]))
        out.append(PDI)
        pos = b
    out.extend(_glue_numbers(s[pos:]))
    return out


# ------------------------------------------------------------------------------------------------
# display processor
# ------------------------------------------------------------------------------------------------
class DisplayOptions(object):
    def __init__(self, shape=True, reorder=True, ltr_isolates=True, base_rtl=True):
        self.shape = shape          # produce presentation forms
        self.reorder = reorder      # visual reordering (UAX #9)
        self.ltr_isolates = ltr_isolates   # isolate signed numbers, units and (LTR-only) bracketed phrases
        self.base_rtl = base_rtl    # force RTL paragraph direction (else P2/P3)


_CONTROLS = set([LRI, RLI, FSI, PDI, LRM, RLM, ALM, ZWNJ, ZWJ, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E])


def _process_line(line, carry, opts):
    """
    Processes one line (no line breaks).  `carry` is the list of opener tokens still open from earlier
    lines.  Returns (visual_string, new_carry).
    """
    toks = tokenize(line)
    for c in reversed(carry):
        toks.insert(0, Tok('open', c.text, c.name))

    # tag matching, ill-formed tags degrade to atoms
    stack = []
    spans = []     # [open_tok_index, close_tok_index or None]
    for ti, tk in enumerate(toks):
        if tk.kind == 'open':
            stack.append(ti)
        elif tk.kind == 'close':
            if stack and toks[stack[-1]].name == tk.name:
                spans.append([stack.pop(), ti])
            else:
                tk.kind = 'atom'
    new_carry = []
    for ti in stack:
        spans.append([ti, None])
        new_carry.append(toks[ti])
    spans.sort(key=lambda s: s[0])

    # logical sequence
    seq = []
    anchors = {}
    atoms = []
    ti = 0
    while ti < len(toks):
        tk = toks[ti]
        if tk.kind == 'text':
            s = tk.text
            # sign directly in front of an atom travels with it
            lead = ''
            if opts.ltr_isolates and ti + 1 < len(toks) and toks[ti + 1].kind == 'atom' and s and s[-1] in '+-−':
                before = s[-2] if len(s) > 1 else ' '
                if not (_is_word_like(ord(before)) or before in '.,'):
                    lead = s[-1]
                    s = s[:-1]
            seq.extend(_isolate_text(s, opts))
            toks[ti].text = s
            if lead:
                toks[ti + 1].lead = lead      # remember the sign for the atom
        elif tk.kind == 'atom':
            suffix = ''
            if opts.ltr_isolates and ti + 1 < len(toks) and toks[ti + 1].kind == 'text':
                m = _RE_UNIT_AFTER_ATOM.match(toks[ti + 1].text)
                if m:
                    suffix = m.group(1)
                    toks[ti + 1].text = toks[ti + 1].text[len(suffix):]
            atoms.append(tk.text)
            seq.append(LRI)
            if tk.lead:
                seq.extend(to_cps(tk.lead))
            seq.append(ATOM_BASE + len(atoms) - 1)
            seq.extend(to_cps(suffix))
            seq.append(PDI)
        else:
            anchors[ti] = len(seq)
        ti += 1

    if not any(T.bidi_class(c) in ('R', 'AL') for c in seq):
        return None, new_carry     # nothing to do: caller keeps the original line

    if opts.shape:
        shaped, owners = shape(seq)
    else:
        shaped, owners = list(seq), [(i, i + 1) for i in range(len(seq))]

    if opts.reorder:
        visual, _levels, _para = reorder(shaped, 1 if opts.base_rtl else None)
    else:
        visual = [(i, c) for i, c in enumerate(shaped)]

    vis = [(i, c) for (i, c) in visual if c not in _CONTROLS]

    # which spans contain which visible characters
    m = len(vis)
    opens_at = [[] for _ in range(m)]
    closes_at = [[] for _ in range(m)]
    for sp in spans:
        a = anchors[sp[0]]
        b = anchors[sp[1]] if sp[1] is not None else len(seq)
        member = [a <= owners[i][0] < b for (i, _c) in vis]
        p = 0
        while p < m:
            if member[p]:
                q = p
                while q + 1 < m and member[q + 1]:
                    q += 1
                opens_at[p].append(sp)
                closes_at[q].append(sp)
                p = q + 1
            else:
                p += 1

    out = []
    for p in range(m):
        for sp in sorted(opens_at[p], key=lambda s: s[0]):
            out.append(toks[sp[0]].text)
        c = vis[p][1]
        if ATOM_BASE <= c < ATOM_BASE + len(atoms):
            out.append(atoms[c - ATOM_BASE])
        else:
            out.append(chr(c))
        for sp in sorted(closes_at[p], key=lambda s: s[0], reverse=True):
            out.append(toks[sp[1]].text if sp[1] is not None else '</' + toks[sp[0]].name + '>')
    return ''.join(out), new_carry


def process_display(logical, opts=None):
    """Logical Arabic text -> display-ready text. Text without strong RTL letters is returned unchanged."""
    if opts is None:
        opts = DisplayOptions()
    if not has_arabic_letters(logical):
        return logical
    logical = unshape(logical)
    parts = _RE_LINE.split(logical)
    out = []
    carry = []
    for idx, part in enumerate(parts):
        if idx % 2 == 1:
            out.append(part)
            continue
        res, carry2 = _process_line(part, carry, opts)
        out.append(part if res is None else res)
        carry = carry2
    return ''.join(out)


if __name__ == '__main__':
    tests = [
        "مرحبا بك في عالم RimWorld",
        "مرحبا بك في RimWorld",
        "لديك 25 قطعة خشب و10 حديد",
        "هل أنت مستعد؟ (Ready?)",
        "تم بناء {0} بنجاح!",
        "افتح قائمة Settings ثم اختر Graphics",
        "معدل النجاح هو 75%",
        "درجة الحرارة: -15°C",
        "افتح Settings ثم انتقل إلى Mods واختر RimWorld",
        "أين ذهبت؟ وماذا حدث!",
    ]
    for s in tests:
        print(s)
        print('   ->', process_display(s))
