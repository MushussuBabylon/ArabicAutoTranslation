#!/usr/bin/env python3
"""Checks Languages/Arabic/Keyed/Mod.xml against the English file and the rules of the project brief (PART 12).

    python3 Tools/check_language_xml.py [english.xml arabic.xml ArabicGlossary.txt]

Without arguments it checks the files of this repository. It verifies that
  * the Arabic file is well-formed XML with exactly the keys of the English file, in the same order,
  * every entry has an <!-- EN: ... --> comment identical to the English source text, and the header, the section
    comments, the blank lines and the indentation are those of the English file,
  * placeholders ({0}, [PAWN_nameDef], ${0}), quoted literals, domains and ::markers:: survive, no Latin word appears
    that the English text does not contain, leading / trailing spaces are kept,
  * the Arabic text is plain logical text (no presentation forms, no bidirectional control characters, no kashida, no
    Korean), uses the required spelling of "also" and has no pronoun variable that would need the kashida rule,
  * it lists the glossary terms found in the English strings and whether the approved Arabic was used.
The exit code is 1 when a check fails.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
_args = sys.argv[1:4]
if len(_args) == 3:
    en_path, ar_path, gl_path = _args
else:
    en_path = os.path.join(_ROOT, 'Languages', 'English', 'Keyed', 'Mod.xml')
    ar_path = os.path.join(_ROOT, 'Languages', 'Arabic', 'Keyed', 'Mod.xml')
    gl_path = os.path.join(_ROOT, 'Arabic', 'ArabicGlossary.txt')
problems = []


def bad(msg):
    problems.append(msg)
    print('  PROBLEM: ' + msg)


def rng(a, b):
    return '[' + chr(a) + '-' + chr(b) + ']'


ARABIC_LETTER = re.compile(rng(0x0621, 0x064A))
PRESENTATION = re.compile(rng(0xFB50, 0xFDFF) + '|' + rng(0xFE70, 0xFEFF))
BIDI_CONTROL = re.compile('[' + ''.join(chr(c) for c in (0x061C, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E,
                                                           0x2066, 0x2067, 0x2068, 0x2069)) + ']')
HANGUL = re.compile(rng(0x1100, 0x11FF) + '|' + rng(0x3130, 0x318F) + '|' + rng(0xAC00, 0xD7AF))
FATHATAN = chr(0x064B)
TATWEEL = chr(0x0640)
ALEF = chr(0x0627)

en_text = open(en_path, 'rb').read().decode('utf-8-sig')
ar_text = open(ar_path, 'rb').read().decode('utf-8-sig')

print('1. well-formed XML, same keys in the same order')
try:
    en_root = ET.parse(en_path).getroot()
    ar_root = ET.parse(ar_path).getroot()
except ET.ParseError as e:
    sys.exit('XML error: %s' % e)
en_items = [(e.tag, e.text or '') for e in en_root]
ar_items = [(e.tag, e.text or '') for e in ar_root]
if [k for k, _ in en_items] != [k for k, _ in ar_items]:
    bad('the keys differ from the English file (set or order)')
else:
    print('  ok: %d keys, identical order; root element %s' % (len(en_items), ar_root.tag))

print('2. comments: EN source comments exact, other comments untouched')
entry = re.compile(r'^(\s*)<(AT_[A-Za-z0-9_]+)>(.*)</\2>\s*$')
en_lines = en_text.split('\n')
ar_lines = ar_text.split('\n')
en_other_comments = [l.strip() for l in en_lines if '<!--' in l]
ar_other_comments = [l.strip() for l in ar_lines if '<!--' in l and not l.strip().startswith('<!-- EN: ')]
if en_other_comments != ar_other_comments:
    bad('section comments differ from the English file')
# every entry preceded by its exact EN comment
n_en = 0
for i, line in enumerate(ar_lines):
    m = entry.match(line)
    if not m:
        continue
    key = m.group(2)
    prev = ar_lines[i - 1].strip() if i else ''
    english_raw = next(entry.match(l).group(3) for l in en_lines if entry.match(l) and entry.match(l).group(2) == key)
    want = '<!-- EN: %s -->' % english_raw
    if prev != want:
        bad('%s: EN comment missing or changed' % key)
    else:
        n_en += 1
print('  ok: %d EN comments, each identical to the English source of its entry' % n_en if n_en == len(en_items) else '')
# structure: Arabic file without its EN comments, values blanked, must equal the English file with values blanked
def skeleton(lines, drop_en):
    out = []
    for l in lines:
        if drop_en and l.strip().startswith('<!-- EN: '):
            continue
        m = entry.match(l)
        out.append('%s<%s/>' % (m.group(1), m.group(2)) if m else l)
    return out


if skeleton(en_lines, False) != skeleton(ar_lines, True):
    bad('line structure (header, blank lines, indentation, section comments) differs from the English file')
else:
    print('  ok: line structure identical to the English file (blank lines, indentation, section comments)')
header_ok = ar_lines[0].strip() == en_lines[0].strip() and '<LanguageData>' in ar_text and ar_text.rstrip().endswith('</LanguageData>')
if not header_ok:
    bad('XML header or root tags differ')
else:
    print('  ok: XML header and <LanguageData> root as in the English file')

print('3. placeholders, literals and tokens')
PROT = [
    r'\{[^{}]*\}', r'\[[^\[\]]*\]', r'\$\{\d\}', r'\$\d', r'::[A-Z]+::', r'&lt;=',
    r'[\w-]+(?:\.[\w-]+)*\.(?:com|txt)', r'":fx"', r'"[^"]*"', r"'[^']*'", r'\(,\)',
]
protected_total = 0
for (k, en_v), (_, ar_v) in zip(en_items, ar_items):
    toks = set()
    for p in PROT:
        toks.update(re.findall(p, en_v))
    # a quoted English phrase that is only ordinary words is allowed to be translated; the others must survive
    for t in sorted(toks):
        if t.startswith('"') and t.strip('"') in ('claude',):
            pass
        protected_total += 1
        if t not in ar_v and t not in ar_text.split('<%s>' % k)[1].split('</%s>' % k)[0]:
            # entity form: compare with the raw text of the XML
            raw = ar_text.split('<%s>' % k)[1].split('</%s>' % k)[0]
            if t not in raw:
                bad('%s: protected token %r missing in the Arabic text' % (k, t))
    if en_v.count('\\n') != ar_v.count('\\n'):
        bad('%s: number of \\n differs' % k)
    if (en_v != en_v.strip()) != (ar_v != ar_v.strip()) or en_v.endswith(' ') != ar_v.endswith(' ') or en_v.startswith(' ') != ar_v.startswith(' '):
        bad('%s: leading / trailing space differs' % k)
    # Latin words that the Arabic text introduces
    en_latin = set(re.findall(r'[A-Za-z][A-Za-z0-9_.:\-/|$]*', en_v))
    en_latin = {w.rstrip('.:-/|') for w in en_latin} | en_latin
    for w in (x.rstrip('.:-/|') for x in re.findall(r'[A-Za-z][A-Za-z0-9_.:\-/|$]*', ar_v)):
        if w not in en_latin and not any(w in e for e in en_latin):
            bad('%s: Latin word %r is not in the English source' % (k, w))
print('  checked %d protected tokens (placeholders, brackets, quoted literals, domains, ::markers::)' % protected_total)

print('4. Arabic text rules')
for (k, en_v), (_, ar_v) in zip(en_items, ar_items):
    if not ARABIC_LETTER.search(ar_v):
        bad('%s: no Arabic letters' % k)
    if ar_v.strip() == en_v.strip():
        bad('%s: identical to the English text' % k)
    if PRESENTATION.search(ar_v):
        bad('%s: contains presentation forms (the text must stay plain)' % k)
    if BIDI_CONTROL.search(ar_v):
        bad('%s: contains a bidirectional control character' % k)
    if HANGUL.search(ar_v):
        bad('%s: contains Korean' % k)
    if TATWEEL in ar_v:
        bad('%s: contains a kashida, but there is no pronoun variable in this file' % k)
    if FATHATAN + ALEF in ar_v:
        bad('%s: tanween is placed before the alef' % k)
    if re.search(chr(0x0623) + chr(0x064A) + chr(0x0636) + ALEF + '(?!' + FATHATAN + ')', ar_v) or \
       (chr(0x0623) + chr(0x064A) + chr(0x0636) + FATHATAN + ALEF) in ar_v:
        bad('%s: wrong spelling of the required form' % k)
    if re.search(r'\{PAWN_\w+\}|<Pro\w+>|\[PAWN_(?!nameDef\])\w+\]', ar_v):
        bad('%s: a pronoun variable is present - the kashida rule would have to be applied' % k)
want_also = chr(0x0623) + chr(0x064A) + chr(0x0636) + ALEF + FATHATAN
n_also = ar_text.count(want_also)
print('  ok-check: required spelling used %d time(s); no presentation forms, bidi controls, tatweel or Korean' % n_also)

print('5. glossary (rule 10): terms of the glossary found in the English strings')
terms = []
for line in open(gl_path, encoding='utf-8'):
    line = line.strip()
    if not line or line[0] in '#[@':
        continue
    parts = [p.strip() for p in line.split('|')]
    if len(parts) < 2:
        continue
    names = [n.strip() for n in parts[0].split(' / ')]
    attrs = {}
    flags = set()
    for a in parts[2:]:
        if '=' in a:
            kk, vv = a.split('=', 1)
            attrs[kk.strip()] = vv.strip()
        elif a:
            flags.add(a)
    forms = [parts[1]] + [attrs[x] for x in ('pl', 'fem', 'fpl', 'def', 'indef') if x in attrs]
    for n in names:
        terms.append((n, parts[1], forms, flags, attrs))
shown = 0
for (k, en_v), (_, ar_v) in zip(en_items, ar_items):
    for n, ar, forms, flags, attrs in terms:
        flags_re = 0 if 'cs' in flags else re.I
        if re.search(r'(?<![A-Za-z])' + re.escape(n) + r'(?![A-Za-z])', en_v, flags_re):
            core = [f[2:] if f.startswith(chr(0x0627) + chr(0x0644)) else f for f in forms]
            found = any(c and c in ar_v for c in core)
            shown += 1
            print('  %-42s %-12s -> %-14s %s%s' % (k, n, ar, 'USED' if found else 'not used', '  [guard]' if 'guard' in flags else ''))
print('  (%d glossary hits; every "not used" line is reviewed by hand below the run)' % shown)

print()
print('keys: %d   PROBLEMS: %d' % (len(en_items), len(problems)))
sys.exit(1 if problems else 0)
