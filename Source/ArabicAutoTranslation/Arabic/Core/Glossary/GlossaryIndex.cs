using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Finds glossary terms in an English source text and turns them into a translation plan.
    ///
    /// This is not a global string replacement: terms are matched as whole words / phrases on word tokens, longer and more
    /// specific entries win over shorter overlapping ones, context conditions (mod, def type / field) and DLC scope
    /// decide between entries that share an English text, adjectives and verbs are only enforced where that is safe,
    /// and placeholders, tags and escape sequences are never touched.
    /// </summary>
    internal sealed class GlossaryIndex
    {
        private enum TK : byte
        {
            Word,
            Space,
            Break,
            Punct,
            Tag,
            Var,
            Esc
        }

        private struct Token
        {
            internal int Start;
            internal int End;   // exclusive
            internal TK Kind;
        }

        private sealed class Surface
        {
            internal string[] Words;   // lower case
            internal GlossaryEntry Entry;
            internal TermForm Form;
            internal int Chars;
        }

        private sealed class Cand
        {
            internal Surface Surface;
            internal int FirstTok;
            internal int LastTok;
            internal int StartTok;     // including a determiner
            internal int StartChar;
            internal int EndChar;
            internal bool Definite;
            internal bool Indefinite;
            internal int SectionRank;
        }

        // the same spans the translators protect (Helpers.ProtectPlaceholders): tags, {..}, [..] and \n \r \t
        private static readonly Regex ProtectedSpan = new Regex(@"<[^>]+>|\{[^\}]+\}|\[[^\]]+\]|\\[nrt]", RegexOptions.CultureInvariant);

        private static readonly Regex MarkerPattern = new Regex(@"\{\s*GL\s*(\d+)\s*\}", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> VerbPrecursors = new HashSet<string>(StringComparer.Ordinal)
        {
            "to", "will", "would", "can", "cannot", "could", "should", "must", "may", "might", "shall", "please", "let", "lets"
        };

        private static readonly HashSet<string> VerbObjects = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "this", "that", "these", "those", "your", "their", "his", "her", "its", "our", "my",
            "it", "them", "him", "us", "you", "me", "all", "any", "each", "every"
        };

        private readonly Dictionary<string, List<Surface>> _byFirstWord = new Dictionary<string, List<Surface>>(StringComparer.Ordinal);

        internal GlossaryData Data { get; private set; }

        internal GlossaryIndex(GlossaryData data)
        {
            Data = data;
            foreach (var e in data.Entries) AddEntry(e);
        }

        // ---------------------------------------------------------------------------------------------- building

        private void AddEntry(GlossaryEntry e)
        {
            foreach (var english in e.English)
            {
                AddSurface(e, english, TermForm.Singular);
                if (e.ArabicPlural != null)
                {
                    foreach (var pl in PluralsOf(english)) AddSurface(e, pl, TermForm.Plural);
                }
            }
            if (e.ArabicPlural != null)
            {
                foreach (var pl in e.EnglishPlural) AddSurface(e, StripThe(pl), TermForm.Plural);
            }
        }

        private static string StripThe(string s)
        {
            return GlossaryParser.StripLeadingThe(s);
        }

        private void AddSurface(GlossaryEntry e, string english, TermForm form)
        {
            var words = SplitWords(english);
            if (words.Length == 0) return;

            var sf = new Surface { Words = words, Entry = e, Form = form };
            int chars = 0;
            foreach (var w in words) chars += w.Length;
            sf.Chars = chars + words.Length - 1;

            List<Surface> list;
            if (!_byFirstWord.TryGetValue(words[0], out list))
            {
                list = new List<Surface>();
                _byFirstWord[words[0]] = list;
            }

            // an identical surface of the same entry and form is only registered once
            foreach (var other in list)
            {
                if (other.Entry == e && other.Form == form && SameWords(other.Words, words)) return;
            }
            list.Add(sf);
        }

        private static bool SameWords(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static string[] SplitWords(string text)
        {
            var words = new List<string>();
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (IsWordChar(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0)
                {
                    words.Add(sb.ToString());
                    sb.Length = 0;
                }
            }
            if (sb.Length > 0) words.Add(sb.ToString());
            return words.ToArray();
        }

        /// <summary>English plural spellings of a term (the head noun is the last word).</summary>
        internal static string[] PluralsOf(string term)
        {
            term = term.TrimEnd();
            if (term.Length == 0) return new string[0];
            int sp = term.LastIndexOf(' ');
            var head = sp < 0 ? term : term.Substring(sp + 1);
            var prefix = sp < 0 ? "" : term.Substring(0, sp + 1);
            return new[] { prefix + PluralWord(head) };
        }

        private static string PluralWord(string w)
        {
            if (w.Length == 0) return w;
            char last = w[w.Length - 1];
            char lowerLast = char.ToLowerInvariant(last);
            if (lowerLast == 'y' && w.Length > 1 && !IsVowel(w[w.Length - 2]))
                return w.Substring(0, w.Length - 1) + "ies";
            if (lowerLast == 's' || lowerLast == 'x' || lowerLast == 'z')
                return w + "es";
            if (w.Length > 1 && lowerLast == 'h' && (char.ToLowerInvariant(w[w.Length - 2]) == 'c' || char.ToLowerInvariant(w[w.Length - 2]) == 's'))
                return w + "es";
            return w + "s";
        }

        private static bool IsVowel(char c)
        {
            c = char.ToLowerInvariant(c);
            return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
        }

        internal static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // ---------------------------------------------------------------------------------------------- tokens

        private static List<Token> Tokenize(string s)
        {
            var toks = new List<Token>();
            int pos = 0;
            for (var m = ProtectedSpan.Match(s); m.Success; m = m.NextMatch())
            {
                TokenizePlain(s, pos, m.Index, toks);
                TK kind;
                char c0 = s[m.Index];
                if (c0 == '<') kind = TK.Tag;
                else if (c0 == '\\') kind = TK.Esc;
                else kind = TK.Var;
                toks.Add(new Token { Start = m.Index, End = m.Index + m.Length, Kind = kind });
                pos = m.Index + m.Length;
            }
            TokenizePlain(s, pos, s.Length, toks);
            return toks;
        }

        private static void TokenizePlain(string s, int from, int to, List<Token> toks)
        {
            int i = from;
            while (i < to)
            {
                char c = s[i];
                if (IsWordChar(c))
                {
                    int j = i + 1;
                    while (j < to && IsWordChar(s[j])) j++;
                    toks.Add(new Token { Start = i, End = j, Kind = TK.Word });
                    i = j;
                }
                else if (c == ' ' || c == '\t' || c == '\u00A0')
                {
                    int j = i + 1;
                    while (j < to && (s[j] == ' ' || s[j] == '\t' || s[j] == '\u00A0')) j++;
                    toks.Add(new Token { Start = i, End = j, Kind = TK.Space });
                    i = j;
                }
                else if (c == '\n' || c == '\r')
                {
                    toks.Add(new Token { Start = i, End = i + 1, Kind = TK.Break });
                    i++;
                }
                else
                {
                    toks.Add(new Token { Start = i, End = i + 1, Kind = TK.Punct });
                    i++;
                }
            }
        }

        private static bool IsContent(TK k)
        {
            return k == TK.Word || k == TK.Var;
        }

        private static string WordText(string s, Token t)
        {
            return s.Substring(t.Start, t.End - t.Start);
        }

        // ---------------------------------------------------------------------------------------------- scanning

        /// <summary>Finds the glossary terms of a text. Never throws.</summary>
        internal GlossaryScan Scan(string source, GlossaryContext ctx)
        {
            var scan = new GlossaryScan { Source = source, Context = ctx ?? GlossaryContext.None };
            if (string.IsNullOrEmpty(source)) return scan;
            ctx = scan.Context;
            scan.MarkerCollision = MarkerPattern.IsMatch(source);

            var toks = Tokenize(source);
            var cands = new List<Cand>();

            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].Kind != TK.Word) continue;
                var lower = WordText(source, toks[i]).ToLowerInvariant();
                List<Surface> list;
                if (!_byFirstWord.TryGetValue(lower, out list)) continue;

                foreach (var sf in list)
                {
                    int last;
                    if (!MatchWords(source, toks, i, sf.Words, out last)) continue;

                    var e = sf.Entry;
                    if (!ConditionsHold(e, ctx)) continue;
                    if (e.CaseSensitive && !char.IsUpper(source[toks[i].Start])) continue;

                    var cand = new Cand { Surface = sf, FirstTok = i, LastTok = last };
                    bool definite, indefinite;
                    cand.StartTok = FindDeterminer(source, toks, i, out definite, out indefinite);
                    cand.Definite = definite;
                    cand.Indefinite = indefinite;
                    cand.StartChar = toks[cand.StartTok].Start;
                    cand.EndChar = toks[last].End;

                    if (e.Mode == GlossaryMode.Whole)
                    {
                        if (HasContent(toks, 0, cand.StartTok - 1) || HasContent(toks, last + 1, toks.Count - 1)) continue;
                    }
                    else if (e.Mode == GlossaryMode.Start)
                    {
                        if (HasContent(toks, 0, cand.StartTok - 1)) continue;
                    }

                    if (e.Guard && GuardRejects(source, toks, i, last)) continue;

                    cand.SectionRank = SectionMatches(e, ctx) ? 1 : 0;
                    cands.Add(cand);
                }
            }

            if (cands.Count == 0) return scan;

            cands.Sort(CompareCandidates);

            var used = new bool[toks.Count];
            var chosen = new List<Cand>();
            foreach (var c in cands)
            {
                bool free = true;
                for (int k = c.StartTok; k <= c.LastTok; k++)
                {
                    if (used[k]) { free = false; break; }
                }
                if (!free)
                {
                    // the determiner may have been taken by a neighbour: retry without it
                    if (c.StartTok != c.FirstTok)
                    {
                        bool freeBare = true;
                        for (int k = c.FirstTok; k <= c.LastTok; k++)
                        {
                            if (used[k]) { freeBare = false; break; }
                        }
                        if (freeBare)
                        {
                            c.StartTok = c.FirstTok;
                            c.StartChar = toks[c.FirstTok].Start;
                            c.Definite = false;
                            c.Indefinite = false;
                            free = true;
                        }
                    }
                }
                if (!free) continue;
                // Whole entries must still be the only content once other (longer) matches have been chosen
                for (int k = c.StartTok; k <= c.LastTok; k++) used[k] = true;
                chosen.Add(c);
            }

            chosen.Sort(delegate (Cand a, Cand b) { return a.StartChar.CompareTo(b.StartChar); });

            foreach (var c in chosen)
            {
                var e = c.Surface.Entry;
                var form = c.Surface.Form;
                if (form == TermForm.Singular && ctx.Hint == "female" && e.ArabicFeminine != null) form = TermForm.Feminine;
                else if (form == TermForm.Singular && ctx.Hint == "femaleplural" && (e.ArabicFemininePlural != null || e.ArabicFeminine != null))
                    form = TermForm.FemininePlural;
                else if (form == TermForm.Singular && ctx.Hint == "plural" && e.ArabicPlural != null) form = TermForm.Plural;

                var m = new GlossaryMatch
                {
                    Start = c.StartChar,
                    End = c.EndChar,
                    Entry = e,
                    Form = form,
                    Definite = c.Definite,
                    Indefinite = c.Indefinite,
                    Surface = source.Substring(toks[c.FirstTok].Start, toks[c.LastTok].End - toks[c.FirstTok].Start),
                    FirstToken = c.StartTok,
                    LastToken = c.LastTok
                };
                m.WholeText = !HasContent(toks, 0, c.StartTok - 1) && !HasContent(toks, c.LastTok + 1, toks.Count - 1);
                scan.Matches.Add(m);
            }

            // a Whole entry loses its match when another chosen match turned out to share the text
            if (scan.Matches.Count > 1)
            {
                for (int i = scan.Matches.Count - 1; i >= 0; i--)
                {
                    var m = scan.Matches[i];
                    if (m.Entry.Mode == GlossaryMode.Whole && !m.WholeText) scan.Matches.RemoveAt(i);
                }
            }
            return scan;
        }

        private static int CompareCandidates(Cand a, Cand b)
        {
            int c = b.Surface.Words.Length.CompareTo(a.Surface.Words.Length);
            if (c != 0) return c;
            c = b.Surface.Chars.CompareTo(a.Surface.Chars);
            if (c != 0) return c;
            c = b.Surface.Entry.Specificity.CompareTo(a.Surface.Entry.Specificity);
            if (c != 0) return c;
            c = b.SectionRank.CompareTo(a.SectionRank);
            if (c != 0) return c;
            c = a.Surface.Entry.Order.CompareTo(b.Surface.Entry.Order);
            if (c != 0) return c;
            c = a.StartChar.CompareTo(b.StartChar);
            if (c != 0) return c;
            return a.Surface.Form.CompareTo(b.Surface.Form);
        }

        private static bool MatchWords(string s, List<Token> toks, int i, string[] words, out int last)
        {
            last = i;
            int j = i;
            for (int w = 0; w < words.Length; w++)
            {
                if (j >= toks.Count || toks[j].Kind != TK.Word) return false;
                var t = toks[j];
                if (t.End - t.Start != words[w].Length) return false;
                if (string.Compare(s, t.Start, words[w], 0, words[w].Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                last = j;
                if (w < words.Length - 1)
                {
                    int sep = j + 1;
                    // a possessive: the words "driver" and "s" of a term are the text "driver's" (also with the typographic
                    // apostrophe), and "elders' cook" is the term "Elders' cook" - the apostrophe is not a word of its own
                    if (sep < toks.Count && IsApostrophe(s, toks[sep]))
                    {
                        if (words[w + 1] == "s")
                        {
                            if (sep + 1 >= toks.Count || !IsLetterS(s, toks[sep + 1])) return false;
                            j = sep + 1;
                            w++;
                            last = j;
                            if (w == words.Length - 1) return true;
                            sep = j + 1;
                        }
                        else
                        {
                            sep++;
                        }
                    }
                    // the words of a term are separated by a space, or by a hyphen ("spirit-wood arm" is the term "Spirit wood arm")
                    bool dash = sep < toks.Count && toks[sep].Kind == TK.Punct && s[toks[sep].Start] == '-';
                    if (sep + 1 >= toks.Count || (toks[sep].Kind != TK.Space && !dash)) return false;
                    j = sep + 1;
                }
            }
            return true;
        }

        private static bool IsApostrophe(string s, Token t)
        {
            return t.Kind == TK.Punct && (s[t.Start] == '\'' || s[t.Start] == '\u2019');
        }

        private static bool IsLetterS(string s, Token t)
        {
            return t.Kind == TK.Word && t.End - t.Start == 1 && (s[t.Start] == 's' || s[t.Start] == 'S');
        }

        private static bool HasContent(List<Token> toks, int from, int to)
        {
            for (int i = from; i <= to; i++)
            {
                if (i >= 0 && i < toks.Count && IsContent(toks[i].Kind)) return true;
            }
            return false;
        }

        private static int FindDeterminer(string s, List<Token> toks, int first, out bool definite, out bool indefinite)
        {
            definite = false;
            indefinite = false;
            if (first >= 2 && toks[first - 1].Kind == TK.Space && toks[first - 2].Kind == TK.Word)
            {
                var d = WordText(s, toks[first - 2]).ToLowerInvariant();
                if (d == "the") { definite = true; return first - 2; }
                if (d == "a" || d == "an") { indefinite = true; return first - 2; }
            }
            return first;
        }

        private static bool ConditionsHold(GlossaryEntry e, GlossaryContext ctx)
        {
            if (ctx.AnyContext) return true;
            if (e.WhenMods != null && e.WhenMods.Length > 0)
            {
                bool ok = false;
                foreach (var m in e.WhenMods)
                {
                    if (string.Equals(m, ctx.ModPackageId, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                }
                if (!ok) return false;
            }
            if (e.WhenKey != null)
            {
                if (string.IsNullOrEmpty(ctx.Key) || ctx.Key.IndexOf(e.WhenKey, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private static bool SectionMatches(GlossaryEntry e, GlossaryContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.ModPackageId)) return false;
            foreach (var p in e.SectionPackages)
            {
                if (string.Equals(p, ctx.ModPackageId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// A noun that is also an English verb is skipped where it is used as a verb: after "to" or a modal verb, or as the
        /// first word of a sentence that goes on with an article or a pronoun ("Spot the enemy").
        /// </summary>
        private static bool GuardRejects(string s, List<Token> toks, int first, int last)
        {
            if (first >= 2 && toks[first - 1].Kind == TK.Space && toks[first - 2].Kind == TK.Word)
            {
                var prev = WordText(s, toks[first - 2]).ToLowerInvariant();
                if (VerbPrecursors.Contains(prev)) return true;
            }

            if (last + 2 < toks.Count && toks[last + 1].Kind == TK.Space && toks[last + 2].Kind == TK.Word)
            {
                var next = WordText(s, toks[last + 2]).ToLowerInvariant();
                if (VerbObjects.Contains(next) && AtSentenceStart(s, toks, first)) return true;
            }
            return false;
        }

        private static bool AtSentenceStart(string s, List<Token> toks, int first)
        {
            for (int i = first - 1; i >= 0; i--)
            {
                var t = toks[i];
                if (t.Kind == TK.Space || t.Kind == TK.Tag || t.Kind == TK.Esc) continue;
                if (t.Kind == TK.Break) return true;
                if (t.Kind == TK.Punct)
                {
                    char c = s[t.Start];
                    return c == '.' || c == '!' || c == '?' || c == ':' || c == ';';
                }
                return false;   // a word or a variable precedes
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------- signature

        /// <summary>
        /// Identifies the glossary data that influenced a text: the entries that matched it (and how). A cached translation
        /// is stale exactly when this value changes - an edited, removed or newly matching entry.
        /// </summary>
        internal static string SignatureOf(GlossaryScan scan)
        {
            if (scan == null || scan.Matches.Count == 0) return "0";
            ulong h = Fnv64.Start();
            foreach (var m in scan.Matches)
            {
                h = Fnv64.AddU64(h, m.Entry.Signature);
                h = Fnv64.AddField(h, ((int)m.Form).ToString());
                h = Fnv64.AddField(h, m.Definite ? "d" : (m.Indefinite ? "i" : ""));
            }
            return Fnv64.Hex(h);
        }

        // ---------------------------------------------------------------------------------------------- planning

        /// <summary>
        /// Builds the translation plan of a text: either the complete translation (the text is just a term), or the text
        /// with every term replaced by a {GLn} marker.
        /// </summary>
        internal GlossaryPlan Plan(string source, GlossaryContext ctx, bool allowMarkers)
        {
            var plan = new GlossaryPlan { Source = source };
            plan.Scan = Scan(source, ctx);
            var matches = plan.Scan.Matches;
            if (matches.Count == 0) return plan;

            if (matches.Count == 1 && matches[0].WholeText)
            {
                var m = matches[0];
                plan.Offline = true;
                plan.OfflineArabic = source.Substring(0, m.Start) + TermArabic(m) + source.Substring(m.End);
                return plan;
            }

            if (!allowMarkers || plan.Scan.MarkerCollision) return plan;

            // group the matches into units: two adjacent nouns form one compound
            int i = 0;
            while (i < matches.Count)
            {
                var a = matches[i];
                var unit = new GlossaryUnit();
                unit.Parts.Add(a);
                unit.Start = a.Start;
                unit.End = a.End;
                if (i + 1 < matches.Count)
                {
                    var b = matches[i + 1];
                    if (IsAdjacentCompound(plan.Source, a, b))
                    {
                        unit.Parts.Add(b);
                        unit.End = b.End;
                        i++;
                    }
                }
                i++;
                unit.Arabic = UnitArabic(unit);
                plan.Units.Add(unit);
            }

            var sb = new StringBuilder(source.Length + 8 * plan.Units.Count);
            int pos = 0;
            for (int u = 0; u < plan.Units.Count; u++)
            {
                var unit = plan.Units[u];
                sb.Append(source, pos, unit.Start - pos);
                sb.Append("{GL").Append(u).Append('}');
                pos = unit.End;
            }
            sb.Append(source, pos, source.Length - pos);
            plan.Marked = sb.ToString();
            return plan;
        }

        /// <summary>"Slag chunk": a modifier noun directly followed by its head noun (exactly one space between).</summary>
        private static bool IsAdjacentCompound(string source, GlossaryMatch a, GlossaryMatch b)
        {
            if (a.Entry.Mode != GlossaryMode.Any || b.Entry.Mode != GlossaryMode.Any) return false;
            if (a.Entry.Adjective || b.Entry.Adjective) return false;
            if (b.Definite || b.Indefinite) return false;
            if (b.Start - a.End < 1) return false;
            for (int i = a.End; i < b.Start; i++)
            {
                if (source[i] != ' ') return false;
            }
            return b.Start - a.End == 1;
        }

        /// <summary>The Arabic text of a single match, with the article its English text asks for.</summary>
        internal static string TermArabic(GlossaryMatch m)
        {
            if (m.Definite) return ArabicText.DefiniteForm(m.Entry, m.Form);
            if (m.Indefinite) return ArabicText.IndefiniteForm(m.Entry, m.Form);
            return m.Entry.FormText(m.Form);
        }

        private static string UnitArabic(GlossaryUnit unit)
        {
            if (unit.Parts.Count == 1) return TermArabic(unit.Parts[0]);

            // modifier (first English word) + head (second): Arabic puts the head first and the modifier after it
            var modifier = unit.Parts[0];
            var head = unit.Parts[1];
            var headText = ArabicText.IndefiniteForm(head.Entry, head.Form);
            string modText;
            if (modifier.Definite) modText = ArabicText.DefiniteForm(modifier.Entry, modifier.Form);
            else if (modifier.Indefinite) modText = ArabicText.IndefiniteForm(modifier.Entry, modifier.Form);
            else modText = modifier.Entry.FormText(modifier.Form);
            return headText + " " + modText;
        }

        // ---------------------------------------------------------------------------------------------- restoring

        /// <summary>
        /// Puts the approved Arabic text where the markers are. Returns false (and the unchanged text) when a marker is
        /// missing, duplicated or unknown: the translator did not keep it and the translation must not be used.
        /// </summary>
        internal static bool Restore(GlossaryPlan plan, string translated, out string restored)
        {
            restored = translated;
            if (plan == null || plan.Units.Count == 0) return true;
            if (translated == null) return false;

            var counts = new int[plan.Units.Count];
            var found = new List<Match>();
            for (var m = MarkerPattern.Match(translated); m.Success; m = m.NextMatch())
            {
                int idx;
                if (!int.TryParse(m.Groups[1].Value, out idx) || idx < 0 || idx >= counts.Length) return false;
                counts[idx]++;
                found.Add(m);
            }
            foreach (var n in counts)
            {
                if (n != 1) return false;
            }

            var sb = new StringBuilder(translated);
            for (int k = found.Count - 1; k >= 0; k--)
            {
                var m = found[k];
                int idx = int.Parse(m.Groups[1].Value);
                var arabic = plan.Units[idx].Arabic;
                int at = m.Index;
                int removeFrom = at;
                // لِـ + الـ -> للـ : the alef of the article is dropped after a prefixed lam
                if (ArabicText.StartsWithArticle(arabic) && at >= 1 && sb[at - 1] == ArabicText.Lam &&
                    (at == 1 || !ArabicText.IsLetter(sb[at - 2])))
                {
                    arabic = arabic.Substring(1);
                }
                sb.Remove(removeFrom, m.Length);
                sb.Insert(removeFrom, arabic);
            }
            restored = sb.ToString();
            return true;
        }

        /// <summary>True when the text still contains a glossary marker (a translator mangled or kept it).</summary>
        internal static bool HasMarker(string text)
        {
            return !string.IsNullOrEmpty(text) && MarkerPattern.IsMatch(text);
        }
    }
}
