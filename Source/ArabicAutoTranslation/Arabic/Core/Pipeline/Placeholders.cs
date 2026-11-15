using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// The spans every translator protects (rich-text tags, {..}, [..] and the escapes \n \r \t) and the check that a
    /// processed text still carries exactly the placeholders of its source: none lost, none invented, none changed.
    /// </summary>
    internal static class Placeholders
    {
        private const string SpanPattern = @"<[^>]+>|\{[^\}]+\}|\[[^\]]+\]|\\[nrt]";

        // the same spans Helpers.ProtectPlaceholders protects
        internal static readonly Regex Span = new Regex(SpanPattern, RegexOptions.CultureInvariant);

        /// <summary>
        /// A multiplier sign in front of its number: "x2", "x{0}". A translator reads the letter as a word of its own (Yandex
        /// wrote the Arabic letter for s, or the spelled-out name of the letter x), so the sign travels with its number
        /// whenever a translator is given a text. It is not a placeholder of its own (<see cref="Span"/> does not match it):
        /// the number is the placeholder, and a "x" between two numbers or after a word ("3x3", "{0}x{1}", "box2") is left alone.
        /// </summary>
        internal const string MultiplierPattern = @"(?<![A-Za-z0-9}\])])x(?:\{[^\}]+\}|[0-9]+(?:\.[0-9]+)?)";

        /// <summary>
        /// A multiplier sign behind its number: "4x", "2.5x" (Yandex answered "8x" with the Arabic letter for s, but "4x" with
        /// the word for times). Not "3x3", "20x20", "5xp" or the end of a version such as "v1.5x".
        /// </summary>
        internal const string NumberTimesPattern = @"(?<![A-Za-z0-9.}\])])[0-9]+(?:\.[0-9]+)?x(?![A-Za-z0-9])";

        /// <summary>
        /// A tier numeral at the end of a text ("Dragon scales II", "Nirvana IV", "Keen senses I"). A translator answered "II"
        /// with the Arabic for "secondly" and read a single "I" as the pronoun, so the numeral travels with the text like a
        /// multiplier sign and stays Latin. A one-letter numeral counts after a short run of words ("Keen senses I") or as a
        /// text of its own ("I"), never behind a word that makes it the pronoun ("so do I", "and I"). Not a placeholder of
        /// its own (<see cref="Span"/> does not match it).
        /// </summary>
        internal const string RomanTierPattern =
            @"(?<![A-Za-z0-9])(?:VIII|VII|VI|IV|IX|III|II)(?![A-Za-z0-9])(?=[.:!]?[ \t]*\z)" +
            @"|(?<=\A[A-Za-z' \-]{1,40} )(?<!\b(?:do|does|did|am|and|or|so|nor|as|than|that|what|who|how|if|can|will|could|would|should|have|had|was|were|but|because|when|while|like) )" +
            @"[IVX](?=[.:!]?[ \t]*\z)" +
            @"|\A[IVX](?=[.:!]?[ \t]*\z)";

        /// <summary>What a translator is protected from: the spans of <see cref="Span"/>, the multiplier signs and a tier numeral.</summary>
        internal static readonly Regex WireSpan = new Regex(
            SpanPattern + "|" + MultiplierPattern + "|" + NumberTimesPattern + "|" + RomanTierPattern, RegexOptions.CultureInvariant);

        private static readonly Regex AnyRomanTier = new Regex(RomanTierPattern, RegexOptions.CultureInvariant);

        /// <summary>True when a text ends with a tier numeral ("Dragon scales II").</summary>
        internal static bool HasRomanTier(string text)
        {
            return !string.IsNullOrEmpty(text) && AnyRomanTier.IsMatch(text);
        }

        private static readonly Regex AnyMultiplier = new Regex(
            MultiplierPattern + "|" + NumberTimesPattern, RegexOptions.CultureInvariant);

        /// <summary>True when a text holds a multiplier sign ("x2", "x{0}", "4x").</summary>
        internal static bool HasMultiplier(string text)
        {
            return !string.IsNullOrEmpty(text) && AnyMultiplier.IsMatch(text);
        }

        // "__PH3__" is what Helpers.ProtectPlaceholders sends in place of a placeholder. A translator may not leave it as it
        // is: Yandex turned the letters into a word of its own (PH read as the pH of chemistry, or as a letter), spaced the
        // underscores out ("_ _") or wrote the digits in another script. Only the underscores and the number are relied on.
        private static readonly Regex ProtectedToken = new Regex(
            @"[_\u2017\uFF3F](?:[\s\u200E\u200F\u061C]*[_\u2017\uFF3F])?" +
            @"[^0-9\u0660-\u0669\u06F0-\u06F9_\u2017\uFF3F]{0,30}?" +
            @"([0-9\u0660-\u0669\u06F0-\u06F9]{1,4})" +
            @"[\s\u200E\u200F\u061C]*[_\u2017\uFF3F](?:[\s\u200E\u200F\u061C]*[_\u2017\uFF3F])?",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Puts the protective tokens of a translation back into the form they were sent in ("__PH3__"), so that the
        /// placeholder they stand for can be restored. <paramref name="isSentToken"/> says whether a token was sent at
        /// all: a number that was not sent leaves the text as it is.
        /// </summary>
        internal static string NormalizeProtectedTokens(string text, Func<string, bool> isSentToken)
        {
            if (string.IsNullOrEmpty(text) || isSentToken == null) return text;
            return ProtectedToken.Replace(text, delegate (Match m)
            {
                int n = 0;
                foreach (char c in m.Groups[1].Value) n = n * 10 + DigitValue(c);
                string token = "__PH" + n + "__";
                return isSentToken(token) ? token : m.Value;
            });
        }

        private static int DigitValue(char c)
        {
            if (c >= '\u0660' && c <= '\u0669') return c - '\u0660';
            if (c >= '\u06F0' && c <= '\u06F9') return c - '\u06F0';
            return c - '0';
        }

        // ---------------------------------------------------------------------------------------- numbered tokens

        // "{3}" is the protective token for a translator that cannot carry "__PH3__" through a sentence (Yandex reads the
        // letters PH as a word and tears the underscores away). A translator may still write the braces the other way
        // round (in right-to-left text) or full-width, space them from the number, or write the number in another script.
        private static readonly Regex NumberedToken = new Regex(
            @"[\{\}\uFF5B\uFF5D][\s\u200E\u200F\u061C]*([0-9\u0660-\u0669\u06F0-\u06F9]{1,4})[\s\u200E\u200F\u061C]*[\{\}\uFF5B\uFF5D]",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces every placeholder of a text (the spans of <see cref="WireSpan"/>) with its number in braces, "{0}", "{1}",
        /// ..., and lists the placeholders in that order. Unlike a token with letters in it, a number gives a translator
        /// nothing to translate.
        /// </summary>
        internal static string ProtectNumbered(string text, out List<string> originals)
        {
            var list = new List<string>();
            originals = list;
            if (string.IsNullOrEmpty(text)) return text;
            return WireSpan.Replace(text, delegate (Match m)
            {
                list.Add(m.Value);
                return "{" + (list.Count - 1) + "}";
            });
        }

        /// <summary>
        /// Puts the placeholders back for their numbers, wherever the translator moved them. The text is read once, so a
        /// placeholder that looks like a number in braces is not replaced a second time. False when a number was lost or
        /// when a number that was never sent appears; the text is then left as it came.
        /// </summary>
        internal static bool TryRestoreNumbered(string translated, IList<string> originals, out string restored)
        {
            restored = translated;
            if (string.IsNullOrEmpty(translated) || originals == null || originals.Count == 0) return true;

            var found = new bool[originals.Count];
            bool invented = false;
            string result = NumberedToken.Replace(translated, delegate (Match m)
            {
                int n = 0;
                foreach (char c in m.Groups[1].Value) n = n * 10 + DigitValue(c);
                if (n >= originals.Count)
                {
                    invented = true;
                    return m.Value;
                }
                found[n] = true;
                return originals[n];
            });

            if (invented) return false;
            foreach (bool f in found)
            {
                if (!f) return false;
            }
            restored = result;
            return true;
        }

        /// <summary>
        /// True when a text has exactly the placeholders of <paramref name="expected"/> (spans of <see cref="Span"/>, in any
        /// order, each as often as it is listed). A translator can leave a stray brace next to a restored placeholder: "{{1}"
        /// is then a span of its own and the placeholder that was meant is gone. An entry of the list can hold a placeholder
        /// in front of which a multiplier sign went along ("x{0}"): what counts is the placeholder.
        /// </summary>
        internal static bool HasExactly(string text, IList<string> expected)
        {
            var actual = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                for (var m = Span.Match(text); m.Success; m = m.NextMatch()) actual.Add(m.Value);
            }
            var wanted = new List<string>();
            if (expected != null)
            {
                foreach (var original in expected)
                {
                    if (string.IsNullOrEmpty(original)) continue;
                    for (var m = Span.Match(original); m.Success; m = m.NextMatch()) wanted.Add(m.Value);
                }
            }
            return Difference(wanted, actual, Mode.Exact) == null;
        }

        /// <summary>How strictly two placeholder lists are compared.</summary>
        internal enum Mode
        {
            /// <summary>Every token the same number of times.</summary>
            Exact,
            /// <summary>Like Exact, but a tag only has to appear (the display stage repeats the tags of a span at each line break).</summary>
            TagsAsSet,
            /// <summary>The actual text may lack tokens but must not have any the expected text does not have.</summary>
            ActualWithinExpected
        }

        /// <summary>
        /// The tokens of a text in order. A real line break or tab counts as the escape that stands for it ("\n"), so a
        /// translator that turns the two characters into the character (or the other way round) changes nothing.
        /// </summary>
        internal static List<string> Of(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            int pos = 0;
            for (var m = Span.Match(text); m.Success; m = m.NextMatch())
            {
                AddBreaks(text, pos, m.Index, list);
                list.Add(m.Value);
                pos = m.Index + m.Length;
            }
            AddBreaks(text, pos, text.Length, list);
            return list;
        }

        private static void AddBreaks(string text, int from, int to, List<string> list)
        {
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < to && text[i + 1] == '\n')
                    {
                        list.Add("\\n");
                        i++;
                    }
                    else
                    {
                        list.Add("\\r");
                    }
                }
                else if (c == '\n')
                {
                    list.Add("\\n");
                }
                else if (c == '\t')
                {
                    list.Add("\\t");
                }
            }
        }

        internal static bool IsTag(string token)
        {
            return token != null && token.Length > 2 && token[0] == '<';
        }

        /// <summary>Null when the two lists agree, otherwise a short description of the first difference.</summary>
        internal static string Difference(List<string> expected, List<string> actual, Mode mode)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            var expectedTags = new HashSet<string>(StringComparer.Ordinal);
            var actualTags = new HashSet<string>(StringComparer.Ordinal);
            bool tagsAsSet = mode == Mode.TagsAsSet;

            foreach (var t in expected)
            {
                if (tagsAsSet && IsTag(t))
                {
                    expectedTags.Add(t);
                    continue;
                }
                int n;
                if (!count.TryGetValue(t, out n)) order.Add(t);
                count[t] = n + 1;
            }
            foreach (var t in actual)
            {
                if (tagsAsSet && IsTag(t))
                {
                    actualTags.Add(t);
                    continue;
                }
                int n;
                if (!count.TryGetValue(t, out n)) order.Add(t);
                count[t] = n - 1;
            }

            foreach (var t in order)
            {
                int n = count[t];
                if (n > 0 && mode != Mode.ActualWithinExpected)
                    return "missing " + Show(t) + (n > 1 ? " (x" + n + ")" : "");
                if (n < 0)
                    return "unexpected " + Show(t) + (n < -1 ? " (x" + (-n) + ")" : "");
            }

            if (tagsAsSet)
            {
                foreach (var t in expectedTags)
                {
                    if (!actualTags.Contains(t)) return "missing " + Show(t);
                }
                foreach (var t in actualTags)
                {
                    if (!expectedTags.Contains(t)) return "unexpected " + Show(t);
                }
            }
            return null;
        }

        /// <summary>Compares the placeholders of two texts.</summary>
        internal static string Check(string expectedText, string actualText, Mode mode)
        {
            return Difference(Of(expectedText), Of(actualText), mode);
        }

        private static string Show(string token)
        {
            return token == "\n" ? "\\n" : token;
        }
    }
}
