using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// RULE 6 - Arabic pronoun variables and orthography.
    ///
    /// RimWorld fills pronoun variables ({PAWN_possessive}, [PAWN_objective], &lt;Prohis&gt;, &lt;Proher&gt;, ...) with suffix
    /// pronouns in Arabic, so the variable has to sit directly behind the Arabic word it belongs to:
    ///   * after a letter that connects to the next one (ب ت ث ج ح خ س ش ص ض ط ظ ع غ ف ق ك ل م ن ...): insert a kashida (tatweel):  كتابـ{PAWN_possessive}
    ///   * after a letter that does not connect (ا د ذ ر ز و ...): nothing:                                          شعر{PAWN_possessive}
    ///   * taa marbuta (ة) becomes an open taa plus kashida:                                                         قوة -> قوتـ{...}
    ///   * alef maksura (ى) becomes yaa plus kashida:                                                                على -> عليـ{...}
    /// The variables themselves are never changed, moved or removed; only the space in front of them goes.
    ///
    /// "Connects" is decided from the Unicode joining type (dual-joining letters take the kashida), which gives the 20
    /// letters of the rule and also ه ي ئ and the extended Arabic letters; "does not connect" are the right-joining
    /// letters (ا أ إ آ د ذ ر ز و ؤ ...).
    /// </summary>
    internal static class PronounRules
    {
        private static readonly Regex Variable = new Regex(@"\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|<[^<>\r\n]+>", RegexOptions.CultureInvariant);

        /// <summary>True when the variable stands for a pronoun that Arabic writes as a suffix of the previous word.</summary>
        internal static bool IsAttachable(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 3) return false;
            char open = token[0];
            var lower = token.Substring(1, token.Length - 2).Trim().ToLowerInvariant();
            if (open == '<')
            {
                if (!lower.StartsWith("pro", StringComparison.Ordinal)) return false;
                switch (lower.Substring(3))
                {
                    case "his":
                    case "her":
                    case "hers":
                    case "him":
                    case "himself":
                    case "herself":
                    case "their":
                    case "theirs":
                    case "them":
                    case "themself":
                    case "themselves":
                    case "its":
                    case "itself":
                        return true;
                    default:
                        return false;
                }
            }
            if (open == '{' || open == '[')
            {
                return lower.EndsWith("possessive", StringComparison.Ordinal) ||
                       lower.EndsWith("objective", StringComparison.Ordinal) ||
                       lower.EndsWith("reflexive", StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// Attaches every suffix-pronoun variable to the Arabic word before it and applies the kashida / ة / ى rules.
        /// Idempotent. <paramref name="changes"/> (optional) receives one line per change.
        /// </summary>
        internal static string Apply(string text, List<string> changes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('{') < 0 && text.IndexOf('[') < 0 && text.IndexOf('<') < 0) return text;

            var found = Variable.Matches(text);
            if (found.Count == 0) return text;

            StringBuilder sb = null;
            for (int k = found.Count - 1; k >= 0; k--)
            {
                var m = found[k];
                if (!IsAttachable(m.Value)) continue;
                if (sb == null) sb = new StringBuilder(text);
                AttachAt(sb, m.Index, m.Value, changes);
            }
            return sb == null ? text : sb.ToString();
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\u00A0';
        }

        private static void AttachAt(StringBuilder sb, int varStart, string variable, List<string> changes)
        {
            int p = varStart - 1;
            while (p >= 0 && IsSpace(sb[p])) p--;
            if (p < 0) return;
            if (!(ArabicText.IsLetter(sb[p]) || ArabicText.IsMark(sb[p]))) return;

            // the last base letter of the word (marks stand behind their letter)
            int q = p;
            while (q >= 0 && ArabicText.IsMark(sb[q])) q--;
            if (q < 0 || !ArabicText.IsLetter(sb[q])) return;

            int gap = varStart - 1 - p;
            char last = sb[q];
            string what = null;

            if (last == ArabicText.TaMarbuta)
            {
                sb[q] = ArabicText.Teh;
                what = "\u0629->\u062A\u0640";
            }
            else if (last == ArabicText.AlefMaksura)
            {
                sb[q] = ArabicText.Yeh;
                what = "\u0649->\u064A\u0640";
            }
            else if (last == ArabicText.Tatweel)
            {
                // already joined to the variable
            }
            else if (UnicodeData.JoinTypeOf(last) == JoinType.D)
            {
                what = "kashida after " + last;
            }

            bool needTatweel = what != null;
            if (gap > 0) sb.Remove(p + 1, gap);
            if (needTatweel) sb.Insert(p + 1, ArabicText.Tatweel);

            if (changes != null && (gap > 0 || needTatweel))
            {
                var parts = new List<string>();
                if (gap > 0) parts.Add("attached");
                if (what != null) parts.Add(what);
                changes.Add(variable + ": " + string.Join(", ", parts.ToArray()));
            }
        }

        /// <summary>
        /// Checks the rule on a finished text: returns null when every attachable variable that follows an Arabic word is
        /// attached correctly, otherwise a description of the first violation.
        /// </summary>
        internal static string Validate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var found = Variable.Matches(text);
            foreach (Match m in found)
            {
                if (!IsAttachable(m.Value)) continue;
                int p = m.Index - 1;
                if (p < 0) continue;
                if (IsSpace(text[p]))
                {
                    int q = p;
                    while (q >= 0 && IsSpace(text[q])) q--;
                    if (q >= 0 && (ArabicText.IsLetter(text[q]) || ArabicText.IsMark(text[q])))
                        return m.Value + " is separated from the Arabic word before it";
                    continue;
                }
                if (!(ArabicText.IsLetter(text[p]) || ArabicText.IsMark(text[p]))) continue;
                int b = p;
                while (b >= 0 && ArabicText.IsMark(text[b])) b--;
                if (b < 0 || !ArabicText.IsLetter(text[b])) continue;
                char last = text[b];
                if (last == ArabicText.Tatweel) continue;
                if (last == ArabicText.TaMarbuta) return m.Value + " follows \u0629 (must be \u062A\u0640)";
                if (last == ArabicText.AlefMaksura) return m.Value + " follows \u0649 (must be \u064A\u0640)";
                if (UnicodeData.JoinTypeOf(last) == JoinType.D) return m.Value + " follows a connecting letter without kashida";
            }
            return null;
        }

        /// <summary>The attachable variables of a text, in order (used to check that none was lost).</summary>
        internal static List<string> VariablesOf(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in Variable.Matches(text)) list.Add(m.Value);
            return list;
        }
    }
}
