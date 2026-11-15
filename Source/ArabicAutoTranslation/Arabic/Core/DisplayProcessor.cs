using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>How logical Arabic text is turned into display-ready text.</summary>
    internal sealed class DisplayOptions
    {
        /// <summary>Produce Arabic presentation forms (contextual shaping, lam-alef ligatures).</summary>
        internal bool Shape = true;
        /// <summary>Reorder visually (UAX #9, rule L2) and mirror brackets (L4).</summary>
        internal bool Reorder = true;
        /// <summary>Isolate signed numbers, unit symbols and LTR-only bracketed phrases with explicit LRI/PDI.</summary>
        internal bool LtrIsolates = true;
        /// <summary>Force a right-to-left paragraph (otherwise P2 / P3 decide).</summary>
        internal bool BaseRtl = true;

        /// <summary>Short stable text identifying the option combination (part of the cache identity).</summary>
        internal string Signature()
        {
            return (Shape ? "S1" : "S0") + (Reorder ? "R1" : "R0") + (LtrIsolates ? "I1" : "I0") + (BaseRtl ? "B1" : "B0");
        }

        internal static DisplayOptions Default()
        {
            return new DisplayOptions();
        }
    }

    internal enum TokKind
    {
        Text,
        Atom,
        Open,
        Close
    }

    /// <summary>Piece of a line: plain text, an atom (placeholder), or a rich-text tag.</summary>
    internal sealed class Tok
    {
        internal TokKind Kind;
        internal string Text;
        internal string Name;     // tag name for Open / Close
        internal string Lead = ""; // sign that travels inside the isolate of an Atom

        internal Tok(TokKind kind, string text, string name)
        {
            Kind = kind;
            Text = text;
            Name = name;
        }
    }

    /// <summary>
    /// Logical Arabic text -> display-ready text for a renderer without BiDi and shaping (Unity IMGUI).
    /// One line: tokenize (placeholders are atoms, rich-text tags are zero-width anchors) -> shape in logical order ->
    /// UAX #9 reorder -> assemble (atoms expanded, rich-text spans re-wrapped around the visual extents of their
    /// text). Port of Tools/arabic_ref.py.
    /// </summary>
    internal static class DisplayProcessor
    {
        internal const int Lri = 0x2066;
        internal const int Rli = 0x2067;
        internal const int Fsi = 0x2068;
        internal const int Pdi = 0x2069;
        internal const int AtomBase = 0xF0000;   // supplementary private use area A: bidi class L, no joining

        private static readonly Regex TokenRegex = new Regex(
            @"(?<brace>\{[A-Za-z0-9_.\-:,#]+\})" +
            @"|(?<square>\[[A-Za-z_][A-Za-z0-9_.\-]*(?:\([^\[\]\r\n]*\))?\])" +
            @"|(?<close></(?:[bB]|[iI]|[sS][iI][zZ][eE]|[cC][oO][lL][oO][rR]|[mM][aA][tT][eE][rR][iI][aA][lL])>)" +
            @"|(?<open><(?:[bB]|[iI])>|<(?:[sS][iI][zZ][eE]|[cC][oO][lL][oO][rR]|[mM][aA][tT][eE][rR][iI][aA][lL])=[^<>\r\n]*>)" +
            @"|(?<quad><[qQ][uU][aA][dD][^<>\r\n]*>)" +
            @"|(?<angle><[A-Za-z][A-Za-z0-9_]*>)",
            RegexOptions.CultureInvariant);

        private const string DigitClass = @"0-9\u0660-\u0669\u06F0-\u06F9";

        private static readonly Regex GlueRegex = new Regex(
            @"(?<![" + DigitClass + @"])([+\-\u2212]?)([" + DigitClass + @"]+(?:[.,][" + DigitClass + @"]+)*)" +
            @"(%|\u2030|\u00B0[CFKcfk]?)?",
            RegexOptions.CultureInvariant);

        private static readonly Regex UnitAfterAtomRegex = new Regex(@"^(%|\u2030|\u00B0[CFKcfk]?)",
            RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------------

        /// <summary>True when the text contains a strong right-to-left letter (Arabic, Hebrew, presentation forms ...).</summary>
        internal static bool HasStrongRtl(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x0590)
                {
                    continue;   // nothing below U+0590 is R / AL
                }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                }
                if (UnicodeData.IsStrongRtl(cp))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsControl(int cp)
        {
            return cp == Lri || cp == Rli || cp == Fsi || cp == Pdi || cp == 0x200E || cp == 0x200F || cp == 0x061C ||
                   cp == 0x200C || cp == 0x200D || (cp >= 0x202A && cp <= 0x202E);
        }

        private static bool IsSign(char c)
        {
            return c == '+' || c == '-' || c == '\u2212';
        }

        /// <summary>Code point that ends just before <paramref name="endExclusive"/> (a surrogate pair counts as one).</summary>
        private static int CodePointBefore(string s, int endExclusive)
        {
            char low = s[endExclusive - 1];
            if (char.IsLowSurrogate(low) && endExclusive >= 2 && char.IsHighSurrogate(s[endExclusive - 2]))
            {
                return char.ConvertToUtf32(s[endExclusive - 2], low);
            }
            return low;
        }

        // ------------------------------------------------------------------------------------------------
        // tokenizer
        // ------------------------------------------------------------------------------------------------

        internal static List<Tok> Tokenize(string line)
        {
            List<Tok> toks = new List<Tok>();
            int pos = 0;
            MatchCollection matches = TokenRegex.Matches(line);
            for (int mi = 0; mi < matches.Count; mi++)
            {
                Match m = matches[mi];
                if (m.Index > pos)
                {
                    toks.Add(new Tok(TokKind.Text, line.Substring(pos, m.Index - pos), null));
                }
                string raw = m.Value;
                if (m.Groups["open"].Success)
                {
                    string body = raw.Substring(1);
                    int eq = body.IndexOf('=');
                    string name = (eq >= 0 ? body.Substring(0, eq) : body).TrimEnd('>').ToLowerInvariant();
                    toks.Add(new Tok(TokKind.Open, raw, name));
                }
                else if (m.Groups["close"].Success)
                {
                    toks.Add(new Tok(TokKind.Close, raw, raw.Substring(2, raw.Length - 3).ToLowerInvariant()));
                }
                else
                {
                    toks.Add(new Tok(TokKind.Atom, raw, null));
                }
                pos = m.Index + m.Length;
            }
            if (pos < line.Length)
            {
                toks.Add(new Tok(TokKind.Text, line.Substring(pos), null));
            }
            return toks;
        }

        // ------------------------------------------------------------------------------------------------
        // number / unit isolation (optional, documented deviation: expressed with explicit LRI / PDI isolates)
        // ------------------------------------------------------------------------------------------------

        private static void GlueNumbers(string s, List<int> output)
        {
            int pos = 0;
            MatchCollection matches = GlueRegex.Matches(s);
            for (int mi = 0; mi < matches.Count; mi++)
            {
                Match m = matches[mi];
                bool hasSign = m.Groups[1].Success && m.Groups[1].Length > 0;
                bool hasUnit = m.Groups[3].Success && m.Groups[3].Length > 0;
                if (!hasSign && !hasUnit)
                {
                    continue;
                }
                CodePoints.AddString(output, s.Substring(pos, m.Index - pos));
                output.Add(Lri);
                CodePoints.AddString(output, m.Value);
                output.Add(Pdi);
                pos = m.Index + m.Length;
            }
            CodePoints.AddString(output, s.Substring(pos));
        }

        private static bool IsLtrPhrase(string inner)
        {
            bool hasAlnum = false;
            int[] cps = CodePoints.From(inner);
            for (int i = 0; i < cps.Length; i++)
            {
                BidiClass cls = UnicodeData.ClassOf(cps[i]);
                if (cls == BidiClass.R || cls == BidiClass.AL || cls == BidiClass.AN)
                {
                    return false;
                }
                if (cls == BidiClass.L || cls == BidiClass.EN)
                {
                    hasAlnum = true;
                }
            }
            return hasAlnum;
        }

        /// <summary>(start, end exclusive) of ( .. ) and [ .. ] segments whose content is left-to-right only.</summary>
        private static List<int[]> BracketPhraseSpans(string s)
        {
            List<int[]> spans = new List<int[]>();
            int n = s.Length;
            int i = 0;
            while (i < n)
            {
                char ch = s[i];
                if (ch == '(' || ch == '[')
                {
                    char close = ch == '(' ? ')' : ']';
                    int depth = 0;
                    int end = -1;
                    for (int j = i; j < n; j++)
                    {
                        if (s[j] == ch)
                        {
                            depth++;
                        }
                        else if (s[j] == close)
                        {
                            depth--;
                            if (depth == 0)
                            {
                                end = j;
                                break;
                            }
                        }
                    }
                    if (end > 0 && IsLtrPhrase(s.Substring(i + 1, end - i - 1)))
                    {
                        spans.Add(new int[] { i, end + 1 });
                        i = end + 1;
                        continue;
                    }
                }
                i++;
            }
            return spans;
        }

        /// <summary>Logical code points of a text token, with LTR phrases / numbers wrapped in explicit isolates.</summary>
        private static void IsolateText(string s, DisplayOptions opts, List<int> output)
        {
            if (!opts.LtrIsolates)
            {
                CodePoints.AddString(output, s);
                return;
            }
            int pos = 0;
            List<int[]> spans = BracketPhraseSpans(s);
            for (int k = 0; k < spans.Count; k++)
            {
                int a = spans[k][0];
                int b = spans[k][1];
                GlueNumbers(s.Substring(pos, a - pos), output);
                output.Add(Lri);
                CodePoints.AddString(output, s.Substring(a, b - a));
                output.Add(Pdi);
                pos = b;
            }
            GlueNumbers(s.Substring(pos), output);
        }

        // ------------------------------------------------------------------------------------------------
        // one line
        // ------------------------------------------------------------------------------------------------

        private sealed class Span
        {
            internal int Open;
            internal int Close;   // -1 = still open at the end of the line
        }

        /// <summary>
        /// Processes one line (no line breaks). <paramref name="carry"/> holds the opener tokens still open from earlier
        /// lines. Returns null when the line has nothing to process (the caller keeps it as it is).
        /// </summary>
        internal static string ProcessLine(string line, List<Tok> carry, DisplayOptions opts, out List<Tok> newCarry)
        {
            List<Tok> toks = Tokenize(line);
            for (int c = carry.Count - 1; c >= 0; c--)
            {
                toks.Insert(0, new Tok(TokKind.Open, carry[c].Text, carry[c].Name));
            }

            // tag matching; ill-formed closers degrade to atoms
            List<int> stack = new List<int>();
            List<Span> spans = new List<Span>();
            for (int ti = 0; ti < toks.Count; ti++)
            {
                Tok tk = toks[ti];
                if (tk.Kind == TokKind.Open)
                {
                    stack.Add(ti);
                }
                else if (tk.Kind == TokKind.Close)
                {
                    if (stack.Count > 0 && toks[stack[stack.Count - 1]].Name == tk.Name)
                    {
                        spans.Add(new Span { Open = stack[stack.Count - 1], Close = ti });
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        tk.Kind = TokKind.Atom;
                    }
                }
            }
            newCarry = new List<Tok>();
            for (int si = 0; si < stack.Count; si++)
            {
                spans.Add(new Span { Open = stack[si], Close = -1 });
                newCarry.Add(toks[stack[si]]);
            }
            spans.Sort(delegate (Span a, Span b) { return a.Open.CompareTo(b.Open); });

            // logical sequence
            List<int> seq = new List<int>();
            int[] anchors = new int[toks.Count];
            List<string> atoms = new List<string>();
            for (int ti = 0; ti < toks.Count; ti++)
            {
                Tok tk = toks[ti];
                if (tk.Kind == TokKind.Text)
                {
                    string s = tk.Text;
                    string lead = "";
                    if (opts.LtrIsolates && ti + 1 < toks.Count && toks[ti + 1].Kind == TokKind.Atom && s.Length > 0 &&
                        IsSign(s[s.Length - 1]))
                    {
                        int before = s.Length > 1 ? CodePointBefore(s, s.Length - 1) : ' ';
                        if (!(UnicodeData.IsWordLike(before) || before == '.' || before == ','))
                        {
                            lead = s.Substring(s.Length - 1);
                            s = s.Substring(0, s.Length - 1);
                        }
                    }
                    IsolateText(s, opts, seq);
                    tk.Text = s;
                    if (lead.Length > 0)
                    {
                        toks[ti + 1].Lead = lead;   // remember the sign for the atom
                    }
                }
                else if (tk.Kind == TokKind.Atom)
                {
                    string suffix = "";
                    if (opts.LtrIsolates && ti + 1 < toks.Count && toks[ti + 1].Kind == TokKind.Text)
                    {
                        Match m = UnitAfterAtomRegex.Match(toks[ti + 1].Text);
                        if (m.Success)
                        {
                            suffix = m.Groups[1].Value;
                            toks[ti + 1].Text = toks[ti + 1].Text.Substring(suffix.Length);
                        }
                    }
                    atoms.Add(tk.Text);
                    seq.Add(Lri);
                    if (tk.Lead.Length > 0)
                    {
                        CodePoints.AddString(seq, tk.Lead);
                    }
                    seq.Add(AtomBase + atoms.Count - 1);
                    CodePoints.AddString(seq, suffix);
                    seq.Add(Pdi);
                }
                else
                {
                    anchors[ti] = seq.Count;
                }
            }

            bool hasRtl = false;
            for (int i = 0; i < seq.Count; i++)
            {
                if (UnicodeData.IsStrongRtl(seq[i]))
                {
                    hasRtl = true;
                    break;
                }
            }
            if (!hasRtl)
            {
                return null;   // nothing to do: the caller keeps the original line
            }

            int[] logical = seq.ToArray();
            int[] shaped;
            int[] ownerStart;
            if (opts.Shape)
            {
                ArabicShaper.Shape(logical, out shaped, out ownerStart);
            }
            else
            {
                shaped = (int[])logical.Clone();
                ownerStart = new int[shaped.Length];
                for (int i = 0; i < ownerStart.Length; i++)
                {
                    ownerStart[i] = i;
                }
            }

            int[] visIdx;
            int[] visCp;
            if (opts.Reorder)
            {
                int para;
                Bidi.Reorder(shaped, opts.BaseRtl ? 1 : -1, out visIdx, out visCp, out para);
            }
            else
            {
                visIdx = new int[shaped.Length];
                visCp = new int[shaped.Length];
                for (int i = 0; i < shaped.Length; i++)
                {
                    visIdx[i] = i;
                    visCp[i] = shaped[i];
                }
            }

            // drop the control characters that only steered the algorithm
            List<int> vIndex = new List<int>(visIdx.Length);
            List<int> vCp = new List<int>(visIdx.Length);
            for (int i = 0; i < visIdx.Length; i++)
            {
                if (!IsControl(visCp[i]))
                {
                    vIndex.Add(visIdx[i]);
                    vCp.Add(visCp[i]);
                }
            }
            int m2 = vIndex.Count;

            // which spans contain which visible characters
            List<Span>[] opensAt = new List<Span>[m2];
            List<Span>[] closesAt = new List<Span>[m2];
            for (int p = 0; p < m2; p++)
            {
                opensAt[p] = new List<Span>();
                closesAt[p] = new List<Span>();
            }
            for (int si = 0; si < spans.Count; si++)
            {
                Span sp = spans[si];
                int a = anchors[sp.Open];
                int b = sp.Close >= 0 ? anchors[sp.Close] : seq.Count;
                bool[] member = new bool[m2];
                for (int p = 0; p < m2; p++)
                {
                    int owner = ownerStart[vIndex[p]];
                    member[p] = a <= owner && owner < b;
                }
                int pp = 0;
                while (pp < m2)
                {
                    if (member[pp])
                    {
                        int q = pp;
                        while (q + 1 < m2 && member[q + 1])
                        {
                            q++;
                        }
                        opensAt[pp].Add(sp);
                        closesAt[q].Add(sp);
                        pp = q + 1;
                    }
                    else
                    {
                        pp++;
                    }
                }
            }

            StringBuilder sb = new StringBuilder(line.Length + 16);
            for (int p = 0; p < m2; p++)
            {
                List<Span> opens = opensAt[p];
                opens.Sort(delegate (Span a, Span b) { return a.Open.CompareTo(b.Open); });
                for (int k = 0; k < opens.Count; k++)
                {
                    sb.Append(toks[opens[k].Open].Text);
                }
                int cp = vCp[p];
                if (cp >= AtomBase && cp < AtomBase + atoms.Count)
                {
                    sb.Append(atoms[cp - AtomBase]);
                }
                else
                {
                    CodePoints.Append(sb, cp);
                }
                List<Span> closes = closesAt[p];
                closes.Sort(delegate (Span a, Span b) { return b.Open.CompareTo(a.Open); });
                for (int k = 0; k < closes.Count; k++)
                {
                    Span sp = closes[k];
                    if (sp.Close >= 0)
                    {
                        sb.Append(toks[sp.Close].Text);
                    }
                    else
                    {
                        sb.Append("</").Append(toks[sp.Open].Name).Append('>');
                    }
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------
        // whole text
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Splits at paragraph separators (CR LF, LF, CR, NEL, PS, FS/GS/RS and the two-character text "\n"); the
        /// separators are returned as the odd entries.
        /// </summary>
        internal static List<string> SplitLines(string text)
        {
            List<string> parts = new List<string>();
            StringBuilder cur = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                string sep = null;
                if (c == '\r')
                {
                    sep = (i + 1 < text.Length && text[i + 1] == '\n') ? "\r\n" : "\r";
                }
                else if (c == '\n' || c == '\u0085' || c == '\u2029' || c == '\u001C' || c == '\u001D' || c == '\u001E')
                {
                    sep = c.ToString();
                }
                else if (c == '\\' && i + 1 < text.Length && text[i + 1] == 'n')
                {
                    sep = "\\n";
                }
                if (sep != null)
                {
                    parts.Add(cur.ToString());
                    cur.Length = 0;
                    parts.Add(sep);
                    i += sep.Length;
                }
                else
                {
                    cur.Append(c);
                    i++;
                }
            }
            parts.Add(cur.ToString());
            return parts;
        }

        /// <summary>Logical Arabic text -> display-ready text. Text without strong RTL letters is returned unchanged.</summary>
        internal static string Process(string logical, DisplayOptions opts)
        {
            if (string.IsNullOrEmpty(logical) || !HasStrongRtl(logical))
            {
                return logical;
            }
            if (opts == null)
            {
                opts = DisplayOptions.Default();
            }
            logical = ArabicShaper.Unshape(logical);
            List<string> parts = SplitLines(logical);
            StringBuilder sb = new StringBuilder(logical.Length + 16);
            List<Tok> carry = new List<Tok>();
            for (int idx = 0; idx < parts.Count; idx++)
            {
                string part = parts[idx];
                if (idx % 2 == 1)
                {
                    sb.Append(part);
                    continue;
                }
                List<Tok> carry2;
                string res = ProcessLine(part, carry, opts, out carry2);
                sb.Append(res == null ? part : res);
                carry = carry2;
            }
            return sb.ToString();
        }
    }
}
