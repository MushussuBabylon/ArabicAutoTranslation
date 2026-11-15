using System;
using System.Collections.Generic;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Unicode Bidirectional Algorithm (UAX #9) for a single paragraph: rules P2-P3, X1-X10, W1-W7, N0-N2, I1-I2, L1
    /// and (with clusters) L2 / L4. Port of Tools/arabic_ref.py; every rule is written out explicitly, nothing is
    /// delegated to the platform. All indices are code point indices.
    /// </summary>
    internal static class Bidi
    {
        internal const int MaxDepth = 125;
        private const int MaxBracketStack = 63;

        private struct StackEntry
        {
            internal int Level;
            internal BidiClass Override;   // ON = neutral, otherwise L or R
            internal bool Isolate;
        }

        /// <summary>Outcome of <see cref="ResolveLevels"/>.</summary>
        internal sealed class Resolved
        {
            /// <summary>Resolved level of each input index (meaningful for the indices listed in <see cref="Keep"/>).</summary>
            internal int[] Levels;
            /// <summary>Indices that survive rule X9 (embedding controls and BN are removed), ascending.</summary>
            internal int[] Keep;
            /// <summary>Paragraph embedding level (0 or 1).</summary>
            internal int Paragraph;
        }

        private static bool IsIsolateInitiator(BidiClass c)
        {
            return c == BidiClass.LRI || c == BidiClass.RLI || c == BidiClass.FSI;
        }

        private static bool IsRemovedByX9(BidiClass c)
        {
            return c == BidiClass.RLE || c == BidiClass.LRE || c == BidiClass.RLO || c == BidiClass.LRO ||
                   c == BidiClass.PDF || c == BidiClass.BN;
        }

        private static bool IsNeutralOrIsolate(BidiClass c)
        {
            return c == BidiClass.B || c == BidiClass.S || c == BidiClass.WS || c == BidiClass.ON ||
                   c == BidiClass.FSI || c == BidiClass.LRI || c == BidiClass.RLI || c == BidiClass.PDI;
        }

        private static bool IsWhitespaceOrIsolateFormat(BidiClass c)
        {
            return c == BidiClass.WS || c == BidiClass.FSI || c == BidiClass.LRI || c == BidiClass.RLI ||
                   c == BidiClass.PDI;
        }

        /// <summary>Direction used by N0 / N1 for a resolved type: 0 = L, 1 = R (R, EN, AN), -1 = none.</summary>
        private static int DirOf(BidiClass t)
        {
            if (t == BidiClass.L) return 0;
            if (t == BidiClass.R || t == BidiClass.EN || t == BidiClass.AN) return 1;
            return -1;
        }

        /// <summary>P2 / P3 helper: 0 (L), 1 (R / AL) or -1 (none) for the text in [start, end), skipping isolates.</summary>
        private static int FirstStrongLevel(BidiClass[] types, int start, int end, int[] matchPdi)
        {
            int i = start;
            while (i < end)
            {
                BidiClass t = types[i];
                if (t == BidiClass.L) return 0;
                if (t == BidiClass.R || t == BidiClass.AL) return 1;
                if (IsIsolateInitiator(t))
                {
                    if (matchPdi[i] == -1) return -1;
                    i = matchPdi[i];
                }
                i++;
            }
            return -1;
        }

        /// <summary>
        /// Resolves embedding levels. <paramref name="paraLevel"/> is 0, 1 or -1 (detect with P2 / P3, default 0).
        /// The input must be one paragraph (callers split at paragraph separators).
        /// </summary>
        internal static Resolved ResolveLevels(int[] cps, int paraLevel)
        {
            int n = cps.Length;
            BidiClass[] orig = new BidiClass[n];
            BidiClass[] types = new BidiClass[n];
            for (int i = 0; i < n; i++)
            {
                orig[i] = UnicodeData.ClassOf(cps[i]);
                types[i] = orig[i];
            }

            // BD9: matching isolate initiators and PDIs
            int[] matchPdi = new int[n];
            int[] matchInit = new int[n];
            for (int i = 0; i < n; i++)
            {
                matchPdi[i] = -1;
                matchInit[i] = -1;
            }
            Stack<int> openIsolates = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (IsIsolateInitiator(orig[i]))
                {
                    openIsolates.Push(i);
                }
                else if (orig[i] == BidiClass.PDI && openIsolates.Count > 0)
                {
                    int j = openIsolates.Pop();
                    matchPdi[j] = i;
                    matchInit[i] = j;
                }
            }

            // P2 / P3
            int para;
            if (paraLevel < 0)
            {
                int fl = FirstStrongLevel(orig, 0, n, matchPdi);
                para = fl < 0 ? 0 : fl;
            }
            else
            {
                para = paraLevel;
            }

            // X1 - X8
            int[] levels = new int[n];
            for (int i = 0; i < n; i++)
            {
                levels[i] = para;
            }
            StackEntry[] stack = new StackEntry[MaxDepth + 4];
            int sp = 0;
            stack[0] = new StackEntry { Level = para, Override = BidiClass.ON, Isolate = false };
            int overflowIsolate = 0;
            int overflowEmbedding = 0;
            int validIsolate = 0;
            for (int i = 0; i < n; i++)
            {
                BidiClass t = orig[i];
                StackEntry top = stack[sp];
                if (t == BidiClass.RLE || t == BidiClass.LRE || t == BidiClass.RLO || t == BidiClass.LRO)
                {
                    levels[i] = top.Level;
                    int newLevel;
                    if (t == BidiClass.RLE || t == BidiClass.RLO)
                    {
                        newLevel = top.Level % 2 == 0 ? top.Level + 1 : top.Level + 2;
                    }
                    else
                    {
                        newLevel = top.Level % 2 == 0 ? top.Level + 2 : top.Level + 1;
                    }
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        BidiClass ov = BidiClass.ON;
                        if (t == BidiClass.RLO) ov = BidiClass.R;
                        else if (t == BidiClass.LRO) ov = BidiClass.L;
                        sp++;
                        stack[sp] = new StackEntry { Level = newLevel, Override = ov, Isolate = false };
                    }
                    else if (overflowIsolate == 0)
                    {
                        overflowEmbedding++;
                    }
                }
                else if (IsIsolateInitiator(t))
                {
                    levels[i] = top.Level;
                    if (top.Override != BidiClass.ON)
                    {
                        types[i] = top.Override;
                    }
                    bool rtl = t == BidiClass.RLI;
                    if (t == BidiClass.FSI)
                    {
                        int end = matchPdi[i] != -1 ? matchPdi[i] : n;
                        rtl = FirstStrongLevel(orig, i + 1, end, matchPdi) == 1;
                    }
                    int newLevel;
                    if (rtl)
                    {
                        newLevel = top.Level % 2 == 0 ? top.Level + 1 : top.Level + 2;
                    }
                    else
                    {
                        newLevel = top.Level % 2 == 0 ? top.Level + 2 : top.Level + 1;
                    }
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        validIsolate++;
                        sp++;
                        stack[sp] = new StackEntry { Level = newLevel, Override = BidiClass.ON, Isolate = true };
                    }
                    else
                    {
                        overflowIsolate++;
                    }
                }
                else if (t == BidiClass.PDI)
                {
                    if (overflowIsolate > 0)
                    {
                        overflowIsolate--;
                    }
                    else if (validIsolate == 0)
                    {
                        // does not match any isolate initiator
                    }
                    else
                    {
                        overflowEmbedding = 0;
                        while (!stack[sp].Isolate)
                        {
                            sp--;
                        }
                        sp--;
                        validIsolate--;
                    }
                    top = stack[sp];
                    levels[i] = top.Level;
                    if (top.Override != BidiClass.ON)
                    {
                        types[i] = top.Override;
                    }
                }
                else if (t == BidiClass.PDF)
                {
                    levels[i] = top.Level;
                    if (overflowIsolate > 0)
                    {
                        // matches an overflow isolate: nothing to do
                    }
                    else if (overflowEmbedding > 0)
                    {
                        overflowEmbedding--;
                    }
                    else if (!top.Isolate && sp >= 1)
                    {
                        sp--;
                    }
                }
                else if (t == BidiClass.B)
                {
                    levels[i] = para;
                }
                else if (t == BidiClass.BN)
                {
                    levels[i] = top.Level;
                }
                else
                {
                    levels[i] = top.Level;
                    if (top.Override != BidiClass.ON)
                    {
                        types[i] = top.Override;
                    }
                }
            }

            // X9
            List<int> keepList = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (!IsRemovedByX9(orig[i]))
                {
                    keepList.Add(i);
                }
            }
            int[] keep = keepList.ToArray();
            Resolved result = new Resolved { Levels = levels, Keep = keep, Paragraph = para };
            if (keep.Length == 0)
            {
                return result;
            }
            int[] keepPos = new int[n];
            for (int i = 0; i < n; i++)
            {
                keepPos[i] = -1;
            }
            for (int p = 0; p < keep.Length; p++)
            {
                keepPos[keep[p]] = p;
            }

            // X10: level runs and isolating run sequences
            List<List<int>> runs = new List<List<int>>();
            int[] runOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                runOf[i] = -1;
            }
            for (int p = 0; p < keep.Length; p++)
            {
                int i = keep[p];
                if (runs.Count > 0 && levels[runs[runs.Count - 1][runs[runs.Count - 1].Count - 1]] == levels[i])
                {
                    runs[runs.Count - 1].Add(i);
                }
                else
                {
                    List<int> fresh = new List<int>();
                    fresh.Add(i);
                    runs.Add(fresh);
                }
                runOf[i] = runs.Count - 1;
            }
            int[] runFirst = new int[n];
            for (int i = 0; i < n; i++)
            {
                runFirst[i] = -1;
            }
            for (int ri = 0; ri < runs.Count; ri++)
            {
                runFirst[runs[ri][0]] = ri;
            }

            int[] xLevels = (int[])levels.Clone();   // levels after X1-X8; I1 / I2 below must not leak into sos / eos
            List<List<int>> sequences = new List<List<int>>();
            for (int ri = 0; ri < runs.Count; ri++)
            {
                List<int> run = runs[ri];
                int first = run[0];
                if (orig[first] == BidiClass.PDI && matchInit[first] != -1)
                {
                    int init = matchInit[first];
                    if (runOf[init] != -1)
                    {
                        List<int> initRun = runs[runOf[init]];
                        if (initRun[initRun.Count - 1] == init)
                        {
                            continue;   // continues the sequence of its isolate initiator
                        }
                    }
                }
                List<int> seq = new List<int>(run);
                List<int> cur = run;
                while (true)
                {
                    int last = cur[cur.Count - 1];
                    if (IsIsolateInitiator(orig[last]) && matchPdi[last] != -1 && runFirst[matchPdi[last]] != -1)
                    {
                        cur = runs[runFirst[matchPdi[last]]];
                        seq.AddRange(cur);
                    }
                    else
                    {
                        break;
                    }
                }
                sequences.Add(seq);
            }

            for (int si = 0; si < sequences.Count; si++)
            {
                List<int> seq = sequences[si];
                int m = seq.Count;
                int level = xLevels[seq[0]];
                int pos0 = keepPos[seq[0]];
                int prevLevel = pos0 > 0 ? xLevels[keep[pos0 - 1]] : para;
                BidiClass sos = Math.Max(level, prevLevel) % 2 == 1 ? BidiClass.R : BidiClass.L;
                int lastIdx = seq[m - 1];
                int posLast = keepPos[lastIdx];
                int nextLevel;
                if (IsIsolateInitiator(orig[lastIdx]) && matchPdi[lastIdx] == -1)
                {
                    nextLevel = para;
                }
                else if (posLast + 1 < keep.Length)
                {
                    nextLevel = xLevels[keep[posLast + 1]];
                }
                else
                {
                    nextLevel = para;
                }
                BidiClass eos = Math.Max(level, nextLevel) % 2 == 1 ? BidiClass.R : BidiClass.L;
                BidiClass e = level % 2 == 1 ? BidiClass.R : BidiClass.L;
                int eDir = e == BidiClass.R ? 1 : 0;
                int sosDir = sos == BidiClass.R ? 1 : 0;
                int eosDir = eos == BidiClass.R ? 1 : 0;

                BidiClass[] t = new BidiClass[m];
                BidiClass[] preW1 = new BidiClass[m];
                for (int k = 0; k < m; k++)
                {
                    t[k] = types[seq[k]];
                    preW1[k] = t[k];
                }

                // W1
                BidiClass prev = sos;
                for (int k = 0; k < m; k++)
                {
                    if (t[k] == BidiClass.NSM)
                    {
                        t[k] = (prev == BidiClass.LRI || prev == BidiClass.RLI || prev == BidiClass.FSI ||
                                prev == BidiClass.PDI) ? BidiClass.ON : prev;
                    }
                    prev = t[k];
                }
                // W2
                BidiClass lastStrong = sos;
                for (int k = 0; k < m; k++)
                {
                    if (t[k] == BidiClass.L || t[k] == BidiClass.R || t[k] == BidiClass.AL)
                    {
                        lastStrong = t[k];
                    }
                    else if (t[k] == BidiClass.EN && lastStrong == BidiClass.AL)
                    {
                        t[k] = BidiClass.AN;
                    }
                }
                // W3
                for (int k = 0; k < m; k++)
                {
                    if (t[k] == BidiClass.AL)
                    {
                        t[k] = BidiClass.R;
                    }
                }
                // W4
                for (int k = 1; k < m - 1; k++)
                {
                    if (t[k] == BidiClass.ES && t[k - 1] == BidiClass.EN && t[k + 1] == BidiClass.EN)
                    {
                        t[k] = BidiClass.EN;
                    }
                    else if (t[k] == BidiClass.CS)
                    {
                        if (t[k - 1] == BidiClass.EN && t[k + 1] == BidiClass.EN)
                        {
                            t[k] = BidiClass.EN;
                        }
                        else if (t[k - 1] == BidiClass.AN && t[k + 1] == BidiClass.AN)
                        {
                            t[k] = BidiClass.AN;
                        }
                    }
                }
                // W5
                {
                    int k = 0;
                    while (k < m)
                    {
                        if (t[k] == BidiClass.ET)
                        {
                            int j = k;
                            while (j < m && t[j] == BidiClass.ET)
                            {
                                j++;
                            }
                            bool before = k > 0 && t[k - 1] == BidiClass.EN;
                            bool after = j < m && t[j] == BidiClass.EN;
                            if (before || after)
                            {
                                for (int x = k; x < j; x++)
                                {
                                    t[x] = BidiClass.EN;
                                }
                            }
                            k = j;
                        }
                        else
                        {
                            k++;
                        }
                    }
                }
                // W6
                for (int k = 0; k < m; k++)
                {
                    if (t[k] == BidiClass.ES || t[k] == BidiClass.ET || t[k] == BidiClass.CS)
                    {
                        t[k] = BidiClass.ON;
                    }
                }
                // W7
                lastStrong = sos;
                for (int k = 0; k < m; k++)
                {
                    if (t[k] == BidiClass.L || t[k] == BidiClass.R)
                    {
                        lastStrong = t[k];
                    }
                    else if (t[k] == BidiClass.EN && lastStrong == BidiClass.L)
                    {
                        t[k] = BidiClass.L;
                    }
                }

                // N0: paired brackets (BD14 / BD15 / BD16)
                {
                    List<int> stackClose = new List<int>();   // canonical closing bracket expected
                    List<int> stackPos = new List<int>();
                    List<int[]> pairs = new List<int[]>();
                    for (int k = 0; k < m; k++)
                    {
                        if (t[k] != BidiClass.ON)
                        {
                            continue;
                        }
                        int cp = cps[seq[k]];
                        int canonClose;
                        if (UnicodeData.TryGetClosingBracket(cp, out canonClose))
                        {
                            if (stackClose.Count == MaxBracketStack)
                            {
                                break;
                            }
                            stackClose.Add(canonClose);
                            stackPos.Add(k);
                        }
                        else
                        {
                            int canon = UnicodeData.CanonBracket(cp);
                            if (UnicodeData.IsClosingBracket(canon))
                            {
                                for (int s = stackClose.Count - 1; s >= 0; s--)
                                {
                                    if (stackClose[s] == canon)
                                    {
                                        pairs.Add(new int[] { stackPos[s], k });
                                        stackClose.RemoveRange(s, stackClose.Count - s);
                                        stackPos.RemoveRange(s, stackPos.Count - s);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    pairs.Sort(delegate (int[] a, int[] b)
                    {
                        int c = a[0].CompareTo(b[0]);
                        return c != 0 ? c : a[1].CompareTo(b[1]);
                    });
                    for (int pi = 0; pi < pairs.Count; pi++)
                    {
                        int o = pairs[pi][0];
                        int c = pairs[pi][1];
                        bool foundE = false;
                        bool foundOpp = false;
                        for (int k = o + 1; k < c; k++)
                        {
                            int sd = DirOf(t[k]);
                            if (sd < 0)
                            {
                                continue;
                            }
                            if (sd == eDir)
                            {
                                foundE = true;
                                break;
                            }
                            foundOpp = true;
                        }
                        BidiClass newDir;
                        if (foundE)
                        {
                            newDir = e;
                        }
                        else if (foundOpp)
                        {
                            int ctx = sosDir;
                            for (int k = o - 1; k >= 0; k--)
                            {
                                int sd = DirOf(t[k]);
                                if (sd >= 0)
                                {
                                    ctx = sd;
                                    break;
                                }
                            }
                            int oppDir = eDir == 1 ? 0 : 1;
                            int chosen = ctx == oppDir ? oppDir : eDir;
                            newDir = chosen == 1 ? BidiClass.R : BidiClass.L;
                        }
                        else
                        {
                            continue;
                        }
                        t[o] = newDir;
                        t[c] = newDir;
                        int[] ends = new int[] { o, c };
                        for (int ei = 0; ei < 2; ei++)
                        {
                            int k = ends[ei] + 1;
                            while (k < m && preW1[k] == BidiClass.NSM)
                            {
                                t[k] = newDir;
                                k++;
                            }
                        }
                    }
                }

                // N1 / N2
                {
                    int k = 0;
                    while (k < m)
                    {
                        if (IsNeutralOrIsolate(t[k]))
                        {
                            int j = k;
                            while (j < m && IsNeutralOrIsolate(t[j]))
                            {
                                j++;
                            }
                            int lead = k > 0 ? DirOf(t[k - 1]) : sosDir;
                            int trail = j < m ? DirOf(t[j]) : eosDir;
                            int chosen = (lead == trail && lead >= 0) ? lead : eDir;
                            BidiClass nd = chosen == 1 ? BidiClass.R : BidiClass.L;
                            for (int x = k; x < j; x++)
                            {
                                t[x] = nd;
                            }
                            k = j;
                        }
                        else
                        {
                            k++;
                        }
                    }
                }

                // I1 / I2
                for (int k = 0; k < m; k++)
                {
                    int i = seq[k];
                    if (level % 2 == 0)
                    {
                        if (t[k] == BidiClass.R)
                        {
                            levels[i] = level + 1;
                        }
                        else if (t[k] == BidiClass.AN || t[k] == BidiClass.EN)
                        {
                            levels[i] = level + 2;
                        }
                    }
                    else
                    {
                        if (t[k] == BidiClass.L || t[k] == BidiClass.EN || t[k] == BidiClass.AN)
                        {
                            levels[i] = level + 1;
                        }
                    }
                }
            }

            // L1
            {
                int j = keep.Length - 1;
                while (j >= 0 && IsWhitespaceOrIsolateFormat(orig[keep[j]]))
                {
                    levels[keep[j]] = para;
                    j--;
                }
                for (int p = 0; p < keep.Length; p++)
                {
                    BidiClass oc = orig[keep[p]];
                    if (oc == BidiClass.S || oc == BidiClass.B)
                    {
                        levels[keep[p]] = para;
                        int q = p - 1;
                        while (q >= 0 && IsWhitespaceOrIsolateFormat(orig[keep[q]]))
                        {
                            levels[keep[q]] = para;
                            q--;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Segment separators, paragraph separators and isolate formatting characters are never a cluster's base.</summary>
        private static bool CanCarryMarks(int cp)
        {
            BidiClass c = UnicodeData.ClassOf(cp);
            return c != BidiClass.S && c != BidiClass.B && c != BidiClass.LRI && c != BidiClass.RLI &&
                   c != BidiClass.FSI && c != BidiClass.PDI;
        }

        /// <summary>
        /// Rules L2 and L4 over clusters (a base character plus its following Mn / Me marks stay together, base
        /// first). A mark after a tab, a paragraph separator or an isolate initiator / PDI starts a cluster of its
        /// own. Returns, in left-to-right drawing order, the source index and the output code point (mirrored where
        /// L4 requires it) of every character that survives X9.
        /// </summary>
        internal static void Reorder(int[] cps, int paraLevel, out int[] visualIndex, out int[] visualCp, out int paragraph)
        {
            Resolved r = ResolveLevels(cps, paraLevel);
            int[] keep = r.Keep;
            int[] levels = r.Levels;
            paragraph = r.Paragraph;
            int kn = keep.Length;

            // clusters: start position in keep[], length
            int[] clStart = new int[kn];
            int[] clLen = new int[kn];
            int m = 0;
            for (int p = 0; p < kn; p++)
            {
                int i = keep[p];
                if (m > 0 && UnicodeData.IsMark(cps[i]) && CanCarryMarks(cps[keep[clStart[m - 1] + clLen[m - 1] - 1]]))
                {
                    clLen[m - 1]++;
                }
                else
                {
                    clStart[m] = p;
                    clLen[m] = 1;
                    m++;
                }
            }
            int[] order = new int[m];
            int[] clLevel = new int[m];
            for (int c = 0; c < m; c++)
            {
                order[c] = c;
                clLevel[c] = levels[keep[clStart[c]]];
            }
            if (m > 0)
            {
                int highest = 0;
                int lowestOdd = int.MaxValue;
                for (int c = 0; c < m; c++)
                {
                    if (clLevel[c] > highest) highest = clLevel[c];
                    if (clLevel[c] % 2 == 1 && clLevel[c] < lowestOdd) lowestOdd = clLevel[c];
                }
                if (lowestOdd != int.MaxValue)
                {
                    for (int lvl = highest; lvl >= lowestOdd; lvl--)
                    {
                        int k = 0;
                        while (k < m)
                        {
                            if (clLevel[k] >= lvl)
                            {
                                int j = k;
                                while (j < m && clLevel[j] >= lvl)
                                {
                                    j++;
                                }
                                Array.Reverse(order, k, j - k);
                                Array.Reverse(clLevel, k, j - k);
                                k = j;
                            }
                            else
                            {
                                k++;
                            }
                        }
                    }
                }
            }

            visualIndex = new int[kn];
            visualCp = new int[kn];
            int w = 0;
            for (int ci = 0; ci < m; ci++)
            {
                int c = order[ci];
                for (int off = 0; off < clLen[c]; off++)
                {
                    int i = keep[clStart[c] + off];
                    int cp = cps[i];
                    if (off == 0 && clLevel[ci] % 2 == 1)
                    {
                        cp = UnicodeData.MirrorOf(cp);
                    }
                    visualIndex[w] = i;
                    visualCp[w] = cp;
                    w++;
                }
            }
        }
    }
}
