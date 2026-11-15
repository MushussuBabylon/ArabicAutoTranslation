using System;
using System.Collections.Generic;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>Placeholder comparison, text segmentation for the translator, Arabic orthography and word helpers.</summary>
    internal static class TextTests
    {
        internal static void Run()
        {
            T.Group("placeholders");
            PlaceholderTokens();
            PlaceholderComparison();
            ProtectedTokens();
            NumberedTokens();
            NumberedCheck();
            MultiplierSigns();
            TierNumerals();

            T.Group("text segmentation");
            Segmentation();

            T.Group("arabic orthography and words");
            Orthography();
            Words();
        }

        // ------------------------------------------------------------------------------------------ placeholders

        private static string Join(List<string> list)
        {
            return string.Join("|", list.ToArray());
        }

        private static void PlaceholderTokens()
        {
            T.Equal("{0}|<b>|</b>|[PAWN_nameDef]|\\n", Join(Placeholders.Of("a {0} <b>x</b> [PAWN_nameDef] \\n")), "tokens in order");
            T.Equal("\\n", Join(Placeholders.Of("a\nb")), "a real line feed counts as the escape \\n");
            T.Equal("\\n", Join(Placeholders.Of("a\r\nb")), "CR LF counts as one \\n");
            T.Equal("\\r", Join(Placeholders.Of("a\rb")), "a lone carriage return counts as \\r");
            T.Equal("\\t", Join(Placeholders.Of("a\tb")), "a tab counts as \\t");
            T.Equal("", Join(Placeholders.Of("plain text")), "no tokens in plain text");
            T.Equal("", Join(Placeholders.Of("")), "no tokens in the empty text");
            T.Equal("", Join(Placeholders.Of(null)), "no tokens in null");
            T.Equal("{0}|{0}|{1}", Join(Placeholders.Of("{0} {0} {1}")), "repeated tokens are all listed");
            T.Equal("<Prohis>", Join(Placeholders.Of("x <Prohis> y")), "pronoun tags are tokens");
            T.Equal("<color=#ff0000>|</color>", Join(Placeholders.Of("<color=#ff0000>x</color>")), "color tags are tokens");
            T.True(Placeholders.IsTag("<b>") && Placeholders.IsTag("</b>") && Placeholders.IsTag("<color=#ff0000>"), "tags are recognized");
            T.False(Placeholders.IsTag("{0}") || Placeholders.IsTag("[x]") || Placeholders.IsTag("\\n") || Placeholders.IsTag(null), "other tokens are not tags");
        }

        private static List<string> L(params string[] tokens)
        {
            return new List<string>(tokens);
        }

        private static void PlaceholderComparison()
        {
            var exact = Placeholders.Mode.Exact;
            T.Null(Placeholders.Difference(L("{0}", "{1}"), L("{0}", "{1}"), exact), "equal lists");
            T.Null(Placeholders.Difference(L("{0}", "{1}"), L("{1}", "{0}"), exact), "the order of tokens does not matter (Arabic reorders)");
            T.Equal("missing {1}", Placeholders.Difference(L("{0}", "{1}"), L("{0}"), exact), "a lost placeholder");
            T.Equal("unexpected {2}", Placeholders.Difference(L("{0}"), L("{0}", "{2}"), exact), "an invented placeholder");
            T.Equal("missing {0}", Placeholders.Difference(L("{0}", "{0}"), L("{0}"), exact), "one of two equal placeholders lost");
            T.Equal("missing {0} (x2)", Placeholders.Difference(L("{0}", "{0}", "{0}"), L("{0}"), exact), "two of three equal placeholders lost");
            T.Equal("unexpected {0}", Placeholders.Difference(L("{0}"), L("{0}", "{0}"), exact), "a duplicated placeholder");
            T.Null(Placeholders.Difference(L(), L(), exact), "two empty lists");

            // exact mode treats tags like every other token
            T.Equal("missing </b>", Placeholders.Difference(L("<b>", "</b>"), L("<b>"), exact), "a lost closing tag");

            // the display stage repeats the tags of a span at each line break: tags only have to be there
            var set = Placeholders.Mode.TagsAsSet;
            T.Null(Placeholders.Difference(L("<b>", "{0}", "</b>"), L("<b>", "{0}", "<b>", "</b>", "</b>"), set), "repeated tags are fine");
            T.Equal("missing <b>", Placeholders.Difference(L("<b>", "{0}"), L("{0}"), set), "a lost tag is still reported");
            T.Equal("unexpected <i>", Placeholders.Difference(L("<b>"), L("<b>", "<i>"), set), "an invented tag is still reported");
            T.Equal("missing {0}", Placeholders.Difference(L("<b>", "{0}", "{0}"), L("<b>", "{0}"), set), "other tokens are still counted");

            // one-sided mode (the source is only approximately known): tokens may be missing, never invented
            var within = Placeholders.Mode.ActualWithinExpected;
            T.Null(Placeholders.Difference(L("{0}", "{1}"), L("{0}"), within), "a missing token is allowed");
            T.Null(Placeholders.Difference(L("{0}", "{1}"), L(), within), "all tokens missing is allowed");
            T.Equal("unexpected {2}", Placeholders.Difference(L("{0}", "{1}"), L("{0}", "{2}"), within), "an invented token is not allowed");
            T.Equal("unexpected {0}", Placeholders.Difference(L("{0}"), L("{0}", "{0}"), within), "a duplicated token is not allowed");

            // texts
            T.Null(Placeholders.Check("Built {0}!", "\u062A\u0645 \u0628\u0646\u0627\u0621 {0}!", exact), "text check: placeholder kept");
            T.Equal("missing {0}", Placeholders.Check("Built {0}!", "\u062A\u0645 \u0628\u0646\u0627\u0621", exact), "text check: placeholder lost");
            T.Null(Placeholders.Check("One\\nTwo", "\u0648\u0627\u062D\u062F\n\u0627\u062B\u0646\u0627\u0646", exact), "text check: the escape \\n may become a real line feed");
            T.Null(Placeholders.Check("One\nTwo", "\u0648\u0627\u062D\u062F\\n\u0627\u062B\u0646\u0627\u0646", exact), "text check: a real line feed may become the escape \\n");
        }

        // ------------------------------------------------------------------------------------------ protected tokens

        private static Func<string, bool> Sent(params string[] tokens)
        {
            var set = new HashSet<string>(tokens);
            return delegate (string token) { return set.Contains(token); };
        }

        private static void ProtectedTokens()
        {
            var zero = Sent("__PH0__");
            var three = Sent("__PH0__", "__PH1__", "__PH2__");

            // what Yandex answered (Player.log) to texts that carried __PH0__: it translated the letters PH (as the pH of
            // chemistry, or as a letter) and sometimes spaced the underscores out
            T.Equal("\u0644\u063A\u0627\u063A\u0627\u0631\u064A\u0646 \u0641\u064A:__PH0__ \u0623\u064A\u0627\u0645", Placeholders.NormalizeProtectedTokens("\u0644\u063A\u0627\u063A\u0627\u0631\u064A\u0646 \u0641\u064A:__ \u0627\u0644\u0631\u0642\u0645 \u0627\u0644\u0647\u064A\u062F\u0631\u0648\u062C\u064A\u0646\u064A 0 _ _ \u0623\u064A\u0627\u0645", zero), "the pH of chemistry, underscores spaced out");
            T.Equal("\u0641\u064A \u062D\u0627\u0644\u0629 \u062A\u062D\u062F\u064A\u062B__PH0__.", Placeholders.NormalizeProtectedTokens("\u0641\u064A \u062D\u0627\u0644\u0629 \u062A\u062D\u062F\u064A\u062B__ \u0641 0__.", zero), "a letter instead of PH");
            T.Equal("\u0645\u0646 \u0627\u0643\u062A\u0634\u0627\u0641 \u062A\u063A\u064A\u064A\u0631\u0627\u062A__PH0__.", Placeholders.NormalizeProtectedTokens("\u0645\u0646 \u0627\u0643\u062A\u0634\u0627\u0641 \u062A\u063A\u064A\u064A\u0631\u0627\u062A__ \u0627\u0644\u0631\u0642\u0645 \u0627\u0644\u0647\u064A\u062F\u0631\u0648\u062C\u064A\u0646\u064A 0__.", zero), "the pH of chemistry, underscores together");

            // tokens that came back as they were stay as they are
            T.Equal("a __PH0__ b __PH1__ c", Placeholders.NormalizeProtectedTokens("a __PH0__ b __PH1__ c", three), "tokens that came back untouched");
            T.Equal("__PH0____PH1__", Placeholders.NormalizeProtectedTokens("__PH0____PH1__", three), "two tokens side by side");
            T.Equal("__PH0__ __PH1__", Placeholders.NormalizeProtectedTokens("__PH0__ __PH1__", three), "two tokens with a space between");

            // other ways a translator may write a token
            T.Equal("x __PH1__ y", Placeholders.NormalizeProtectedTokens("x _ _ ph 1 _ _ y", three), "lower case and spaced out");
            T.Equal("x __PH1__ y", Placeholders.NormalizeProtectedTokens("x __PH\u0661__ y", three), "an Arabic-Indic digit");
            T.Equal("x __PH2__ y", Placeholders.NormalizeProtectedTokens("x __PH\u06F2__ y", three), "an Eastern Arabic-Indic digit");
            T.Equal("x __PH2__ y", Placeholders.NormalizeProtectedTokens("x \u2017\u2017PH2\u2017\u2017 y", three), "other underscores");
            T.Equal("x __PH2__ y", Placeholders.NormalizeProtectedTokens("x __\u200F PH 2 \u200E__ y", three), "direction marks");
            T.Equal("x __PH11__ y", Placeholders.NormalizeProtectedTokens("x _ _ \u0641 11 _ _ y", Sent("__PH11__")), "two digits");
            T.Equal("x __PH0__", Placeholders.NormalizeProtectedTokens("x __0__", three), "the letters gone");

            // two torn tokens keep the space between them, and the number says which is which
            T.Equal("__PH0__ __PH1__", Placeholders.NormalizeProtectedTokens("__ \u0641 0__ __ \u0641 1__", three), "two torn tokens");
            T.Equal("__PH1__ \u0648 __PH0__", Placeholders.NormalizeProtectedTokens("__ \u0641 1__ \u0648 __ \u0641 0__", three), "torn tokens in another order");

            // what must stay as it is
            T.Equal("x __PH7__ y", Placeholders.NormalizeProtectedTokens("x __PH7__ y", three), "a number that was not sent");
            T.Equal("x __ \u0641 7 __ y", Placeholders.NormalizeProtectedTokens("x __ \u0641 7 __ y", three), "a torn token with a number that was not sent");
            T.Equal("snake_case_name", Placeholders.NormalizeProtectedTokens("snake_case_name", three), "underscores without a number");
            T.Equal("\u0639\u062F\u062F 0", Placeholders.NormalizeProtectedTokens("\u0639\u062F\u062F 0", three), "a number without underscores");
            T.Equal("__" + new string('x', 31) + "0__", Placeholders.NormalizeProtectedTokens("__" + new string('x', 31) + "0__", three), "more than 30 characters between the underscores and the number");
            T.Equal("__PH0__", Placeholders.NormalizeProtectedTokens("__" + new string('x', 30) + "0__", three), "30 characters between still count");
            T.Null(Placeholders.NormalizeProtectedTokens(null, three), "null stays null");
            T.Equal("", Placeholders.NormalizeProtectedTokens("", three), "the empty text stays empty");
            T.Equal("__ x 0 __", Placeholders.NormalizeProtectedTokens("__ x 0 __", null), "without the list of sent tokens nothing changes");
        }

        // ------------------------------------------------------------------------------------------ numbered tokens

        private static string Restored(string translated, params string[] originals)
        {
            string restored;
            return Placeholders.TryRestoreNumbered(translated, originals, out restored) ? restored : null;
        }

        private static string RoundTrip(string source)
        {
            List<string> originals;
            string protectedText = Placeholders.ProtectNumbered(source, out originals);
            return Restored(protectedText, originals.ToArray());
        }

        private static void NumberedTokens()
        {
            List<string> originals;

            // protecting: every placeholder is replaced by its number
            T.Equal("{0} was found {1}alone{2}.", Placeholders.ProtectNumbered("{PAWN_nameDef} was found <b>alone</b>.", out originals), "placeholders become numbers, in the order of the text");
            T.Equal("{PAWN_nameDef}|<b>|</b>", Join(originals), "placeholders become numbers, in the order of the text: the list of placeholders");
            T.Equal("a{0}b", Placeholders.ProtectNumbered("a\\nb", out originals), "the escape \\n is a placeholder");
            T.Equal("\\n", Join(originals), "the escape \\n is a placeholder: the list of placeholders");
            T.Equal("a\u000Ab", Placeholders.ProtectNumbered("a\u000Ab", out originals), "a real line feed is not");
            T.Equal("", Join(originals), "a real line feed is not: the list of placeholders");
            T.Equal("{0} d", Placeholders.ProtectNumbered("[a <b> c] d", out originals), "a span inside another one is a single placeholder");
            T.Equal("[a <b> c]", Join(originals), "a span inside another one is a single placeholder: the list of placeholders");
            T.Equal("{0} and {1}", Placeholders.ProtectNumbered("{1} and {0}", out originals), "numbers of the game get numbers of their own");
            T.Equal("{1}|{0}", Join(originals), "numbers of the game get numbers of their own: the list of placeholders");
            T.Equal("plain text", Placeholders.ProtectNumbered("plain text", out originals), "a text without placeholders stays as it is");
            T.Equal("", Join(originals), "a text without placeholders stays as it is: the list of placeholders");
            T.Equal("", Placeholders.ProtectNumbered("", out originals), "the empty text");
            T.Equal("", Join(originals), "the empty text: the list of placeholders");
            T.Null(Placeholders.ProtectNumbered(null, out originals), "null stays null");
            T.Equal(0, originals.Count, "null has no placeholders");

            // restoring: the placeholders come back for their numbers, wherever they stand
            T.Equal("{PAWN_nameDef} was found <b>alone</b>.", Restored("{0} was found {1}alone{2}.", "{PAWN_nameDef}", "<b>", "</b>"), "tokens that came back untouched");
            T.Equal("\u062A\u0645 \u0627\u0644\u0639\u062B\u0648\u0631 \u0639\u0644\u0649 {PAWN_nameDef} \u0643\u0637\u0641\u0644 \u0631\u0636\u064A\u0639", Restored("\u062A\u0645 \u0627\u0644\u0639\u062B\u0648\u0631 \u0639\u0644\u0649 {0} \u0643\u0637\u0641\u0644 \u0631\u0636\u064A\u0639", "{PAWN_nameDef}"), "a token the translator moved to another place in the sentence");
            T.Equal("<b>x</b>", Restored("{0}x{1}", "<b>", "</b>"), "tags around a word");
            T.Equal("a {X} b <i>", Restored("a { 0 } b {1}", "{X}", "<i>"), "braces spaced from the number");
            T.Equal("a {X} b <i>", Restored("a }0{ b }1{", "{X}", "<i>"), "braces the other way round");
            T.Equal("a {X}", Restored("a \uFF5B0\uFF5D", "{X}"), "full-width braces");
            T.Equal("a {X} b <i>", Restored("a {\u0660} b {\u0661}", "{X}", "<i>"), "Arabic-Indic digits");
            T.Equal("<i> <j>", Restored("{\u06F0} {\u06F1}", "<i>", "<j>"), "Eastern Arabic-Indic digits");
            T.Equal("a {X}", Restored("a {\u200F0\u200E}", "{X}"), "direction marks inside the braces");
            T.Equal("<i><j>", Restored("{0}{1}", "<i>", "<j>"), "two tokens side by side");
            T.Equal("<j> \u0648 <i>", Restored("{1} \u0648 {0}", "<i>", "<j>"), "tokens in another order");
            T.Equal("{X} \u0648 {X}", Restored("{0} \u0648 {0}", "{X}"), "a token that appears twice comes back twice");
            T.Equal("$1-{0}", Restored("{0}-{1}", "$1", "{0}"), "a placeholder that looks like a number in braces is not replaced twice");
            T.Equal("{x} a", Restored("{x} {0}", "a"), "braces without a number are left alone");
            T.Equal("plain text", Restored("plain text"), "nothing was sent, nothing to put back");
            T.Null(Restored("{0} only", "<i>", "<j>"), "a lost placeholder");
            T.Null(Restored("{0} {5}", "<i>"), "a number that was never sent");
            T.Null(Restored("\u0628\u0644\u0627 \u0631\u0645\u0648\u0632", "{X}"), "every token lost");
            T.Null(Restored("a 0 b", "{X}"), "a number without braces is not a token");
            T.Null(Restored("{0 and 1}", "<i>", "<j>"), "braces that do not enclose the number");

            // what was protected comes back as it was
            T.Equal("{PAWN_nameDef} was found <b>alone</b>.", RoundTrip("{PAWN_nameDef} was found <b>alone</b>."), "round trip of {PAWN_nameDef} was found <b>alone</b>.");
            T.Equal("[a <b> c] d", RoundTrip("[a <b> c] d"), "round trip of [a <b> c] d");
            T.Equal("{1} and {0}", RoundTrip("{1} and {0}"), "round trip of {1} and {0}");
            T.Equal("<color=#ff0000>{0}</color> \\n[PAWN_nameDef]{1_labelShort}", RoundTrip("<color=#ff0000>{0}</color> \\n[PAWN_nameDef]{1_labelShort}"), "round trip of <color=#ff0000>{0}</color> \\n[PAWN_nameDef]{1_labelShort}");
            T.Equal("\u062A\u0645 \u0627\u0644\u0639\u062B\u0648\u0631 \u0639\u0644\u0649 {0}", RoundTrip("\u062A\u0645 \u0627\u0644\u0639\u062B\u0648\u0631 \u0639\u0644\u0649 {0}"), "round trip of \u062A\u0645 \u0627\u0644\u0639\u062B\u0648\u0631 \u0639\u0644\u0649 {0}");

            // numbers with two digits
            var many = new StringBuilder();
            for (int i = 0; i < 12; i++) many.Append("<t").Append(i).Append('>');
            string manyProtected = Placeholders.ProtectNumbered(many.ToString(), out originals);
            T.Equal("{0}{1}{2}{3}{4}{5}{6}{7}{8}{9}{10}{11}", manyProtected, "twelve placeholders are numbered 0 to 11");
            T.Equal(many.ToString(), Restored(manyProtected, originals.ToArray()), "...and come back");
            var reversed = new StringBuilder();
            var reversedExpected = new StringBuilder();
            for (int i = 11; i >= 0; i--)
            {
                reversed.Append('{').Append(i).Append('}');
                reversedExpected.Append("<t").Append(i).Append('>');
            }
            T.Equal(reversedExpected.ToString(), Restored(reversed.ToString(), originals.ToArray()), "...in any order");
        }

        private static bool Exactly(string text, params string[] expected)
        {
            return Placeholders.HasExactly(text, expected);
        }

        /// <summary>What the translator does with a restored text: it must carry exactly the placeholders that were sent.</summary>
        private static string RestoredExactly(string translated, params string[] originals)
        {
            string restored = Restored(translated, originals);
            return restored != null && Placeholders.HasExactly(restored, originals) ? restored : null;
        }

        /// <summary>A multiplier sign ("x2", "x{0}") travels with its number: a translator reads the letter as a word.</summary>
        private static void MultiplierSigns()
        {
            List<string> originals;
            T.Equal("Spirit root: {0}", Placeholders.ProtectNumbered("Spirit root: x{0}", out originals), "the sign goes with its placeholder");
            T.Equal("x{0}", Join(originals), "the sign goes with its placeholder: the list");
            T.Equal("steam buns {0}", Placeholders.ProtectNumbered("steam buns x4", out originals), "the sign goes with a plain number");
            T.Equal("x4", Join(originals), "the sign goes with a plain number: the list");
            T.Equal("Chance {0} and {1}", Placeholders.ProtectNumbered("Chance x2.5 and {1}", out originals), "a decimal number belongs to the sign");
            T.Equal("x2.5|{1}", Join(originals), "a decimal number belongs to the sign: the list");
            T.Equal("({0})", Placeholders.ProtectNumbered("(x{0})", out originals), "after a bracket");
            T.Equal("x{0}", Join(originals), "after a bracket: the list");
            T.Equal("{0}{1}{2}", Placeholders.ProtectNumbered("<b>x{1}</b>", out originals), "after the opening tag of a span");
            T.Equal("<b>|x{1}|</b>", Join(originals), "after the opening tag of a span: the list");

            // not a multiplier: a dimension, a word, a name, a letter on its own
            T.Equal("{0}x{1}", Placeholders.ProtectNumbered("{A}x{B}", out originals), "an x between two placeholders is a dimension");
            T.Equal("{A}|{B}", Join(originals), "an x between two placeholders is a dimension: the list");
            T.Equal("3x3 box2 max{0}", Placeholders.ProtectNumbered("3x3 box2 max{0}", out originals), "a x after a digit or a letter");
            T.Equal("{0}", Join(originals), "a x after a digit or a letter: the list");
            T.Equal("x and 4 X2 x", Placeholders.ProtectNumbered("x and 4 X2 x", out originals), "a x that is not followed by its number");
            T.Equal("", Join(originals), "a x that is not followed by its number: the list");

            T.Equal("Spirit root: x{0}", RoundTrip("Spirit root: x{0}"), "round trip of a sign and its placeholder");
            T.Equal("steam buns x4, chance x2.5 <b>x{1}</b>", RoundTrip("steam buns x4, chance x2.5 <b>x{1}</b>"), "round trip of several signs");

            // a text that is only a sign and its number has nothing to translate
            T.False(TextSegmenter.HasLetters("x{0}") || TextSegmenter.HasLetters("x4") || TextSegmenter.HasLetters("x2.5 {0}"), "a sign and its number are not words");
            T.True(TextSegmenter.HasLetters("x and {0}") && TextSegmenter.HasLetters("Spirit root: x{0}"), "the words next to them are");
            T.Equal("T:Spirit root:  | -:x{0}", Describe(TextSegmenter.SplitAtPlaceholders("Spirit root: x{0}")), "a sign is copied with its placeholder");
            T.Equal("T:steam buns  | -:x4", Describe(TextSegmenter.SplitAtPlaceholders("steam buns x4")), "and with a plain number");
            T.Equal("Spirit root: x{0}", Joined(TextSegmenter.SplitAtPlaceholders("Spirit root: x{0}")), "the pieces give the text back");

            // the placeholder is what the finished text is checked for
            T.True(Exactly("a x{0}", "x{0}"), "a restored sign and placeholder");
            T.True(Exactly("a {0}", "x{0}"), "the sign itself is not a placeholder");
            T.True(Exactly("a x4", "x4"), "a plain number is none either");
            T.False(Exactly("a {{0}", "x{0}"), "a stray brace still fuses the placeholder");
            T.Equal("root: x{0}", RestoredExactly("root: {0}", "x{0}"), "restored with its sign, and exactly the placeholders that went in");

            // the sign behind its number: Yandex answered "8x" with the Arabic letter for s, but "4x" with the word for times
            T.Equal("Deals {0} damage", Placeholders.ProtectNumbered("Deals 8x damage", out originals), "the sign behind a number");
            T.Equal("8x", Join(originals), "the sign behind a number: the list");
            T.Equal("{0} and {1}", Placeholders.ProtectNumbered("2.5x and 10x", out originals), "a decimal and a long number");
            T.Equal("2.5x|10x", Join(originals), "a decimal and a long number: the list");
            T.Equal("({0})", Placeholders.ProtectNumbered("(4x)", out originals), "behind a number in brackets");
            T.Equal("4x", Join(originals), "behind a number in brackets: the list");
            T.Equal("{0}{1}{2}", Placeholders.ProtectNumbered("<b>4x</b>", out originals), "in a span");
            T.Equal("<b>|4x|</b>", Join(originals), "in a span: the list");
            T.Equal("3x3 20x20 5xp v1.5x 2 x 3 1.5xx", Placeholders.ProtectNumbered("3x3 20x20 5xp v1.5x 2 x 3 1.5xx", out originals),
                "a dimension, a word, a version and a spaced x are none");
            T.Equal("", Join(originals), "a dimension, a word, a version and a spaced x are none: the list");
            T.Equal("Deals 8x damage, 2.5x, <b>4x</b>", RoundTrip("Deals 8x damage, 2.5x, <b>4x</b>"), "round trip of signs behind numbers");
            T.False(TextSegmenter.HasLetters("4x") || TextSegmenter.HasLetters("8x {0}") || TextSegmenter.HasLetters("2.5x"), "a number and its sign are not words");
            T.True(TextSegmenter.HasLetters("Deals 8x damage"), "the words around them are");
            T.Equal("T:Deals  | -:8x | T: damage", Describe(TextSegmenter.SplitAtPlaceholders("Deals 8x damage")), "a sign behind a number is copied with it");
            T.True(Exactly("a 8x", "8x"), "a number with its sign is no placeholder either");

            // the cache looks for these texts
            T.True(Placeholders.HasMultiplier("x{0}") && Placeholders.HasMultiplier("Spirit root: x2.5") && Placeholders.HasMultiplier("8x") &&
                   Placeholders.HasMultiplier("(4x)") && Placeholders.HasMultiplier("x2"), "texts with a multiplier");
            T.False(Placeholders.HasMultiplier("3x3") || Placeholders.HasMultiplier("box2") || Placeholders.HasMultiplier("max{0}") ||
                    Placeholders.HasMultiplier("") || Placeholders.HasMultiplier(null) || Placeholders.HasMultiplier("x and 4") ||
                    Placeholders.HasMultiplier("{0}x{1}") || Placeholders.HasMultiplier("5xp"), "texts without one");
        }

        /// <summary>A tier numeral at the end of a text ("Dragon scales II"): a translator spells it out as an ordinal or reads the letter as "I".</summary>
        private static void TierNumerals()
        {
            List<string> originals;
            T.Equal("Dragon scales {0}", Placeholders.ProtectNumbered("Dragon scales II", out originals), "a tier numeral at the end");
            T.Equal("II", Join(originals), "a tier numeral at the end: the list");
            T.Equal("Nirvana {0}", Placeholders.ProtectNumbered("Nirvana IV", out originals), "a numeral with a V in it");
            T.Equal("IV", Join(originals), "a numeral with a V in it: the list");
            T.Equal("Tier {0}", Placeholders.ProtectNumbered("Tier VIII", out originals), "the longest numeral");
            T.Equal("VIII", Join(originals), "the longest numeral: the list");
            T.Equal("Keen senses {0}", Placeholders.ProtectNumbered("Keen senses I", out originals), "a single letter behind a short run of words");
            T.Equal("I", Join(originals), "a single letter behind a short run of words: the list");
            T.Equal("Keen senses {0}.", Placeholders.ProtectNumbered("Keen senses I.", out originals), "a full stop behind it");
            T.Equal("Keen senses {0}: ", Placeholders.ProtectNumbered("Keen senses V: ", out originals), "a colon and a space behind it");
            T.Equal("V", Join(originals), "a colon and a space behind it: the list");
            T.Equal("Mk-{0}", Placeholders.ProtectNumbered("Mk-II", out originals), "behind a hyphen");
            T.Equal("Chance {0} and Nirvana {1}", Placeholders.ProtectNumbered("Chance x2 and Nirvana III", out originals), "next to a multiplier sign");
            T.Equal("x2|III", Join(originals), "next to a multiplier sign: the list");
            T.Equal("{0}", Placeholders.ProtectNumbered("II", out originals), "a text that is only the numeral");

            // not a tier: a numeral that is not at the end, the pronoun, a name, a longer word
            T.Equal("Level III boss", Placeholders.ProtectNumbered("Level III boss", out originals), "a numeral in the middle");
            T.Equal("", Join(originals), "a numeral in the middle: the list");
            T.Equal("so do I", Placeholders.ProtectNumbered("so do I", out originals), "the pronoun behind a verb");
            T.Equal("you and I", Placeholders.ProtectNumbered("you and I", out originals), "the pronoun behind a conjunction");
            T.Equal("{0}", Placeholders.ProtectNumbered("I", out originals), "a text that is only the letter is the numeral, not the pronoun");
            T.Equal("I", Join(originals), "a text that is only the letter is the numeral, not the pronoun: the list");
            T.Equal("I am", Placeholders.ProtectNumbered("I am", out originals), "the pronoun at the start of a text");
            T.Equal("Hello, I", Placeholders.ProtectNumbered("Hello, I", out originals), "a letter behind a comma");
            T.Equal("Vitamin C", Placeholders.ProtectNumbered("Vitamin C", out originals), "another letter");
            T.Equal("Tier XI", Placeholders.ProtectNumbered("Tier XI", out originals), "a numeral that is not listed");
            T.Equal("AIII IIs", Placeholders.ProtectNumbered("AIII IIs", out originals), "a numeral inside a longer word");
            T.Equal("", Join(originals), "a numeral inside a longer word: the list");

            T.Equal("Dragon scales II", RoundTrip("Dragon scales II"), "round trip of a tier numeral");
            T.Equal("Keen senses I.", RoundTrip("Keen senses I."), "round trip of a letter and a full stop");
            T.Equal("Deals x{0} damage, Nirvana IV", RoundTrip("Deals x{0} damage, Nirvana IV"), "round trip next to a placeholder");

            // a text that is only a numeral has nothing to translate
            T.False(TextSegmenter.HasLetters("II") || TextSegmenter.HasLetters("IV.") || TextSegmenter.HasLetters("{0} III") || TextSegmenter.HasLetters("I"), "a numeral is not a word");
            T.True(TextSegmenter.HasLetters("I am") && TextSegmenter.HasLetters("Hello, I"), "but the pronoun is");
            T.True(TextSegmenter.HasLetters("Dragon scales II") && TextSegmenter.HasLetters("Level III boss"), "the words around it are");
            T.Equal("T:Dragon scales  | -:II", Describe(TextSegmenter.SplitAtPlaceholders("Dragon scales II")), "a numeral is copied, not translated");
            T.Equal("Dragon scales II", Joined(TextSegmenter.SplitAtPlaceholders("Dragon scales II")), "the pieces give the text back");
            T.True(Exactly("a II", "II"), "a numeral is no placeholder");

            // the cache looks for these texts
            T.True(Placeholders.HasRomanTier("Dragon scales II") && Placeholders.HasRomanTier("Keen senses I") &&
                   Placeholders.HasRomanTier("Nirvana IV.") && Placeholders.HasRomanTier("II") && Placeholders.HasRomanTier("I"), "texts with a tier numeral");
            T.False(Placeholders.HasRomanTier("Level III boss") || Placeholders.HasRomanTier("so do I") || Placeholders.HasRomanTier("") ||
                    Placeholders.HasRomanTier(null) || Placeholders.HasRomanTier("Vitamin C") || Placeholders.HasRomanTier("Tier XI") ||
                    Placeholders.HasRomanTier("I am") || Placeholders.HasRomanTier("C"), "texts without one");
        }

        private static void NumberedCheck()
        {
            T.True(Exactly("a {0} b <i>", "{0}", "<i>"), "the placeholders that were listed and nothing else");
            T.True(Exactly("<i> b {0}", "{0}", "<i>"), "in any order");
            T.True(Exactly("<b>x</b>", "<b>", "</b>"), "tags are placeholders, too");
            T.True(Exactly("{0}-{0}", "{0}", "{0}"), "a placeholder listed twice is there twice");
            T.True(Exactly("plain text"), "none expected, none found");
            T.True(Exactly(null), "null and none expected");
            T.False(Exactly("a {0}", "{0}", "<i>"), "one is missing");
            T.False(Exactly("a {0} {0}", "{0}"), "one is repeated");
            T.False(Exactly("a {0} {5}", "{0}"), "one was invented");
            T.False(Exactly("a [x]", "{0}"), "one was swapped for another");
            T.False(Exactly("a {{1} b {0}", "{0}", "{1}"), "a stray brace in front of a placeholder makes it another span");
            T.False(Exactly("a <b {0} c>", "{0}"), "a placeholder inside a tag that the translator wrote");
            T.False(Exactly("", "{0}"), "an empty text lost them");
            T.False(Exactly(null, "{0}"), "so does null");
            T.False(Exactly("a {0}"), "none expected, one found");

            // together with the restoring: a stray brace next to a number gets through the restore but not the check
            T.Equal("a {0} b {1}", RestoredExactly("a {0} b {1}", "{0}", "{1}"), "a good answer passes");
            T.Equal("{1} \u2190 {0}", RestoredExactly("{1} \u2190 {0}", "{0}", "{1}"), "so does one that moved them");
            T.Equal("a {{1} b {0}", Restored("a {{1} b {0}", "{0}", "{1}"), "the restore alone accepts a stray brace");
            T.Null(RestoredExactly("a {{1} b {0}", "{0}", "{1}"), "but the check does not");
            T.Null(RestoredExactly("a {0} b {1} {1}", "{0}", "{1}"), "a number that appears twice is caught as well");
        }

        // ------------------------------------------------------------------------------------------ segmentation

        private static string Joined(List<TextSegment> segments)
        {
            var sb = new StringBuilder();
            foreach (var s in segments) sb.Append(s.Text);
            return sb.ToString();
        }

        private static string Describe(List<TextSegment> segments)
        {
            var sb = new StringBuilder();
            foreach (var s in segments)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(s.Translate ? "T:" : "-:").Append(T.Show(s.Text));
            }
            return sb.ToString();
        }

        private static void Segmentation()
        {
            T.Equal(0, TextSegmenter.Split(null, 200).Count, "null gives no segments");
            T.Equal(0, TextSegmenter.Split("", 200).Count, "the empty text gives no segments");

            T.Equal("T:Hello world.", Describe(TextSegmenter.Split("Hello world.", 200)), "one sentence is one segment");
            T.Equal("T:First. Second.", Describe(TextSegmenter.Split("First. Second.", 200)), "sentences that fit together stay together");
            T.Equal("T:Hello | -:\\n | T:World", Describe(TextSegmenter.Split("Hello\nWorld", 200)), "a line feed separates segments and is kept verbatim");
            T.Equal("T:Hello | -:\\\\n | T:World", Describe(TextSegmenter.Split("Hello\\nWorld", 200)), "the escape \\n separates segments and is kept verbatim");
            T.Equal("T:Hello | -:\\r\\n | T:World", Describe(TextSegmenter.Split("Hello\r\nWorld", 200)), "CR LF is one separator");
            T.Equal("-:\\n | T:Hello", Describe(TextSegmenter.Split("\nHello", 200)), "a leading line feed is kept");
            T.Equal("T:Hello | -:\\n", Describe(TextSegmenter.Split("Hello\n", 200)), "a trailing line feed is kept");
            T.Equal("-:   | T:Hello ", Describe(TextSegmenter.Split("  Hello ", 200)), "leading spaces are copied, the rest is translated");
            T.Equal("-:{0}", Describe(TextSegmenter.Split("{0}", 200)), "a placeholder alone is not translated");
            T.Equal("-:123", Describe(TextSegmenter.Split("123", 200)), "a number alone is not translated");
            T.Equal("-:...", Describe(TextSegmenter.Split("...", 200)), "punctuation alone is not translated");
            T.Equal("T:<b>Hello</b>", Describe(TextSegmenter.Split("<b>Hello</b>", 200)), "a text inside tags is translated with its tags");
            T.Equal("T:Hello {0}!", Describe(TextSegmenter.Split("Hello {0}!", 200)), "placeholders inside a sentence stay in it");

            // the limit
            T.Equal("T:First. | -:  | T:Second. | -:  | T:Third.", Describe(TextSegmenter.Split("First. Second. Third.", 10)), "sentences are cut when the limit is reached");
            T.Equal("T:Averyveryverylongsentencewithoutpunctuation", Describe(TextSegmenter.Split("Averyveryverylongsentencewithoutpunctuation", 10)),
                "a single sentence longer than the limit is not cut");
            T.Equal("T:Hello world", Describe(TextSegmenter.Split("Hello world", 5)), "spaces inside a sentence never separate segments");

            // joining the segments gives back the text, whatever the text and the limit
            string[] samples =
            {
                "Hello world.", "First. Second. Third.", "Line one\nLine two", "Line one\r\nLine two\r\n", "A\\nB\\nC", "  leading and trailing  ",
                "<b>Bold</b> and <i>italic</i>.", "{PAWN_nameDef} was hit by [attacker_nameDef]!", "A. B. C. D. E. F.", "...", "?!", "\n\n\n",
                "Tab\tseparated\tvalues.", "Sentence one.   Sentence two.\n\nSentence three.", "No punctuation at all just words",
                "Trailing space. ", " . ", "End with ellipsis\u2026 and more", "Mixed \u0645\u0631\u062D\u0628\u0627 text. And more text here."
            };
            foreach (var s in samples)
            {
                foreach (int limit in new[] { 1, 5, 10, 20, 50, 200, 100000 })
                {
                    var segments = TextSegmenter.Split(s, limit);
                    T.Equal(s, Joined(segments), "joined segments (limit " + limit + ") give back [" + T.Show(s) + "]");
                    foreach (var seg in segments)
                    {
                        if (seg.Translate && !TextSegmenter.HasLetters(seg.Text))
                        {
                            T.Fail("a segment without letters is marked for translation: [" + T.Show(seg.Text) + "]");
                        }
                        if (!seg.Translate && seg.Text.Length == 0)
                        {
                            T.Fail("an empty segment");
                        }
                    }
                }
            }

            T.True(TextSegmenter.HasLetters("a") && TextSegmenter.HasLetters("\u0645\u0631\u062D\u0628\u0627") && TextSegmenter.HasLetters("<b>x</b>"), "letters are found");
            T.False(TextSegmenter.HasLetters("{0}") || TextSegmenter.HasLetters("123") || TextSegmenter.HasLetters("<b></b>") || TextSegmenter.HasLetters("!!") ||
                    TextSegmenter.HasLetters("[PAWN_nameDef]") || TextSegmenter.HasLetters(""), "texts without letters outside placeholders are not translated");
            T.Equal(200, TextSegmenter.DefaultLimit, "the default limit is the one the mod has always used");

            // cut at the placeholders (for a provider that garbles them): they are copied, the words between them are translated
            T.Equal(0, TextSegmenter.SplitAtPlaceholders(null).Count, "null gives no pieces");
            T.Equal("T:Cost:  | -:{0}", Describe(TextSegmenter.SplitAtPlaceholders("Cost: {0}")), "a placeholder ends a piece");
            T.Equal("-:<b> | T:Hi | -:</b> | -:  | -:{0} | -:!", Describe(TextSegmenter.SplitAtPlaceholders("<b>Hi</b> {0}!")), "tags and placeholders are copied");
            T.Equal("-:{0} | T:s", Describe(TextSegmenter.SplitAtPlaceholders("{0}s")), "what follows a placeholder is a piece of its own");
            foreach (var s in samples)
            {
                T.Equal(s, Joined(TextSegmenter.SplitAtPlaceholders(s)), "the pieces around the placeholders give back [" + T.Show(s) + "]");
            }
        }

        // ------------------------------------------------------------------------------------------ orthography

        private static void Orthography()
        {
            // the required spelling: the fathatan stands on the alef (after it), never on the letter before it
            T.Equal("\u0623\u064A\u0636\u0627\u064B", ArabicText.NormalizeOrthography("\u0623\u064A\u0636\u064B\u0627"), "\u0623\u064A\u0636\u064B\u0627 becomes \u0623\u064A\u0636\u0627\u064B");
            T.Equal("\u0623\u064A\u0636\u0627\u064B", ArabicText.NormalizeOrthography("\u0623\u064A\u0636\u0627"), "the bare \u0623\u064A\u0636\u0627 gets its fathatan");
            T.Equal("\u0648\u0623\u064A\u0636\u0627\u064B", ArabicText.NormalizeOrthography("\u0648\u0623\u064A\u0636\u0627"), "with the prefix \u0648");
            T.Equal("\u0641\u0623\u064A\u0636\u0627\u064B", ArabicText.NormalizeOrthography("\u0641\u0623\u064A\u0636\u0627"), "with the prefix \u0641");
            T.Equal("\u062C\u062F\u0651\u0627\u064B", ArabicText.NormalizeOrthography("\u062C\u062F\u064B\u0651\u0627"), "the fathatan moves behind the alef past a shadda");
            T.Equal("\u0643\u062A\u0627\u0628\u0627\u064B", ArabicText.NormalizeOrthography("\u0643\u062A\u0627\u0628\u064B\u0627"), "any word that ends in fathatan + alef");
            T.Equal("\u0646\u0639\u0645 \u0623\u064A\u0636\u0627\u064B.", ArabicText.NormalizeOrthography("\u0646\u0639\u0645 \u0623\u064A\u0636\u064B\u0627."), "inside a sentence");

            // idempotent, and no change where nothing is wrong
            string right = "\u0623\u064A\u0636\u0627\u064B";
            T.Equal(right, ArabicText.NormalizeOrthography(right), "the correct spelling is left alone");
            T.Equal(right, ArabicText.NormalizeOrthography(ArabicText.NormalizeOrthography("\u0623\u064A\u0636\u064B\u0627")), "normalizing twice changes nothing more");
            string plain = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0643";
            T.True(object.ReferenceEquals(plain, ArabicText.NormalizeOrthography(plain)), "a text with nothing to fix is returned as the same string");
            T.Equal("Hello {0}", ArabicText.NormalizeOrthography("Hello {0}"), "Latin text is untouched");
            T.True(ArabicText.NormalizeOrthography(null) == null, "null stays null");
            T.Equal("", ArabicText.NormalizeOrthography(""), "the empty text stays empty");

            // "?" is the Arabic question mark in an Arabic text; placeholders, tags and switch blocks keep theirs
            string ask = "\u0647\u0644 \u0623\u0646\u062A \u0645\u0633\u062A\u0639\u062F";
            T.Equal(ask + "\u061F", ArabicText.NormalizeOrthography(ask + "?"), "? becomes \u061F");
            T.Equal(ask + "\u061F!", ArabicText.NormalizeOrthography(ask + "?!"), "also in front of other punctuation");
            T.Equal("\u0645\u0627\u0630\u0627 {0}\u061F", ArabicText.NormalizeOrthography("\u0645\u0627\u0630\u0627 {0}?"), "after a placeholder");
            T.Equal("{KEY ? \u0623 : \u0628} \u0647\u0644\u061F", ArabicText.NormalizeOrthography("{KEY ? \u0623 : \u0628} \u0647\u0644?"), "the ? of a switch block stays");
            T.Equal("<a href=\"x?y\">" + ask + "\u061F</a>", ArabicText.NormalizeOrthography("<a href=\"x?y\">" + ask + "?</a>"), "the ? of a tag stays");
            T.Equal(ask + " a?b=1", ArabicText.NormalizeOrthography(ask + " a?b=1"), "a ? in an address stays");
            T.Equal(ask + " page2?x=1", ArabicText.NormalizeOrthography(ask + " page2?x=1"), "also after a digit");
            T.Equal(ask + "\u061F (Ready?)", ArabicText.NormalizeOrthography(ask + "? (Ready?)"), "the ? of a Latin phrase stays, the one of the Arabic sentence does not");
            T.Equal(ask + "\u061F (Ready?)", ArabicText.NormalizeOrthography(ask + "\u061F (Ready?)"), "an Arabic sentence with a Latin phrase is left as it is");
            T.Equal("\u0643\u0645 5\u061F", ArabicText.NormalizeOrthography("\u0643\u0645 5?"), "a number at the end of the sentence");
            T.Equal("Ready?", ArabicText.NormalizeOrthography("Ready?"), "a text without Arabic keeps its ?");
            string asked = ask + "\u061F";
            T.True(object.ReferenceEquals(asked, ArabicText.NormalizeOrthography(asked)), "nothing to change: the same string");

            // no space before the Arabic comma, semicolon and question mark, nor before a colon after an Arabic letter
            string word = "\u0643\u0644\u0645\u0629";
            T.Equal(word + "\u060C " + word, ArabicText.NormalizeOrthography(word + " \u060C " + word), "no space before the Arabic comma");
            T.Equal(word + "\u061B " + word, ArabicText.NormalizeOrthography(word + " \u061B " + word), "nor before the Arabic semicolon");
            T.Equal(word + "\u061F", ArabicText.NormalizeOrthography(word + " \u061F"), "nor before the Arabic question mark");
            T.Equal(word + "\u060C " + word, ArabicText.NormalizeOrthography(word + "  \t\u060C " + word), "several spaces and a tab");
            T.Equal(word + ": " + word, ArabicText.NormalizeOrthography(word + " : " + word), "a colon after an Arabic letter");
            T.Equal(word + " 5 : 3", ArabicText.NormalizeOrthography(word + " 5 : 3"), "a colon after a digit stays");
            T.Equal(word + " {0}\u060C " + word, ArabicText.NormalizeOrthography(word + " {0} \u060C " + word), "after a placeholder");
            T.Equal(word + " {KEY ? \u0623 : \u0628}", ArabicText.NormalizeOrthography(word + " {KEY ? \u0623 : \u0628}"), "a switch block keeps its colon");
            T.Equal(word + "\n \u060C " + word, ArabicText.NormalizeOrthography(word + "\n \u060C " + word), "the start of a line is not touched");
            T.Equal(word + " , " + word + ":", ArabicText.NormalizeOrthography(word + " , " + word + " :"), "the Latin comma stays, the colon after an Arabic letter is tightened");
            T.Equal("a , b", ArabicText.NormalizeOrthography("a , b"), "a text without Arabic is untouched");
            string tight = word + "\u060C " + word;
            T.True(object.ReferenceEquals(tight, ArabicText.NormalizeOrthography(tight)), "nothing to tighten: the same string");
            T.Equal(tight, ArabicText.NormalizeOrthography(ArabicText.NormalizeOrthography(word + " \u060C " + word)), "tightening twice changes nothing more");

            // the forbidden spelling never survives
            T.False(ArabicText.NormalizeOrthography("\u0623\u064A\u0636\u064B\u0627").Contains("\u0623\u064A\u0636\u064B\u0627"), "the spelling with the fathatan on the dad is gone");
        }

        private static void Words()
        {
            T.True(ArabicText.IsLetter('\u0628') && ArabicText.IsLetter('\u0640') && ArabicText.IsLetter('\u0621'), "Arabic letters (tatweel included) are letters");
            T.False(ArabicText.IsLetter('\u064E') || ArabicText.IsLetter('a') || ArabicText.IsLetter('1') || ArabicText.IsLetter(' '), "marks, Latin letters, digits and spaces are not");
            T.True(ArabicText.IsMark('\u064E') && ArabicText.IsMark('\u0651') && !ArabicText.IsMark('\u0628'), "marks are marks");
            T.True(ArabicText.ContainsArabic("abc \u0628") && !ArabicText.ContainsArabic("abc 123") && !ArabicText.ContainsArabic("") && !ArabicText.ContainsArabic(null), "ContainsArabic");

            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", ArabicText.AddArticle("\u0645\u0633\u062A\u0639\u0645\u0631"), "the article is added");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", ArabicText.AddArticle("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631"), "a word that has the article keeps one");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", ArabicText.StripArticle("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631"), "the article is removed");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", ArabicText.StripArticle("\u0645\u0633\u062A\u0639\u0645\u0631"), "a word without the article stays");
            T.Equal("\u0627\u0644", ArabicText.StripArticle("\u0627\u0644"), "the two letters alef lam alone are not an article");
            T.Equal("", ArabicText.AddArticle(""), "no article on nothing");
            T.True(ArabicText.IsSingleWord("\u0645\u0633\u062A\u0639\u0645\u0631") && !ArabicText.IsSingleWord("\u0643\u0628\u0633\u0648\u0644\u0629 \u0646\u0642\u0644") && !ArabicText.IsSingleWord(""), "single words");

            // comparison skeleton: no marks, no tatweel, alef variants folded, alef maksura read as yeh
            T.Equal("\u0627\u062D\u0645\u062F", ArabicText.Skeleton("\u0623\u064E\u062D\u0652\u0645\u064E\u062F"), "marks and the hamza of the alef are dropped");
            T.Equal("\u0645\u0631\u062D\u0628\u0627", ArabicText.Skeleton("\u0645\u0640\u0631\u062D\u0628\u0627"), "tatweel is dropped");
            T.Equal("\u0639\u0644\u064A", ArabicText.Skeleton("\u0639\u0644\u0649"), "alef maksura reads as yeh");
            T.Equal("\u0627\u0646\u0633\u0627\u0646", ArabicText.Skeleton("\u0625\u0650\u0646\u0633\u0627\u0646"), "alef with hamza below reads as alef");
            T.Equal("", ArabicText.Skeleton(null), "the skeleton of null is empty");

            // does a translation contain the approved term (with the usual one-letter prefixes)?
            string term = "\u0645\u0633\u062A\u0639\u0645\u0631";
            T.True(ArabicText.ContainsTerm("\u0647\u0630\u0627 \u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u064A\u062F", term), "the term as a word");
            T.True(ArabicText.ContainsTerm("\u0647\u0630\u0627 \u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u064A\u062F", term), "with the article");
            T.True(ArabicText.ContainsTerm("\u0648\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u064A\u062F", term), "with wa + the article");
            T.True(ArabicText.ContainsTerm("\u0644\u0644\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", term), "with li + the article and a plural ending");
            T.True(ArabicText.ContainsTerm("\u0645\u064F\u0633\u0652\u062A\u064E\u0639\u0652\u0645\u0650\u0631", term), "with vowel marks");
            T.True(ArabicText.ContainsTerm("\u0647\u0630\u0627 \u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631"), "a term given with its article");
            T.False(ArabicText.ContainsTerm("\u0643\u0644\u0645\u0629 \u0623\u062E\u0631\u0649", term), "another text does not contain it");
            T.False(ArabicText.ContainsTerm("\u0627\u062A\u0645\u0633\u062A\u0639\u0645\u0631", term), "the term inside another word is not a match");
            T.False(ArabicText.ContainsTerm("", term) || ArabicText.ContainsTerm(null, term) || ArabicText.ContainsTerm("\u0645\u0633\u062A\u0639\u0645\u0631", ""), "nothing to look for or in");
        }
    }
}
