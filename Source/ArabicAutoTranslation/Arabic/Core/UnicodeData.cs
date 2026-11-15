using System;
using System.Collections.Generic;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>Unicode bidirectional character types (same order as Tools/ar_tables.py CLASSES).</summary>
    internal enum BidiClass : byte
    {
        L = 0, R = 1, AL = 2, EN = 3, ES = 4, ET = 5, AN = 6, CS = 7, NSM = 8, BN = 9, B = 10, S = 11, WS = 12, ON = 13,
        LRE = 14, LRO = 15, RLE = 16, RLO = 17, PDF = 18, LRI = 19, RLI = 20, FSI = 21, PDI = 22
    }

    /// <summary>Arabic joining types (ids match Tools/ar_tables.py JT).</summary>
    internal enum JoinType : byte
    {
        U = 0, D = 1, R = 2, L = 3, C = 4, T = 5
    }

    /// <summary>
    /// Read-only lookups over the generated <see cref="ArabicTables"/>. Everything is table driven: the engine never
    /// asks the platform (Mono / .NET) for Unicode properties, so results are identical on every runtime.
    /// </summary>
    internal static class UnicodeData
    {
        private static readonly byte[] BmpClass = new byte[0x10000];          // 0 = L (default)
        private static readonly bool[] BmpMark = new bool[0x10000];
        private static readonly List<int> HighClass = new List<int>();         // triplets, code points >= 0x10000
        private static readonly List<int> HighMark = new List<int>();          // pairs, code points >= 0x10000
        private static readonly Dictionary<int, int> Mirror = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> OpenToClose = new Dictionary<int, int>();
        private static readonly HashSet<int> CanonCloseSet = new HashSet<int>();
        private static readonly Dictionary<int, JoinType> Joins = new Dictionary<int, JoinType>();
        private static readonly Dictionary<int, int[]> Forms = new Dictionary<int, int[]>();       // base -> iso, fin, ini, med
        private static readonly Dictionary<int, int[]> LamAlefForms = new Dictionary<int, int[]>(); // alef -> iso, fin
        private static readonly Dictionary<int, int> UnshapeMap = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> UnshapeLamMap = new Dictionary<int, int>();

        internal const int Lam = 0x0644;

        static UnicodeData()
        {
            int[] r = ArabicTables.BidiRanges;
            for (int i = 0; i + 2 < r.Length; i += 3)
            {
                int first = r[i];
                int last = r[i + 1];
                byte cls = (byte)r[i + 2];
                int bmpLast = Math.Min(last, 0xFFFF);
                for (int cp = first; cp <= bmpLast; cp++)
                {
                    BmpClass[cp] = cls;
                }
                if (last >= 0x10000)
                {
                    HighClass.Add(Math.Max(first, 0x10000));
                    HighClass.Add(last);
                    HighClass.Add(cls);
                }
            }

            int[] m = ArabicTables.MarkRanges;
            for (int i = 0; i + 1 < m.Length; i += 2)
            {
                int first = m[i];
                int last = m[i + 1];
                int bmpLast = Math.Min(last, 0xFFFF);
                for (int cp = first; cp <= bmpLast; cp++)
                {
                    BmpMark[cp] = true;
                }
                if (last >= 0x10000)
                {
                    HighMark.Add(Math.Max(first, 0x10000));
                    HighMark.Add(last);
                }
            }

            int[] mp = ArabicTables.MirrorPairs;
            for (int i = 0; i + 1 < mp.Length; i += 2)
            {
                Mirror[mp[i]] = mp[i + 1];
            }

            int[] bp = ArabicTables.BracketPairs;
            for (int i = 0; i + 1 < bp.Length; i += 2)
            {
                OpenToClose[bp[i]] = bp[i + 1];
                CanonCloseSet.Add(CanonBracket(bp[i + 1]));
            }

            int[] jt = ArabicTables.JoinTable;
            for (int i = 0; i + 1 < jt.Length; i += 2)
            {
                Joins[jt[i]] = (JoinType)jt[i + 1];
            }

            int[] fr = ArabicTables.FormRows;
            for (int i = 0; i + 4 < fr.Length; i += 5)
            {
                Forms[fr[i]] = new int[] { fr[i + 1], fr[i + 2], fr[i + 3], fr[i + 4] };
            }

            int[] la = ArabicTables.LamAlefRows;
            for (int i = 0; i + 2 < la.Length; i += 3)
            {
                LamAlefForms[la[i]] = new int[] { la[i + 1], la[i + 2] };
            }

            int[] us = ArabicTables.UnshapePairs;
            for (int i = 0; i + 1 < us.Length; i += 2)
            {
                UnshapeMap[us[i]] = us[i + 1];
            }

            int[] ul = ArabicTables.UnshapeLamAlef;
            for (int i = 0; i + 1 < ul.Length; i += 2)
            {
                UnshapeLamMap[ul[i]] = ul[i + 1];
            }
        }

        /// <summary>Bidi class of a code point (default L).</summary>
        internal static BidiClass ClassOf(int cp)
        {
            if (cp >= 0 && cp < 0x10000)
            {
                return (BidiClass)BmpClass[cp];
            }
            for (int i = 0; i + 2 < HighClass.Count; i += 3)
            {
                if (cp >= HighClass[i] && cp <= HighClass[i + 1])
                {
                    return (BidiClass)HighClass[i + 2];
                }
            }
            return BidiClass.L;
        }

        /// <summary>True for Mn / Me code points: they stay attached (base first) to the preceding character.</summary>
        internal static bool IsMark(int cp)
        {
            if (cp >= 0 && cp < 0x10000)
            {
                return BmpMark[cp];
            }
            for (int i = 0; i + 1 < HighMark.Count; i += 2)
            {
                if (cp >= HighMark[i] && cp <= HighMark[i + 1])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True for strong right-to-left classes (R, AL).</summary>
        internal static bool IsStrongRtl(int cp)
        {
            BidiClass c = ClassOf(cp);
            return c == BidiClass.R || c == BidiClass.AL;
        }

        /// <summary>Letters, digits and other word-like characters (strong or numeric bidi classes).</summary>
        internal static bool IsWordLike(int cp)
        {
            BidiClass c = ClassOf(cp);
            return c == BidiClass.L || c == BidiClass.R || c == BidiClass.AL || c == BidiClass.EN || c == BidiClass.AN;
        }

        /// <summary>Mirrored counterpart (L4) or the code point itself.</summary>
        internal static int MirrorOf(int cp)
        {
            int v;
            return Mirror.TryGetValue(cp, out v) ? v : cp;
        }

        /// <summary>Canonical form of a paired bracket (U+2329/232A are equivalent to U+3008/3009).</summary>
        internal static int CanonBracket(int cp)
        {
            if (cp == 0x2329) return 0x3008;
            if (cp == 0x232A) return 0x3009;
            return cp;
        }

        /// <summary>If cp is an opening paired bracket, returns its canonical closing bracket.</summary>
        internal static bool TryGetClosingBracket(int cp, out int canonClose)
        {
            int close;
            if (OpenToClose.TryGetValue(cp, out close))
            {
                canonClose = CanonBracket(close);
                return true;
            }
            canonClose = 0;
            return false;
        }

        internal static bool IsClosingBracket(int canonCp)
        {
            return CanonCloseSet.Contains(canonCp);
        }

        /// <summary>Arabic joining type of a code point (U when unknown).</summary>
        internal static JoinType JoinTypeOf(int cp)
        {
            JoinType t;
            return Joins.TryGetValue(cp, out t) ? t : JoinType.U;
        }

        /// <summary>Presentation forms of a base letter: iso, fin, ini, med (0 = missing), or null.</summary>
        internal static int[] FormsOf(int baseCp)
        {
            int[] f;
            return Forms.TryGetValue(baseCp, out f) ? f : null;
        }

        /// <summary>Lam-alef ligature forms (iso, fin) for an alef, or null.</summary>
        internal static int[] LamAlefOf(int alef)
        {
            int[] f;
            return LamAlefForms.TryGetValue(alef, out f) ? f : null;
        }

        /// <summary>Base letter of a single-letter presentation form (or the code point itself when it is not one).</summary>
        internal static bool TryUnshape(int cp, out int baseCp)
        {
            return UnshapeMap.TryGetValue(cp, out baseCp);
        }

        /// <summary>Alef of a LAM + ALEF ligature presentation form.</summary>
        internal static bool TryUnshapeLamAlef(int cp, out int alef)
        {
            return UnshapeLamMap.TryGetValue(cp, out alef);
        }
    }
}
