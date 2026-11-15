using System;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Small, rule based helpers for Arabic words: letters, the definite article, orthography.
    /// Everything here works on logical (typing order) text.
    /// </summary>
    internal static class ArabicText
    {
        internal const char Alef = '\u0627';
        internal const char Lam = '\u0644';
        internal const char Tatweel = '\u0640';
        internal const char TaMarbuta = '\u0629';
        internal const char Teh = '\u062A';
        internal const char AlefMaksura = '\u0649';
        internal const char Yeh = '\u064A';
        internal const char FathaTanwin = '\u064B';
        internal const char Shadda = '\u0651';

        /// <summary>An Arabic-script letter (strong right-to-left, not a combining mark; tatweel counts).</summary>
        internal static bool IsLetter(int cp)
        {
            return UnicodeData.IsStrongRtl(cp) && !UnicodeData.IsMark(cp);
        }

        internal static bool IsLetter(char c)
        {
            return IsLetter((int)c);
        }

        /// <summary>A combining mark used with Arabic letters (vowel signs, shadda, superscript alef, ...).</summary>
        internal static bool IsMark(char c)
        {
            return UnicodeData.IsMark((int)c);
        }

        /// <summary>True when the text contains an Arabic-script letter.</summary>
        internal static bool ContainsArabic(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (IsLetter(s[i])) return true;
            }
            return false;
        }

        internal static bool IsSingleWord(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(' ') < 0;
        }

        internal static bool StartsWithArticle(string s)
        {
            return s != null && s.Length > 2 && s[0] == Alef && s[1] == Lam;
        }

        /// <summary>Removes the definite article ("المستعمر" -> "مستعمر").</summary>
        internal static string StripArticle(string s)
        {
            return StartsWithArticle(s) ? s.Substring(2) : s;
        }

        /// <summary>Adds the definite article ("مستعمر" -> "المستعمر") unless the word already has it.</summary>
        internal static string AddArticle(string s)
        {
            if (string.IsNullOrEmpty(s) || StartsWithArticle(s)) return s;
            return "\u0627\u0644" + s;
        }

        /// <summary>
        /// The approved term as it is written when the English text is definite ("the colonist"). Single words take the
        /// article; phrases are kept as approved (the article of a construct belongs to its last word and cannot be
        /// derived safely), unless the glossary gives the definite form explicitly.
        /// </summary>
        internal static string DefiniteForm(GlossaryEntry e, TermForm form)
        {
            if (form == TermForm.Singular && e.ArabicDefinite != null) return e.ArabicDefinite;
            var t = e.FormText(form);
            return IsSingleWord(t) ? AddArticle(t) : t;
        }

        /// <summary>The approved term as it is written when the English text is indefinite ("a colonist").</summary>
        internal static string IndefiniteForm(GlossaryEntry e, TermForm form)
        {
            if (form == TermForm.Singular && e.ArabicIndefinite != null) return e.ArabicIndefinite;
            var t = e.FormText(form);
            return IsSingleWord(t) ? StripArticle(t) : t;
        }

        /// <summary>
        /// The letters of an Arabic text for comparisons: no vowel marks or tatweel, the alef variants folded into a
        /// plain alef and alef maksura read as yaa.
        /// </summary>
        internal static string Skeleton(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (IsMark(c) || c == Tatweel) continue;
                if (c == '\u0623' || c == '\u0625' || c == '\u0622' || c == '\u0671') c = Alef;
                else if (c == AlefMaksura) c = Yeh;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsClitic(char c)
        {
            // prefixes that may stand in front of a noun: wa, fa, bi, ka, li and the alef / lam of the article
            return c == '\u0648' || c == '\u0641' || c == '\u0628' || c == '\u0643' || c == Lam || c == Alef;
        }

        /// <summary>
        /// True when <paramref name="text"/> contains the Arabic term (any vowel marks, with or without the article, with
        /// the usual one-letter prefixes). Used to check that a translator's own wording did not replace an approved term.
        /// </summary>
        internal static bool ContainsTerm(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return false;
            var hay = Skeleton(text);
            var stem = StripArticle(Skeleton(term).Trim());
            if (stem.Length == 0) return false;

            int from = 0;
            while (from <= hay.Length - stem.Length)
            {
                int idx = hay.IndexOf(stem, from, StringComparison.Ordinal);
                if (idx < 0) return false;
                // the term has to start a word, possibly behind up to four prefix letters (wa + bi + al ...)
                for (int k = 0; k <= 4 && idx - k >= 0; k++)
                {
                    int p = idx - k;
                    if (p == 0 || !IsLetter(hay[p - 1])) return true;
                    if (!IsClitic(hay[p - 1])) break;
                }
                from = idx + 1;
            }
            return false;
        }

        private const char ArabicQuestionMark = '\u061F';

        /// <summary>
        /// "?" becomes the Arabic question mark in a text that has Arabic letters. Placeholders, tags and switch blocks
        /// ({KEY ? a : b}) keep their "?", and so does the "?" of a Latin word, as in "(Ready?)", and a "?" between a
        /// digit and a letter or digit (an address like page2?x=1).
        /// </summary>
        internal static string ArabicQuestionMarks(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('?') < 0 || !ContainsArabic(s)) return s;

            var sb = new StringBuilder(s.Length);
            int pos = 0;
            for (var m = Placeholders.Span.Match(s); m.Success; m = m.NextMatch())
            {
                AppendQuestionMarks(sb, s, pos, m.Index);
                sb.Append(m.Value);
                pos = m.Index + m.Length;
            }
            AppendQuestionMarks(sb, s, pos, s.Length);

            var result = sb.ToString();
            return string.Equals(result, s, StringComparison.Ordinal) ? s : result;
        }

        private static void AppendQuestionMarks(StringBuilder sb, string s, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                char c = s[i];
                sb.Append(c == '?' && !StaysQuestionMark(s, i) ? ArabicQuestionMark : c);
            }
        }

        /// <summary>A "?" that belongs to Latin text: it ends a Latin word, or sits in an address (digit, "?", letter or digit).</summary>
        private static bool StaysQuestionMark(string s, int i)
        {
            if (i == 0) return false;
            char before = s[i - 1];
            if (IsAsciiLetter(before)) return true;
            return before >= '0' && before <= '9' && i + 1 < s.Length && IsAsciiAlphanumeric(s[i + 1]);
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsAsciiAlphanumeric(char c)
        {
            return (c >= '0' && c <= '9') || IsAsciiLetter(c);
        }

        private const char ArabicComma = '\u060C';
        private const char ArabicSemicolon = '\u061B';

        /// <summary>
        /// Removes the spaces before the Arabic comma, semicolon and question mark, and before a colon that follows an Arabic
        /// letter: a translator often writes them the English way ("word , word"), which is wrong in Arabic. Placeholders, tags
        /// and switch blocks are not touched, and neither is the start of a line.
        /// </summary>
        internal static string TightenPunctuation(string s)
        {
            if (string.IsNullOrEmpty(s) || !ContainsArabic(s)) return s;
            if (s.IndexOf(' ') < 0 && s.IndexOf('\t') < 0) return s;

            var sb = new StringBuilder(s.Length);
            int pos = 0;
            for (var m = Placeholders.Span.Match(s); m.Success; m = m.NextMatch())
            {
                AppendTight(sb, s, pos, m.Index);
                sb.Append(m.Value);
                pos = m.Index + m.Length;
            }
            AppendTight(sb, s, pos, s.Length);

            var result = sb.ToString();
            return string.Equals(result, s, StringComparison.Ordinal) ? s : result;
        }

        private static void AppendTight(StringBuilder sb, string s, int from, int to)
        {
            int i = from;
            while (i < to)
            {
                char c = s[i];
                if (c != ' ' && c != '\t')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                int j = i;
                while (j < to && (s[j] == ' ' || s[j] == '\t')) j++;
                bool drop = false;
                if (j < to && sb.Length > 0)
                {
                    char before = sb[sb.Length - 1];
                    char next = s[j];
                    if (before != '\n' && before != '\r')
                    {
                        if (next == ArabicComma || next == ArabicSemicolon || next == ArabicQuestionMark) drop = true;
                        else if (next == ':' && IsLetter(before)) drop = true;
                    }
                }
                if (!drop) sb.Append(s, i, j - i);
                i = j;
            }
        }

        /// <summary>
        /// Moves a fathatan that stands on the letter before a final alef to after the alef:
        /// "أيضًا" -> "أيضاً", "جدًّا" -> "جدّاً". Bare "أيضا" becomes "أيضاً" as well (the required spelling).
        /// </summary>
        internal static string NormalizeOrthography(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = ArabicQuestionMarks(s);
            s = TightenPunctuation(s);
            if (s.IndexOf(FathaTanwin) < 0 && s.IndexOf("\u0623\u064A\u0636\u0627", StringComparison.Ordinal) < 0) return s;

            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (!IsLetter(s[i]) && !IsMark(s[i]))
                {
                    sb.Append(s[i]);
                    i++;
                    continue;
                }

                // one word: a run of letters and marks
                int j = i;
                while (j < s.Length && (IsLetter(s[j]) || IsMark(s[j]))) j++;
                sb.Append(NormalizeWord(s.Substring(i, j - i)));
                i = j;
            }
            return sb.ToString();
        }

        private static string NormalizeWord(string w)
        {
            // fathatan before a final alef: move it behind the alef
            if (w.Length >= 2 && w[w.Length - 1] == Alef)
            {
                int tanwin = w.LastIndexOf(FathaTanwin, w.Length - 2);
                if (tanwin >= 1)
                {
                    // the fathatan must belong to the cluster right before the alef (letter + marks)
                    bool clusterOnly = true;
                    for (int k = tanwin + 1; k < w.Length - 1; k++)
                    {
                        if (!IsMark(w[k])) { clusterOnly = false; break; }
                    }
                    if (clusterOnly)
                    {
                        var rest = w.Remove(tanwin, 1);
                        return rest + FathaTanwin;
                    }
                }
            }

            // "أيضا" (optionally with the و / ف prefix) always carries its fathatan behind the alef
            if (w == "\u0623\u064A\u0636\u0627" || w == "\u0648\u0623\u064A\u0636\u0627" || w == "\u0641\u0623\u064A\u0636\u0627")
                return w + FathaTanwin;

            return w;
        }
    }
}
