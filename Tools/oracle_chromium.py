#!/usr/bin/env python3
"""
Independent oracle for the Arabic engine, using Chromium (Blink BiDi + HarfBuzz shaping) via Playwright.

  A. BiDi order   - random strings; for every string the visual order produced by arabic_ref.reorder()
                    must be non-decreasing in the x position Blink gives each character (so the two
                    orderings agree wherever Blink can tell characters apart).
  B. Shaping      - random Arabic words and the mandatory sample strings; the pre-shaped, pre-reordered
                    string drawn left-to-right (bidi-override) must be pixel-identical (within tolerance) to
                    the logical string rendered normally by Chromium in an RTL paragraph.

Usage:  python3 oracle_chromium.py [--seed N] [--count N] [--out DIR]
Exit status is non-zero when any check fails. Nothing here is part of the shipped mod.
"""
import argparse
import io
import json
import os
import random
import sys

from playwright.sync_api import sync_playwright
from PIL import Image, ImageChops, ImageFilter

import ar_tables as T
import arabic_ref as R

FONT = "'DejaVu Sans'"
THRESH = 0.004      # allowed unmatched-ink fraction (1 px dilation) before a string is reported

MEASURE_HTML = """<!doctype html><html><head><meta charset="utf-8"></head>
<body style="margin:0;font:22px %s"><div id="c"></div>
<script>
function measure(items) {
  const root = document.getElementById('c');
  const res = [];
  for (const it of items) {
    const d = document.createElement('div');
    d.style.whiteSpace = 'pre';
    d.dir = it.dir;
    const tn = document.createTextNode(it.s);
    d.appendChild(tn);
    root.appendChild(d);
    const xs = [];
    const r = document.createRange();
    let i = 0;
    for (const ch of it.s) {
      r.setStart(tn, i); r.setEnd(tn, i + ch.length);
      const rects = r.getClientRects();
      if (rects.length === 0) xs.push(null);
      else { const rc = rects[0]; xs.push([rc.left, rc.right]); }
      i += ch.length;
    }
    res.push(xs);
    root.removeChild(d);
  }
  return res;
}
</script></body></html>""" % FONT

ALPHA = {
    'ar': list("ابتثجحخدذرزسشصضطظعغفقكلمنهوىيءآأؤإئة"),
    'he': list("אבגדהוזחטי"),
    'la': list("abcdeXYZ"),
    'en': list("0123456789"),
    'an': list("٠١٢٣٤٥٦٧٨٩"),
    'ws': [' ', ' ', ' ', ' ', '\t'],
    'punct': list(".,:;/-+%#$!?\"'&*@=_~^|\\"),
    'br': list("()[]{}<>«»"),
    'mark': ['َ', 'ّ', '́'],
    'ctl': ['⁦', '⁧', '⁨', '⁩', '‪', '‫', '‬', '‭', '‮',
            '‎', '‏', '؜'],
    'sym': ['°', '±', '−', '٪', '،', '؟', '٫', '٬'],
}
WEIGHTS = [('ar', 30), ('he', 4), ('la', 14), ('en', 12), ('an', 4), ('ws', 12), ('punct', 8), ('br', 8),
           ('mark', 2), ('ctl', 5), ('sym', 4)]


# characters that are not drawn: a combining mark right after one of them has no base to sit on (Blink then gives
# the mark / the control a glyph box of its own, which says nothing about the engine), so the corpus avoids them
_NO_BASE = None


def sanitize_marks(s):
    """Drops combining marks that do not directly follow a drawn character (or another kept mark)."""
    global _NO_BASE
    if _NO_BASE is None:
        _NO_BASE = set(['S', 'B', 'BN', 'LRE', 'LRO', 'RLE', 'RLO', 'PDF', 'LRI', 'RLI', 'FSI', 'PDI'])
    out = []
    for ch in s:
        if T.is_mark(ord(ch)):
            if not out:
                continue
            prev = out[-1]
            if T.bidi_class(ord(prev)) in _NO_BASE or prev in '\u200e\u200f\u061c':
                continue
        out.append(ch)
    return ''.join(out)


def random_string(rng, maxlen):
    names = [w[0] for w in WEIGHTS]
    wts = [w[1] for w in WEIGHTS]
    n = rng.randint(1, maxlen)
    out = []
    for _ in range(n):
        k = rng.choices(names, wts)[0]
        out.append(rng.choice(ALPHA[k]))
    return sanitize_marks(''.join(out))


def random_nested(rng, depth=3):
    """Structured strings with nested (and sometimes unmatched) isolates / embeddings / overrides."""
    parts = []
    for _ in range(rng.randint(1, 4)):
        if depth > 0 and rng.random() < 0.4:
            op, cl = rng.choice([('\u2066', '\u2069'), ('\u2067', '\u2069'), ('\u2068', '\u2069'),
                                 ('\u202A', '\u202C'), ('\u202B', '\u202C'), ('\u202D', '\u202C'),
                                 ('\u202E', '\u202C')])
            if rng.random() < 0.12:
                cl = ''
            parts.append(op + random_nested(rng, depth - 1) + cl)
        else:
            parts.append(random_string(rng, 5))
    return ''.join(parts)


def random_arabic_word(rng):
    letters = list("ءآأؤإئابةتثجحخدذرزسشصضطظعغفقكلمنهوىي")
    n = rng.randint(1, 9)
    out = []
    for _ in range(n):
        r = rng.random()
        if r < 0.04:
            out.append('ـ')
        elif r < 0.07:
            out.append(rng.choice(['َ', 'ُ', 'ِ', 'ّ', 'ْ', 'ً']))
        else:
            out.append(rng.choice(letters))
    s = ''.join(out)
    if s[0] in 'ًَُِّْ':
        s = 'ب' + s
    return s


# ------------------------------------------------------------------------------------------------
def order_consistent(xs, order):
    """Blink's x positions are non-decreasing along the engine's visual order (zero-width characters ignored)."""
    seq = []
    for i in order:
        if xs[i] is None:
            continue
        if xs[i][1] - xs[i][0] <= 0.5:
            continue
        seq.append(i)
    for a, b in zip(seq, seq[1:]):
        ca = (xs[a][0] + xs[a][1]) / 2.0
        cb = (xs[b][0] + xs[b][1]) / 2.0
        if ca > cb + 0.6:
            return False
    return True


def oracle_bidi(page, rng, count):
    """
    Returns (items checked, failures, icu_deviations).  A string whose order disagrees with Blink under the
    standard's rules but agrees once ICU's one known deviation is applied (isolate initiators / PDIs are not
    reset to L / R inside a directional override, X5a / X6a) is listed in icu_deviations, not in failures.
    """
    page.set_content(MEASURE_HTML)
    items = []
    for _ in range(count):
        s = random_string(rng, 18) if rng.random() < 0.55 else sanitize_marks(random_nested(rng, 3))
        modes = ['rtl', 'ltr']
        # HTML dir=auto does not implement the isolate skipping of UAX#9 P2 for unmatched initiators
        if not any(c in s for c in '\u2066\u2067\u2068'):
            modes.append('auto')
        for d in modes:
            items.append({'s': s, 'dir': d})
    res = page.evaluate("items => measure(items)", items)
    bad = []
    icu_dev = []
    for it, xs in zip(items, res):
        s = it['s']
        cps = R.to_cps(s)
        para = {'rtl': 1, 'ltr': 0, 'auto': None}[it['dir']]
        visual, levels, p = R.reorder(cps, para)
        order = [i for (i, _c) in visual]
        if order_consistent(xs, order):
            continue
        visual2, _l2, _p2 = R.reorder(cps, para, icu_compat=True)
        if order_consistent(xs, [i for (i, _c) in visual2]):
            icu_dev.append((s, it['dir'], order))
        else:
            bad.append((s, it['dir'], order))
    return len(items), bad, icu_dev


# ------------------------------------------------------------------------------------------------
def render_rows(browser, rows, width=900):
    """rows: list of (html_fragment); returns list of PIL images (each row cropped to its ink bbox)."""
    imgs = []
    chunk = 60
    for k in range(0, len(rows), chunk):
        part = rows[k:k + chunk]
        html = ('<!doctype html><html><head><meta charset="utf-8"></head><body style="margin:0;background:#fff;'
                'color:#000;font:26px %s">' % FONT)
        for j, frag in enumerate(part):
            html += '<div style="height:56px;padding:6px 10px;box-sizing:border-box">%s</div>' % frag
        html += '</body></html>'
        page = browser.new_page(viewport={'width': width, 'height': 56 * len(part) + 8})
        page.set_content(html)
        page.evaluate("() => document.fonts.ready.then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))")
        png = page.screenshot(full_page=True)
        page.close()
        im = Image.open(io.BytesIO(png)).convert('L')
        for j in range(len(part)):
            row = im.crop((0, j * 56, width, (j + 1) * 56))
            inv = ImageChops.invert(row)
            bbox = inv.point(lambda v: 255 if v > 40 else 0).getbbox()
            imgs.append(row.crop(bbox) if bbox else row.crop((0, 0, 1, 1)))
    return imgs


def esc(s):
    return (s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;'))


def natural_frag(s):
    return '<span dir="rtl" style="display:inline-block;white-space:pre;unicode-bidi:isolate">%s</span>' % esc(s)


def visual_frag(v):
    # Blink would otherwise join un-shaped base letters by itself even under bidi-override, which would hide a
    # missing shaping step. Disabling the joining features makes this side a faithful model of Unity IMGUI:
    # every character is drawn from its own glyph, left to right, with no contextual substitution.
    return ('<span dir="ltr" style="display:inline-block;white-space:pre;unicode-bidi:bidi-override;'
            "font-feature-settings:'isol' 0,'init' 0,'medi' 0,'fina' 0,'fin2' 0,'fin3' 0,'med2' 0\">%s</span>"
            % esc(v))


def compare_images(a, b):
    """
    Tolerant ink comparison (robust to 1 px sub-pixel shifts, sensitive to wrong glyph shapes / dots).
    Returns (size_delta, unmatched_ink_fraction).
    """
    dw = abs(a.size[0] - b.size[0])
    dh = abs(a.size[1] - b.size[1])
    w = max(a.size[0], b.size[0])
    h = max(a.size[1], b.size[1])
    ca = Image.new('L', (w, h), 255)
    cb = Image.new('L', (w, h), 255)
    ca.paste(a, (0, 0))
    cb.paste(b, (0, 0))
    ia = ca.point(lambda v: 255 if v < 140 else 0)
    ib = cb.point(lambda v: 255 if v < 140 else 0)
    da = ia.filter(ImageFilter.MaxFilter(3))
    db = ib.filter(ImageFilter.MaxFilter(3))
    un_a = ImageChops.subtract(ia, db).histogram()[255]
    un_b = ImageChops.subtract(ib, da).histogram()[255]
    tot = ia.histogram()[255] + ib.histogram()[255]
    return (dw, dh), (un_a + un_b) / float(max(1, tot))


def oracle_shaping(browser, rng, count, outdir, samples):
    texts = list(samples)
    for _ in range(count):
        texts.append(random_arabic_word(rng))
    opts = R.DisplayOptions(ltr_isolates=True)
    nat_rows = []
    vis_rows = []
    for s in texts:
        annotated = R.from_cps(R._isolate_text(s, opts))       # same isolates the engine inserts
        v = R.process_display(s, opts)
        nat_rows.append(natural_frag(annotated))
        vis_rows.append(visual_frag(v))
    nat = render_rows(browser, nat_rows)
    vis = render_rows(browser, vis_rows)
    bad = []
    worst = 0.0
    for idx, (s, a, b) in enumerate(zip(texts, nat, vis)):
        delta, frac = compare_images(a, b)
        worst = max(worst, frac)
        if delta[0] > 3 or delta[1] > 3 or frac > THRESH:
            bad.append((s, R.process_display(s, opts), delta, frac, idx))
    os.makedirs(outdir, exist_ok=True)
    for (s, v, delta, frac, idx) in bad[:12]:
        w = max(nat[idx].size[0], vis[idx].size[0])
        sheet = Image.new('L', (w + 20, nat[idx].size[1] + vis[idx].size[1] + 30), 255)
        sheet.paste(nat[idx], (10, 10))
        sheet.paste(vis[idx], (10, 20 + nat[idx].size[1]))
        sheet.save(os.path.join(outdir, 'diff_%03d.png' % idx))
    # always keep a contact sheet of the mandatory samples (natural above, engine output below)
    for k, s in enumerate(samples):
        a, b = nat[k], vis[k]
        w = max(a.size[0], b.size[0])
        sheet = Image.new('L', (w + 20, a.size[1] + b.size[1] + 30), 255)
        sheet.paste(a, (10, 10))
        sheet.paste(b, (10, 20 + a.size[1]))
        sheet.save(os.path.join(outdir, 'sample_%02d.png' % (k + 1)))
    return len(texts), bad, worst


PHRASES = [
    "(مرحبا بك) [نص] «اقتباس»",
    "المستعمر الأول: {0} قتل {1}",
    "ابنِ برجاً ثم أنشئ منطقة تخزين",
    "الحمل والولادة في حوض النمو",
    "نقاء الجسد وسبات الموت",
    "لا تُبنى المسلة قبل الكيان",
    "وصلة نفسية (Psylink) مستوى 3",
    "تحذير: درجة الحرارة 40°C ثم -5°C",
    "مهمة جديدة: التحقيق في 12 موقعاً",
    "النتيجة 3/4 أو 100% أو +25",
    "الألم فضيلة؛ والتفوق خطأ.",
    "زرعة (implant) إلكترونية حيوية",
    "ثقة المستعمرين: 85٪ (جيدة)",
    "اختر: أ) نعم ب) لا",
    "ـ ـ ـ تجربة التطويل ـ ـ ـ",
    "ٱلْحَمْدُ لِلَّهِ رَبِّ ٱلْعَالَمِينَ",
]
PHRASES = [p for p in PHRASES if '{' not in p]     # {..} would be an atom for the engine, not plain text


def oracle_mixed(browser, rng, count, outdir):
    """End-to-end: random mixed strings, engine output vs Blink's own rendering of the logical string."""
    opts = R.DisplayOptions(ltr_isolates=True)
    texts = []
    tries = 0
    while len(texts) < count and tries < count * 20:
        tries += 1
        s = random_string(rng, 14)
        if not any(R.T.bidi_class(ord(c)) in ('R', 'AL') for c in s):
            continue
        if any(t.kind != 'text' for t in R.tokenize(s)):
            continue                                      # atoms / tags are covered by the unit tests
        if any(c in s for c in '\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\u200E\u200F\u061C'):
            continue                                      # explicit controls are covered by oracle A
        if '\t' in s or any(c in s for c in '\u00AB\u00BB\u2039\u203A'):
            continue
        # a combining mark must follow an Arabic letter (orphan marks are drawn on a dotted circle / clash with
        # the previous glyph in Blink, which says nothing about the engine)
        fixed = []
        for ch in s:
            if ch in '\u064E\u0651\u0301' and (ch == '\u0301' or not (fixed and 'ء' <= fixed[-1] <= 'ي')):
                continue
            fixed.append(ch)
        s = ''.join(fixed)
        if not s or not any(R.T.bidi_class(ord(c)) in ('R', 'AL') for c in s):
            continue
        texts.append(s)
    texts = PHRASES + texts
    nat_rows = []
    vis_rows = []
    for s in texts:
        annotated = R.from_cps(R._isolate_text(s, opts))
        nat_rows.append(natural_frag(annotated))
        vis_rows.append(visual_frag(R.process_display(s, opts)))
    nat = render_rows(browser, nat_rows)
    vis = render_rows(browser, vis_rows)
    bad = []
    worst = 0.0
    for idx, (s, a, b) in enumerate(zip(texts, nat, vis)):
        delta, frac = compare_images(a, b)
        worst = max(worst, frac)
        if delta[0] > 3 or delta[1] > 3 or frac > THRESH:
            bad.append((s, delta, frac, idx))
    os.makedirs(outdir, exist_ok=True)
    for (s, delta, frac, idx) in bad[:12]:
        w = max(nat[idx].size[0], vis[idx].size[0])
        sheet = Image.new('L', (w + 20, nat[idx].size[1] + vis[idx].size[1] + 30), 255)
        sheet.paste(nat[idx], (10, 10))
        sheet.paste(vis[idx], (10, 20 + nat[idx].size[1]))
        sheet.save(os.path.join(outdir, 'mixed_diff_%03d.png' % idx))
    return len(texts), bad, worst


SAMPLES = [
    "مرحبا بك في عالم RimWorld",
    "مرحبا بك في RimWorld",
    "لديك 25 قطعة خشب و10 حديد",
    "هل أنت مستعد؟ (Ready?)",
    "تم بناء 0 بنجاح!",
    "افتح قائمة Settings ثم اختر Graphics",
    "معدل النجاح هو 75%",
    "درجة الحرارة: -15°C",
    "افتح Settings ثم انتقل إلى Mods واختر RimWorld",
    "أين ذهبت؟ وماذا حدث!",
    "لا يوجد ألف لام ولاء؟ الآن إلى الأمام أبداً",
]


def negative_controls(browser, rng, outdir):
    """The oracle must FAIL when the engine is deliberately broken - otherwise it proves nothing."""
    results = []

    def run(label, setup, teardown):
        setup()
        try:
            n, bad, worst = oracle_mixed(browser, random.Random(7), 120, outdir)
            n2, bad2, worst2 = oracle_shaping(browser, random.Random(8), 40, outdir, SAMPLES)
        finally:
            teardown()
        detected = bool(bad) or bool(bad2)
        results.append((label, detected, len(bad) + len(bad2)))

    # 1. no shaping
    saved = R.DisplayOptions.__init__
    def no_shape_init(self, shape=False, reorder=True, ltr_isolates=True, base_rtl=True):
        saved(self, False, reorder, ltr_isolates, base_rtl)
    run('shaping disabled', lambda: setattr(R.DisplayOptions, '__init__', no_shape_init),
        lambda: setattr(R.DisplayOptions, '__init__', saved))
    # 2. no mirroring
    orig_mirror = T.mirror_of
    run('bracket mirroring disabled', lambda: setattr(T, 'mirror_of', lambda cp: cp),
        lambda: setattr(T, 'mirror_of', orig_mirror))
    # 3. no reordering
    def no_reorder_init(self, shape=True, reorder=False, ltr_isolates=True, base_rtl=True):
        saved(self, shape, False, ltr_isolates, base_rtl)
    run('reordering disabled', lambda: setattr(R.DisplayOptions, '__init__', no_reorder_init),
        lambda: setattr(R.DisplayOptions, '__init__', saved))
    # 4. one wrong contextual form (kaf medial -> kaf final)
    forms = T.FORMS[0x0643]
    medial = forms['medial']
    def break_form():
        forms['medial'] = forms['final']
    def restore_form():
        forms['medial'] = medial
    run('wrong medial form for KAF', break_form, restore_form)
    # 5. N0 bracket rule broken (treat every bracket pair as unresolved)
    orig_brackets = T.brackets
    run('paired-bracket table emptied', lambda: setattr(T, 'brackets', lambda: {}),
        lambda: setattr(T, 'brackets', orig_brackets))
    return results


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--seed', type=int, default=20261031)
    ap.add_argument('--count', type=int, default=1500)
    ap.add_argument('--words', type=int, default=240)
    ap.add_argument('--mixed', type=int, default=300)
    ap.add_argument('--negative-control', action='store_true')
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out'))
    args = ap.parse_args()
    rng = random.Random(args.seed)
    failed = False
    if os.path.isdir(args.out):
        for f in os.listdir(args.out):
            if f.startswith(('diff_', 'mixed_diff_', 'sample_')):
                os.remove(os.path.join(args.out, f))
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        page = browser.new_page()
        total, bad, icu_dev = oracle_bidi(page, rng, args.count)
        print('[A] BiDi order vs Blink: %d strings checked (rtl/ltr/auto), %d mismatches, '
              '%d explained by ICU\'s override-of-PDI deviation' % (total, len(bad), len(icu_dev)))
        for s, d, order in bad[:15]:
            print('    MISMATCH dir=%s %r -> %s' % (d, s, order))
        for s, d, order in icu_dev[:3]:
            print('    ICU-DEVIATION dir=%s %r' % (d, s))
        failed = failed or bool(bad)
        n, bad2, worst = oracle_shaping(browser, rng, args.words, args.out, SAMPLES)
        print('[B] shaping: %d strings (11 mandatory samples + random words), %d differ (worst unmatched ink %.4f)'
              % (n, len(bad2), worst))
        for s, v, delta, frac, idx in bad2[:15]:
            print('    DIFF %r  size_delta=%s unmatched_ink=%.4f' % (s, delta, frac))
        failed = failed or bool(bad2)
        n3, bad3, worst3 = oracle_mixed(browser, rng, args.mixed, args.out)
        print('[C] end-to-end mixed strings (shape + bidi + mirror + isolates): %d strings, %d differ (worst %.4f)'
              % (n3, len(bad3), worst3))
        for s, delta, frac, idx in bad3[:15]:
            print('    DIFF %r  size_delta=%s unmatched_ink=%.4f' % (s, delta, frac))
        failed = failed or bool(bad3)
        if args.negative_control:
            print('[N] negative controls (the oracle must detect each deliberately broken engine):')
            for label, detected, nbad in negative_controls(browser, rng, args.out):
                print('    %-34s -> %s (%d differing strings)' % (label, 'DETECTED' if detected else 'NOT DETECTED', nbad))
                failed = failed or (not detected)
        browser.close()
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
