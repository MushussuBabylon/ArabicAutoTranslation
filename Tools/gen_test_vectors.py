#!/usr/bin/env python3
"""
Generates Source/ArabicAutoTranslation.Tests/GoldenVectors.cs from the Python reference engine (Tools/arabic_ref.py).

The C# engine (Source/ArabicAutoTranslation/Arabic/Core) is a line-by-line port of the reference, and the reference is the
part that was validated against Chromium / ICU / FriBidi (Tools/oracle_*.py).  The vectors therefore pin the C# port
to the validated implementation: display strings (three option sets), UAX #9 levels, visual order, and FNV-1a values.

    python3 Tools/gen_test_vectors.py            (rewrites GoldenVectors.cs; deterministic)

The output is pure ASCII (every non-ASCII character is written as a \\uXXXX escape).
"""
import io
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True

import arabic_ref as R   # noqa: E402

OUT = os.path.join(HERE, '..', 'Source', 'ArabicAutoTranslation.Tests', 'GoldenVectors.cs')
SEED = 20261103

BS = chr(92)


def cs(s):
    """C# string literal (ASCII only)."""
    out = ['"']
    for ch in s:
        o = ord(ch)
        if ch == '"':
            out.append(BS + '"')
        elif ch == BS:
            out.append(BS + BS)
        elif 0x20 <= o <= 0x7E:
            out.append(ch)
        elif o > 0xFFFF:
            o -= 0x10000
            out.append(BS + 'u%04X' % (0xD800 + (o >> 10)))
            out.append(BS + 'u%04X' % (0xDC00 + (o & 0x3FF)))
        else:
            out.append(BS + 'u%04X' % o)
    out.append('"')
    return ''.join(out)


def ints(values):
    return 'new int[] { ' + ', '.join(str(v) for v in values) + ' }' if values else 'new int[0]'


# ------------------------------------------------------------------------------------------------
# display vectors
# ------------------------------------------------------------------------------------------------
MANDATORY = [
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

CURATED = [
    "مرحبا", "RimWorld", "", " ", "   مرحبا   ", "؟", "،", "؛", "...", "…",
    "(مرحبا) بك", "[مرحبا] بك", "((مرحبا))", "مرحبا (Hello) بك", "«مرحبا»", "(Ready?) هل أنت مستعد؟",
    "لا", "الله", "لأ", "لإ", "لآ", "ولا", "لا يوجد", "كلا ولا",
    "مُحَمَّد", "كِتَابٌ", "اللّٰه", "كتـاب", "مسؤول", "شيء", "جزء", "رئيس", "إنسان", "أحمد", "آمن", "ئ", "ؤ", "ء",
    "عام 2024 م", "10-20", "+5", "-3.5", "1,000", "50%", "٣٠", "٣٠٪", "$100", "100$", "€5",
    "25°C", "98.6°F", "12:30 م", "2024/11/03", "£ 50", "x2", "2x", "١٢٣ 456",
    "و/أو", "٣/٤", "A/B test اختبار", "C# و C++", "iPhone 15 Pro مع الشاحن",
    "\"Hello\" قال", "'مرحبا' قال", "قال: \"مرحبا\"", "example.com/path هو الموقع", "user@example.com هو البريد",
    "<b>مرحبا</b> بك", "<color=#ff0000>تحذير</color>: النص", "<size=20>كبير</size> وصغير", "<i>مائل</i> و <b>غامق</b>",
    "<b><i>مرحبا</i></b>", "<b>مرحبا", "مرحبا</b>", "<b>Hello</b> مرحبا", "<color=#00ff00>Done</color> تم", "<b>تم بناء {0}</b>",
    "{0}", "{PAWN_nameDef}", "[PAWN_nameDef] فقد [PAWN_possessive] سلاحه", "<Prohis>", "{0} و {1}", "{0}ـ و {1}",
    "كتابـ{PAWN_possessive}", "قوتـ[PAWN_possessive] كبيرة", "عليـ<Prohis>",
    "السطر الأول\nالسطر الثاني", "line1" + BS + "nمرحبا", "السطر الأول\r\nالسطر الثاني", "مرحبا\n\nبك", "\nمرحبا", "مرحبا\n",
    "<b>السطر الأول\nالسطر الثاني</b>", "السطر الأول" + BS + "nالسطر الثاني",
    "(مرحبا\nبك)", "<color=#ff0000>أحمر\nأحمر</color> عادي",
    "هذا نص طويل جدا يحتوي على كلمات كثيرة ليتم اختبار المعالجة على فقرة طويلة تتجاوز مائتي حرف بسهولة. " * 3,
    "اختبار 123 ثم اختبار 456 ثم Test 789 ثم نهاية",
    "ثم -5 ثم +7 ثم 8-9 ثم 10%",
    "نعم (yes) أو لا (no) أو ربما (maybe)",
    "[مرحبا](Hello)", "{مرحبا}", "<مرحبا>", "a < b و c > d",
    "ABC", "abc 123", "123", "-123", "(123)", "1.5 + 2.5 = 4",
    "ـ", "ـــ", "ـمرحبا", "مرحباـ",
    "ً", "اً", "بَّ",
    "الساعة 12:30:45 مساء", "من 1 إلى 10", "المستوى 5/10",
    "RimWorld: لعبة", "لعبة: RimWorld", "(RimWorld) لعبة", "لعبة (RimWorld)",
    "Hello, world! مرحبا بالعالم.", "مرحبا بالعالم. Hello, world!",
]

ARABIC_LETTERS = "ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآؤئةىء"
LATIN_WORDS = ["RimWorld", "Settings", "Mods", "OK", "Hello", "x", "ID", "Graphics", "A", "iPhone"]
PUNCT = [" ", " ", " ", ".", ",", "!", "?", ":", ";", "؟", "،", "-", "+", "%", "/", "(", ")", "[", "]"]
DIGITS = ["0", "1", "5", "25", "100", "2024", "٣", "١٢"]
ATOMS = ["{0}", "{1}", "{PAWN_nameDef}", "[PAWN_possessive]", "<b>", "</b>", "<i>", "</i>", "<color=#ff0000>", "</color>",
         "<size=20>", "</size>", "\n", BS + "n", "<Prohis>"]


def random_arabic_word(rng):
    n = rng.randint(1, 6)
    w = ''.join(rng.choice(ARABIC_LETTERS) for _ in range(n))
    if rng.random() < 0.12:
        w += rng.choice("ًَُِّْ")
    if rng.random() < 0.05:
        w = w[:1] + "ـ" + w[1:]
    if rng.random() < 0.06:
        w = "لا" + w
    return w


def random_display_string(rng):
    parts = []
    for _ in range(rng.randint(2, 10)):
        r = rng.random()
        if r < 0.45:
            parts.append(random_arabic_word(rng))
        elif r < 0.60:
            parts.append(rng.choice(LATIN_WORDS))
        elif r < 0.72:
            parts.append(rng.choice(DIGITS))
        elif r < 0.84:
            parts.append(rng.choice(ATOMS))
        else:
            parts.append(rng.choice(PUNCT))
        if rng.random() < 0.6:
            parts.append(" ")
    return ''.join(parts)


def display_vectors():
    rng = random.Random(SEED)
    items = [("mandatory %02d" % (i + 1), s) for i, s in enumerate(MANDATORY)]
    items += [("curated %03d" % i, s) for i, s in enumerate(CURATED)]
    items += [("random %03d" % i, random_display_string(rng)) for i in range(220)]
    full = R.DisplayOptions(shape=True, reorder=True, ltr_isolates=True)
    noiso = R.DisplayOptions(shape=True, reorder=True, ltr_isolates=False)
    shaped = R.DisplayOptions(shape=True, reorder=False, ltr_isolates=True)
    out = []
    for name, s in items:
        out.append((name, s, R.process_display(s, full), R.process_display(s, noiso), R.process_display(s, shaped)))
    return out


# ------------------------------------------------------------------------------------------------
# bidi vectors
# ------------------------------------------------------------------------------------------------
PALETTE = {
    'L': [0x61, 0x62, 0x5A, 0xE9, 0xDF, 0x4D],
    'R': [0x5D0, 0x5D1, 0x5D2, 0x5D3, 0x5D4],
    'AL': [0x627, 0x628, 0x62A, 0x644, 0x645, 0x646, 0x647, 0x648, 0x64A, 0x6A9],
    'EN': [0x30, 0x31, 0x32, 0x35, 0x39, 0x6F0, 0xB2],
    'AN': [0x660, 0x661, 0x669, 0x66B, 0x66C],
    'ES': [0x2B, 0x2D],
    'ET': [0x23, 0x24, 0x25, 0xB0, 0x20AC],
    'CS': [0x2C, 0x2E, 0x2F, 0x3A],
    'NSM': [0x64B, 0x64E, 0x651, 0x300, 0x5B0],
    'BN': [0x200B, 0x200C, 0x200D, 0xAD],
    'S': [0x09],
    'WS': [0x20, 0x20, 0x20, 0x2003],
    'ON': [0x21, 0x22, 0x26, 0x2A, 0x3B, 0x3D, 0x3F, 0x40, 0x5C, 0x5E, 0x5F, 0x7C, 0x7E],
    'BR': [0x28, 0x29, 0x5B, 0x5D, 0x7B, 0x7D, 0xAB, 0xBB, 0x2329, 0x232A, 0x3008, 0x3009],
    'MARK': [0x200E, 0x200F, 0x61C],
    'EMB': [0x202A, 0x202B, 0x202C, 0x202D, 0x202E],
    'ISO': [0x2066, 0x2067, 0x2068, 0x2069],
}
WEIGHTS = [('L', 6), ('R', 4), ('AL', 8), ('EN', 5), ('AN', 3), ('ES', 2), ('ET', 2), ('CS', 3), ('NSM', 2), ('BN', 1),
           ('S', 1), ('WS', 6), ('ON', 3), ('BR', 8), ('MARK', 1), ('EMB', 2), ('ISO', 4)]

BRACKET_CASES = [
    "a(b)c", "a(b)ג", "ג(a)ג", "(ab)ג", "ג(ab)", "אב(גד)הו", "a(ב)c",
    "(אב)", "a[ב]c", "a{ב}c", "a(b[ג]d)e", "ג(a[b]c)ד", "(((", ")))", "(]", "a(b]c",
    "ב(א)(ב)א", "א〈ב〉ג", "a〈b〉c", "a(1)ב", "א(1)ב", "א(a1)ב",
    "ب(ا)ب", "ا(a)ا", "ב(12)א", "a(12)ב", "a(⁧ב)⁩c", "א(a⁦b)⁩ב",
    "ab(cd)אב", "אב(cd)ab", "(a)(ב)(c)", "א[ב(ג)ד]ה", "a[b(ג)d]e", "(א a ב)",
    "اب(abc)تث", "اب (abc) تث", "اب (12) تث", "a ( א ) b",
    "«x» א", "א «x»", "א«ב»", "א (ב) (ג) a",
    "a(ב)", "(ב)a", "a(בג)ד(e)", "x(א)ב(y)",
]


def random_bidi_string(rng, maxlen):
    names = [n for n, _ in WEIGHTS]
    weights = [w for _, w in WEIGHTS]
    n = rng.randint(1, maxlen)
    cps = []
    for _ in range(n):
        cls = rng.choices(names, weights)[0]
        cps.append(rng.choice(PALETTE[cls]))
    return ''.join(chr(c) for c in cps)


def bidi_texts():
    rng = random.Random(SEED + 1)
    texts = []
    for s in BRACKET_CASES:
        for para in (-1, 0, 1):
            texts.append((s, para))
    for i in range(420):
        texts.append((random_bidi_string(rng, 14), (-1, 0, 1)[i % 3]))
    return texts


def level_vectors():
    out = []
    for s, para in bidi_texts():
        cps = R.to_cps(s)
        levels, keep, par = R.resolve_levels(cps, None if para < 0 else para)
        out.append((s, para, list(keep), [levels[i] for i in keep], par))
    return out


def order_vectors():
    out = []
    for s, para in bidi_texts():
        cps = R.to_cps(s)
        visual, levels, par = R.reorder(cps, None if para < 0 else para)
        out.append((s, para, [i for i, _ in visual], [cp for _, cp in visual], par))
    return out


# ------------------------------------------------------------------------------------------------
# shaping vectors (ArabicShaper.Shape directly)
# ------------------------------------------------------------------------------------------------
SHAPE_LETTERS = "ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآؤئةىءٱپچژگکیڤ"
SHAPE_WORDS = [
    "", "ا", "ب", "بيت", "لا", "بلا", "ادر", "ء", "بَ", "لأ", "لإ", "لآ", "ولا", "كلا", "الله", "كتاب", "مرحبا", "مُحَمَّد",
    "كتـاب", "ـ", "بـ", "ـب", "ب ب", "بZب", "ب1ب", "ب٣ب", "ب.ب", "abc بيت 123", "RimWorld", "123", "سلام عليكم",
    "لَا", "لـا", "بـلا", "ال", "أَلَا", "لٱ", "ؤ", "بؤ", "ؤب", "ئ", "بئ", "ئب", "ةب", "بة", "ىب", "بى", "ببب", "بببب",
    "شيء", "جزء", "رئيس", "إنسان", "مسؤول", "ھھ", "ہہ", "ڤڤ", "پپپ", "چچ", "کک", "گگ", "یی",
    "ب\u200cب", "ب\u200dب", "ب\u2066ب", "لا\u064Eلا",
]


def shape_vectors():
    items = list(SHAPE_WORDS)
    for ch in SHAPE_LETTERS:
        items += [ch, ch + "ب", "ب" + ch, "ب" + ch + "ب", ch + "ا", "ا" + ch]
    seen = set()
    out = []
    for text in items:
        if text in seen:
            continue
        seen.add(text)
        cps = R.to_cps(text)
        shaped, owners = R.shape(cps)
        out.append((text, shaped, [o[0] for o in owners]))
    return out


# ------------------------------------------------------------------------------------------------
# fnv
# ------------------------------------------------------------------------------------------------
def fnv(data):
    h = 14695981039346656037
    for b in data:
        h ^= b
        h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h


def fnv_of(s):
    return fnv(s.encode('utf-16-le'))


FNV_STRINGS = ["", "a", "foobar", "RimWorld", "مرحبا", "مستعمر|مستعمرين", "The colonist is hungry.", "ـ", "{GL0}"]


# ------------------------------------------------------------------------------------------------
def main():
    disp = display_vectors()
    shp = shape_vectors()
    lev = level_vectors()
    order = order_vectors()

    w = []
    w.append('// <auto-generated>')
    w.append('// Generated by Tools/gen_test_vectors.py from the Python reference engine (Tools/arabic_ref.py). DO NOT EDIT BY HAND.')
    w.append('// Re-run the generator after changing the reference engine or the vector lists.')
    w.append('// </auto-generated>')
    w.append('namespace ArabicAutoTranslation.Tests')
    w.append('{')
    w.append('    internal sealed class DisplayVector')
    w.append('    {')
    w.append('        internal readonly string Name;')
    w.append('        internal readonly string Logical;')
    w.append('        /// <summary>Shaped + reordered, LTR isolates on (the default display mode).</summary>')
    w.append('        internal readonly string Full;')
    w.append('        /// <summary>Shaped + reordered, LTR isolates off.</summary>')
    w.append('        internal readonly string NoIsolates;')
    w.append('        /// <summary>Shaped only, typing order (render mode 1).</summary>')
    w.append('        internal readonly string ShapedOnly;')
    w.append('        internal DisplayVector(string name, string logical, string full, string noIsolates, string shapedOnly)')
    w.append('        {')
    w.append('            Name = name; Logical = logical; Full = full; NoIsolates = noIsolates; ShapedOnly = shapedOnly;')
    w.append('        }')
    w.append('    }')
    w.append('')
    w.append('    internal sealed class LevelVector')
    w.append('    {')
    w.append('        internal readonly string Text;')
    w.append('        /// <summary>-1 = detect (P2/P3), 0 or 1.</summary>')
    w.append('        internal readonly int Para;')
    w.append('        internal readonly int[] Keep;')
    w.append('        internal readonly int[] Levels;')
    w.append('        internal readonly int Paragraph;')
    w.append('        internal LevelVector(string text, int para, int[] keep, int[] levels, int paragraph)')
    w.append('        {')
    w.append('            Text = text; Para = para; Keep = keep; Levels = levels; Paragraph = paragraph;')
    w.append('        }')
    w.append('    }')
    w.append('')
    w.append('    internal sealed class OrderVector')
    w.append('    {')
    w.append('        internal readonly string Text;')
    w.append('        internal readonly int Para;')
    w.append('        internal readonly int[] Index;')
    w.append('        internal readonly int[] CodePoints;')
    w.append('        internal readonly int Paragraph;')
    w.append('        internal OrderVector(string text, int para, int[] index, int[] codePoints, int paragraph)')
    w.append('        {')
    w.append('            Text = text; Para = para; Index = index; CodePoints = codePoints; Paragraph = paragraph;')
    w.append('        }')
    w.append('    }')
    w.append('')
    w.append('    internal sealed class ShapeVector')
    w.append('    {')
    w.append('        internal readonly string Logical;')
    w.append('        internal readonly int[] Shaped;')
    w.append('        /// <summary>Index in the input of the first source character of every output character.</summary>')
    w.append('        internal readonly int[] Owners;')
    w.append('        internal ShapeVector(string logical, int[] shaped, int[] owners)')
    w.append('        {')
    w.append('            Logical = logical; Shaped = shaped; Owners = owners;')
    w.append('        }')
    w.append('    }')
    w.append('')
    w.append('    internal static class GoldenVectors')
    w.append('    {')
    w.append('        internal static readonly DisplayVector[] Display = new DisplayVector[]')
    w.append('        {')
    for name, logical, full, noiso, shaped in disp:
        w.append('            new DisplayVector(%s, %s, %s, %s, %s),' % (cs(name), cs(logical), cs(full), cs(noiso), cs(shaped)))
    w.append('        };')
    w.append('')
    w.append('        /// <summary>The ten mandatory mixed-text samples are the first ten display vectors.</summary>')
    w.append('        internal const int MandatoryCount = %d;' % len(MANDATORY))
    w.append('')
    w.append('        internal static readonly ShapeVector[] Shaping = new ShapeVector[]')
    w.append('        {')
    for logical, shaped, owners in shp:
        w.append('            new ShapeVector(%s, %s, %s),' % (cs(logical), ints(shaped), ints(owners)))
    w.append('        };')
    w.append('')
    w.append('        internal static readonly LevelVector[] Levels = new LevelVector[]')
    w.append('        {')
    for s, para, keep, levels, par in lev:
        w.append('            new LevelVector(%s, %d, %s, %s, %d),' % (cs(s), para, ints(keep), ints(levels), par))
    w.append('        };')
    w.append('')
    w.append('        internal static readonly OrderVector[] Order = new OrderVector[]')
    w.append('        {')
    for s, para, idx, cps, par in order:
        w.append('            new OrderVector(%s, %d, %s, %s, %d),' % (cs(s), para, ints(idx), ints(cps), par))
    w.append('        };')
    w.append('')
    w.append('        /// <summary>FNV-1a (64 bit) over the UTF-16 code units, low byte first: text, expected hex.</summary>')
    w.append('        internal static readonly string[][] Fnv = new string[][]')
    w.append('        {')
    for s in FNV_STRINGS:
        w.append('            new string[] { %s, "%016x" },' % (cs(s), fnv_of(s)))
    w.append('        };')
    w.append('')
    w.append('        /// <summary>AddU64 over the value 0x0123456789ABCDEF starting from the empty hash: expected hex.</summary>')
    w.append('        internal const string FnvU64 = "%016x";' % fnv(bytes(bytearray([0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01]))))
    w.append('    }')
    w.append('}')
    w.append('')

    text = '\n'.join(w)
    for ch in text:
        if ord(ch) > 0x7E and ch != '\n':
            raise SystemExit('non-ASCII character in output: U+%04X' % ord(ch))
    with io.open(OUT, 'w', encoding='ascii', newline='\n') as f:
        f.write(text)
    print('wrote %s: %d display, %d shaping, %d level, %d order vectors, %d bytes' % (os.path.normpath(OUT), len(disp), len(shp), len(lev), len(order), len(text)))


if __name__ == '__main__':
    main()
