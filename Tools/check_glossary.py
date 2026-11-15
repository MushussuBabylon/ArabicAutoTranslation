#!/usr/bin/env python3
"""
Checks Arabic/ArabicGlossary.txt against the RimWorld Arabic Terminology Glossary the project was given.

    python3 Tools/check_glossary.py [path/to/RimWorld_Glossary.txt]

The reference copy ships next to the C# tests (Source/ArabicAutoTranslation.Tests/Data/RimWorld_Glossary.reference.txt),
which is the default. The check verifies that

  * every "English -> Arabic" pair of the reference glossary exists in the data file with exactly the same Arabic text
    (code point for code point), in the matching DLC section,
  * every plural / feminine remark of the reference ("( plural: ... )", "( feminine: ... , plural: ... )") exists as
    the pl= / fem= attribute of that entry,
  * the entries the master prompt lists in addition to the reference (Medicine, Ancient Stockpile, Cook, left/right
    arm and sensors, Fleshbeast alternative) exist,
  * the data file itself is well formed (every line parses, ids are unique, every attribute is known).

The same rules are checked by the C# test runner (Source/ArabicAutoTranslation.Tests), against the real C# parser.
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, '..')
DATA = os.path.join(ROOT, 'Arabic', 'ArabicGlossary.txt')
REF_DEFAULT = os.path.join(ROOT, 'Source', 'ArabicAutoTranslation.Tests', 'Data', 'RimWorld_Glossary.reference.txt')

HEADINGS = {
    'CORE / BASE GAME': 'Core',
    'ROYALTY': 'Royalty',
    'IDEOLOGY': 'Ideology',
    'BIOTECH': 'Biotech',
    'ANOMALY': 'Anomaly',
    'ODYSSEY': 'Odyssey',
    'STANDARDIZED TERMS (ADDED IN THE TERMINOLOGY REVIEW)': 'Standardized',
}
KNOWN_ATTRS = {'mode', 'cs', 'adj', 'guard', 'pl', 'fem', 'fpl', 'def', 'indef', 'enpl', 'mod', 'key', 'id', 'derived',
               'note'}

SPLIT = {
    'left / right sight sensor': [('Left sight sensor', 'المستشعر البصري الأيسر'),
                                   ('Right sight sensor', 'المستشعر البصري الأيمن')],
    'left / right hearing sensor': [('Left hearing sensor', 'المستشعر السمعي الأيسر'),
                                     ('Right hearing sensor', 'المستشعر السمعي الأيمن')],
}

errors = []


def err(msg):
    errors.append(msg)


def parse_reference(path):
    """-> list of (section, [english aliases], arabic, remarks)"""
    out = []
    section = None
    for raw in io.open(path, encoding='utf-8').read().split('\n'):
        line = raw.strip()
        if not line:
            continue
        if line in HEADINGS:
            section = HEADINGS[line]
            continue
        if section is None or '→' not in line:
            continue
        left, right = [p.strip() for p in line.split('→', 1)]
        if left.lower() in SPLIT:
            for eng, ar in SPLIT[left.lower()]:
                out.append((section, [eng], ar, []))
            continue
        if left.lower() == 'arm':
            out.append((section, ['Arm'], 'الذراع', []))
            out.append((section, ['Left arm'], 'الذراع اليسرى', []))
            out.append((section, ['Right arm'], 'الذراع اليمنى', []))
            continue
        remarks = [r.strip() for r in re.findall(r'\(([^)]*)\)', right)]
        arabic = re.sub(r'\([^)]*\)', '', right).strip()
        out.append((section, [a.strip() for a in left.split(' / ')], arabic, remarks))
    return out


def parse_data(path):
    entries = []
    section = None
    seen_ids = set()
    version = None
    for n, raw in enumerate(io.open(path, encoding='utf-8').read().split('\n'), 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('@glossary-version'):
            version = line.split(None, 1)[1].strip()
            continue
        if line.startswith('['):
            if not line.endswith(']'):
                err('line %d: unterminated section header' % n)
                continue
            section = line[1:-1].split('|')[0].strip()
            continue
        if section is None:
            err('line %d: entry before any section' % n)
            continue
        fields = [f.strip() for f in line.split('|')]
        if len(fields) < 2 or not fields[0] or not fields[1]:
            err('line %d: entry needs "English | Arabic"' % n)
            continue
        attrs = {}
        for f in fields[2:]:
            k, _, v = f.partition('=')
            k = k.strip()
            if k not in KNOWN_ATTRS:
                err('line %d: unknown attribute "%s"' % (n, k))
            attrs[k] = v.strip()
        eng = [a.strip() for a in fields[0].split(' / ')]
        entries.append(dict(section=section, english=eng, arabic=fields[1], attrs=attrs, line=n))
        if 'id' in attrs:
            if attrs['id'] in seen_ids:
                err('line %d: duplicate id %s' % (n, attrs['id']))
            seen_ids.add(attrs['id'])
    if version is None:
        err('missing @glossary-version')
    return entries


def find(entries, section, english, arabic=None):
    el = english.lower()
    res = []
    for e in entries:
        if e['section'] != section:
            continue
        if any(a.lower() == el for a in e['english']) and (arabic is None or e['arabic'] == arabic):
            res.append(e)
    return res


def main():
    ref_path = sys.argv[1] if len(sys.argv) > 1 else REF_DEFAULT
    ref = parse_reference(ref_path)
    data = parse_data(DATA)
    pairs = 0
    for section, aliases, arabic, remarks in ref:
        for eng in aliases:
            pairs += 1
            hits = find(data, section, eng, arabic)
            if not hits:
                err('MISSING [%s] %s -> %s' % (section, eng, arabic))
                continue
            for r in remarks:
                m = re.match(r'^plural:\s*(.+)$', r)
                mf = re.match(r'^feminine:\s*([^,]+?)\s*(?:,\s*plural:\s*(.+))?$', r)
                if mf:
                    if not any(h['attrs'].get('fem') == mf.group(1).strip() for h in hits):
                        err('MISSING fem=%s on [%s] %s' % (mf.group(1).strip(), section, eng))
                    if mf.group(2) and not any(h['attrs'].get('pl') == mf.group(2).strip() for h in hits):
                        err('MISSING pl=%s on [%s] %s' % (mf.group(2).strip(), section, eng))
                elif m:
                    if not any(h['attrs'].get('pl') == m.group(1).strip() for h in hits):
                        err('MISSING pl=%s on [%s] %s' % (m.group(1).strip(), section, eng))
    extras = [
        ('Standardized', 'Medicine', 'دواء'),
        ('Standardized', 'Ancient Stockpile', 'مخزون'),
        ('Standardized', 'Cook', 'طباخ'),
        ('Standardized', 'Left arm', 'الذراع اليسرى'),
        ('Standardized', 'Right arm', 'الذراع اليمنى'),
        ('Standardized', 'Left sight sensor', 'المستشعر البصري الأيسر'),
        ('Standardized', 'Right sight sensor', 'المستشعر البصري الأيمن'),
        ('Standardized', 'Left hearing sensor', 'المستشعر السمعي الأيسر'),
        ('Standardized', 'Right hearing sensor', 'المستشعر السمعي الأيمن'),
        ('Standardized', 'Raider', 'غازي'),
        ('Ideology', 'Raider', 'الغزو'),
        ('Standardized', 'Investigate', 'تحقيق في'),
        ('Core', 'Spot', 'مكان'),
        ('Odyssey', 'Fishing Spot', 'موقع صيد السمك'),
        ('Standardized', 'Body', 'جسم'),
        ('Ideology', 'Flesh Purity', 'نقاء الجسد'),
        ('Ideology', 'Medicine Maker', 'صانع الأدوية'),
        ('Biotech', 'Tunneler', 'حفار'),
        ('Ideology', 'Tunneler', 'ناسك الأنفاق'),
    ]
    for section, eng, ar in extras:
        if not find(data, section, eng, ar):
            err('MISSING (master prompt) [%s] %s -> %s' % (section, eng, ar))
    fb = find(data, 'Anomaly', 'Fleshbeast', 'وحش اللحم')
    if not fb or fb[0]['attrs'].get('indef') != 'وحش لحم':
        err('Fleshbeast must carry indef=وحش لحم')

    print('reference pairs checked : %d' % pairs)
    print('data entries            : %d' % len(data))
    if errors:
        for e in errors:
            print('FAIL', e)
        print('%d problem(s)' % len(errors))
        return 1
    print('OK: every reference pair is present with identical Arabic text.')
    return 0


sys.exit(main())
