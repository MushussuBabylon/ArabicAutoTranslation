using System;
using System.Collections.Generic;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// The display stage (DisplayProcessor): 356 strings with three option sets from the reference implementation, the
    /// ten mandatory mixed-text samples with structural checks, tokenizer, line handling and option behavior.
    /// </summary>
    internal static class DisplayTests
    {
        private static readonly DisplayOptions Full = new DisplayOptions { Shape = true, Reorder = true, LtrIsolates = true };
        private static readonly DisplayOptions NoIsolates = new DisplayOptions { Shape = true, Reorder = true, LtrIsolates = false };
        private static readonly DisplayOptions ShapedOnly = new DisplayOptions { Shape = true, Reorder = false, LtrIsolates = true };

        internal static void Run()
        {
            T.Group("display: reference vectors");
            ReferenceVectors();

            T.Group("display: mandatory mixed-text samples");
            MandatorySamples();

            T.Group("display: tokenizer, lines, options");
            Tokenizer();
            Lines();
            Options();
        }

        private static string Cps(params int[] cps)
        {
            return CodePoints.ToString(cps);
        }

        private static bool HasBidiControl(string s)
        {
            foreach (char c in s)
            {
                if ((c >= '\u2066' && c <= '\u2069') || (c >= '\u202A' && c <= '\u202E')) return true;
            }
            return false;
        }

        private static void ReferenceVectors()
        {
            foreach (var v in GoldenVectors.Display)
            {
                string d = v.Name + " [" + T.Show(v.Logical) + "]";
                string full = DisplayProcessor.Process(v.Logical, Full);
                string noIso = DisplayProcessor.Process(v.Logical, NoIsolates);
                string shaped = DisplayProcessor.Process(v.Logical, ShapedOnly);
                T.Equal(v.Full, full, "shaped + reordered, isolates on: " + d);
                T.Equal(v.NoIsolates, noIso, "shaped + reordered, isolates off: " + d);
                T.Equal(v.ShapedOnly, shaped, "shaped only (typing order): " + d);

                // properties that hold for every string
                T.False(HasBidiControl(full) || HasBidiControl(noIso) || HasBidiControl(shaped), "no bidirectional control character is left in " + d);
                if (DisplayProcessor.HasStrongRtl(v.Logical))
                {
                    T.False(ArabicPipeline.HasUnshapedLetters(full) || ArabicPipeline.HasUnshapedLetters(noIso) || ArabicPipeline.HasUnshapedLetters(shaped),
                        "every Arabic letter with forms is shaped in " + d);
                }
                else
                {
                    T.True(full == v.Logical && noIso == v.Logical && shaped == v.Logical, "text without right-to-left letters is returned as it is: " + d);
                }
            }
        }

        private static void MandatorySamples()
        {
            T.Equal(10, GoldenVectors.MandatoryCount, "the number of mandatory samples");
            if (GoldenVectors.Display.Length < GoldenVectors.MandatoryCount)
            {
                T.Fail("the mandatory samples are missing from the vectors");
                return;
            }

            for (int i = 0; i < GoldenVectors.MandatoryCount; i++)
            {
                var v = GoldenVectors.Display[i];
                string logical = v.Logical;
                string shaped = DisplayProcessor.Process(logical, ShapedOnly);
                string full = DisplayProcessor.Process(logical, Full);
                string d = "sample " + (i + 1) + " [" + T.Show(logical) + "]";

                // shaping: no base-form letters left; typing order untouched; the plain text comes back from the shaped text
                T.False(ArabicPipeline.HasUnshapedLetters(full), "all letters shaped: " + d);
                T.True(ArabicShaper.HasPresentationForms(full), "presentation forms in the output: " + d);
                T.Equal(logical, ArabicShaper.Unshape(shaped), "shaping alone only changes the letter forms: " + d);

                // BiDi: reordering moves characters but changes none (no bracket of these samples is mirrored)
                T.Equal(Sorted(shaped), Sorted(full), "the visual text is a permutation of the shaped text: " + d);
                T.False(HasBidiControl(full), "no control characters: " + d);

                // the same text through the pipeline checks (placeholders and tags survive)
                T.Null(Placeholders.Check(logical, full, Placeholders.Mode.TagsAsSet), "placeholders survive: " + d);
            }

            // the Latin words, numbers and placeholders stay intact and in their own left-to-right order
            string[] samples = new string[GoldenVectors.MandatoryCount];
            for (int i = 0; i < samples.Length; i++) samples[i] = DisplayProcessor.Process(GoldenVectors.Display[i].Logical, Full);

            T.True(samples[0].StartsWith("RimWorld ", StringComparison.Ordinal), "sample 1: RimWorld is intact and leftmost (last word of a right-to-left sentence)");
            T.True(samples[1].StartsWith("RimWorld ", StringComparison.Ordinal), "sample 2: RimWorld is intact and leftmost");
            T.True(samples[2].Contains(" 25 ") && samples[2].Contains("10"), "sample 3: the numbers 25 and 10 are intact");
            T.True(samples[3].StartsWith("(Ready?) ", StringComparison.Ordinal), "sample 4: (Ready?) is intact, not mirrored, and leftmost");
            T.True(samples[3].IndexOf('\u061F') > 0, "sample 4: the Arabic question mark is kept");
            T.True(samples[4].Contains("{0}"), "sample 5: {0} is intact");
            T.True(samples[4].StartsWith("!", StringComparison.Ordinal), "sample 5: the final exclamation mark ends up on the left");
            T.True(samples[5].StartsWith("Graphics ", StringComparison.Ordinal) && samples[5].Contains(" Settings "), "sample 6: Settings and Graphics are intact");
            T.True(samples[6].StartsWith("75% ", StringComparison.Ordinal), "sample 7: 75% is intact and leftmost");
            T.True(samples[7].StartsWith("-15\u00B0C ", StringComparison.Ordinal), "sample 8: -15 degrees C is intact (sign and unit stay with the number)");
            T.True(samples[8].StartsWith("RimWorld ", StringComparison.Ordinal) && samples[8].Contains(" Mods ") && samples[8].Contains(" Settings "),
                "sample 9: RimWorld, Mods and Settings are intact");
            T.True(samples[9].StartsWith("!", StringComparison.Ordinal) && samples[9].IndexOf('\u061F') > 0, "sample 10: the punctuation of the two questions is kept");

            // sample 1 spelled out: the whole visual string
            T.Equal("RimWorld " + Cps(0xFEE2, 0xFEDF, 0xFE8E, 0xFECB) + " " + Cps(0xFEF2, 0xFED3) + " " + Cps(0xFEDA, 0xFE91) + " " +
                    Cps(0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3),
                samples[0], "sample 1, character by character");

            // render mode 1 (shaped, typing order) is stable when the text is processed again: the stage unshapes first
            for (int i = 0; i < GoldenVectors.MandatoryCount; i++)
            {
                string logical = GoldenVectors.Display[i].Logical;
                string once = DisplayProcessor.Process(logical, ShapedOnly);
                T.Equal(once, DisplayProcessor.Process(once, ShapedOnly), "shaped-only output is a fixed point of the stage: sample " + (i + 1));
                T.Equal(samples[i], DisplayProcessor.Process(once, Full), "reordering shaped text equals reordering the plain text: sample " + (i + 1));
            }
        }

        private static string Sorted(string s)
        {
            char[] a = s.ToCharArray();
            Array.Sort(a);
            return new string(a);
        }

        private static void Tokenizer()
        {
            var toks = DisplayProcessor.Tokenize("a {0} b");
            T.Equal(3, toks.Count, "text, placeholder, text");
            if (toks.Count == 3)
            {
                T.True(toks[0].Kind == TokKind.Text && toks[0].Text == "a ", "first token is the text before the placeholder");
                T.True(toks[1].Kind == TokKind.Atom && toks[1].Text == "{0}", "second token is the placeholder");
                T.True(toks[2].Kind == TokKind.Text && toks[2].Text == " b", "third token is the text after the placeholder");
            }

            toks = DisplayProcessor.Tokenize("<b>x</b>");
            T.Equal(3, toks.Count, "open tag, text, close tag");
            if (toks.Count == 3)
            {
                T.True(toks[0].Kind == TokKind.Open && toks[0].Name == "b", "open tag <b>");
                T.True(toks[1].Kind == TokKind.Text && toks[1].Text == "x", "text between the tags");
                T.True(toks[2].Kind == TokKind.Close && toks[2].Name == "b", "close tag </b>");
            }

            toks = DisplayProcessor.Tokenize("<color=#ff0000>x</color>");
            T.True(toks.Count == 3 && toks[0].Kind == TokKind.Open && toks[0].Name == "color" && toks[2].Kind == TokKind.Close && toks[2].Name == "color",
                "a color tag with an argument is an opener named color");

            toks = DisplayProcessor.Tokenize("[PAWN_nameDef]");
            T.True(toks.Count == 1 && toks[0].Kind == TokKind.Atom, "[PAWN_nameDef] is one placeholder");
            toks = DisplayProcessor.Tokenize("<Prohis>");
            T.True(toks.Count == 1 && toks[0].Kind == TokKind.Atom, "<Prohis> is one placeholder");
            toks = DisplayProcessor.Tokenize("{PAWN_possessive}");
            T.True(toks.Count == 1 && toks[0].Kind == TokKind.Atom, "{PAWN_possessive} is one placeholder");
            toks = DisplayProcessor.Tokenize("{ 0 }");
            T.True(toks.Count == 1 && toks[0].Kind == TokKind.Text, "braces with spaces are plain text");
            T.Equal(0, DisplayProcessor.Tokenize("").Count, "no tokens in the empty line");
        }

        private static void Lines()
        {
            string ar = "\u0645\u0631\u062D\u0628\u0627";
            string arShaped = Cps(0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3);
            string ar2 = "\u0628\u0643";
            string ar2Shaped = Cps(0xFEDA, 0xFE91);

            // every line is processed on its own and the line breaks stay where they are
            T.Equal(arShaped + "\n" + ar2Shaped, DisplayProcessor.Process(ar + "\n" + ar2, Full), "a line feed separates two lines");
            T.Equal(arShaped + "\r\n" + ar2Shaped, DisplayProcessor.Process(ar + "\r\n" + ar2, Full), "a carriage return + line feed separates two lines");
            T.Equal(arShaped + "\\n" + ar2Shaped, DisplayProcessor.Process(ar + "\\n" + ar2, Full), "the two-character escape \\n separates two lines");
            T.Equal("\n" + arShaped, DisplayProcessor.Process("\n" + ar, Full), "a leading line feed is kept");
            T.Equal(arShaped + "\n", DisplayProcessor.Process(ar + "\n", Full), "a trailing line feed is kept");

            var parts = DisplayProcessor.SplitLines("a\nb");
            T.True(parts.Count == 3 && parts[0] == "a" && parts[1] == "\n" && parts[2] == "b", "SplitLines: line feed");
            parts = DisplayProcessor.SplitLines("a\\nb");
            T.True(parts.Count == 3 && parts[0] == "a" && parts[1] == "\\n" && parts[2] == "b", "SplitLines: the escape \\n");
            parts = DisplayProcessor.SplitLines("a\r\nb");
            T.True(parts.Count == 3 && parts[1] == "\r\n", "SplitLines: CR LF is one separator");
            parts = DisplayProcessor.SplitLines("");
            T.True(parts.Count == 1 && parts[0] == "", "SplitLines: the empty text is one empty line");
            parts = DisplayProcessor.SplitLines("x\n");
            T.True(parts.Count == 3 && parts[2] == "", "SplitLines: a trailing separator leaves an empty last line");

            // rich text
            T.Equal(ar2Shaped + " <b>" + arShaped + "</b>", DisplayProcessor.Process("<b>" + ar + "</b> " + ar2, Full), "a bold span is re-wrapped around its visual position");
            string withPlaceholder = DisplayProcessor.Process("\u062A\u0645 [PAWN_nameDef] \u0628\u0643", Full);
            T.True(withPlaceholder.Contains("[PAWN_nameDef]"), "a square-bracket placeholder stays intact");
            withPlaceholder = DisplayProcessor.Process("\u062A\u0645 <Prohis> \u0628\u0643", Full);
            T.True(withPlaceholder.Contains("<Prohis>"), "an angle-bracket pronoun stays intact");

            // no right-to-left letters: nothing to do
            foreach (var s in new[] { "RimWorld", "123", "Hello {0}", "{0}", "<b>x</b>", "x y z", "line1\nline2" })
            {
                T.True(object.ReferenceEquals(s, DisplayProcessor.Process(s, Full)), "text without right-to-left letters is returned as the same string: " + T.Show(s));
            }
            T.True(DisplayProcessor.Process(null, Full) == null, "null stays null");
            T.Equal("", DisplayProcessor.Process("", Full), "the empty text stays empty");

            T.True(DisplayProcessor.HasStrongRtl(ar), "Arabic has strong right-to-left letters");
            T.True(DisplayProcessor.HasStrongRtl("\u05D0\u05D1"), "Hebrew has strong right-to-left letters");
            T.True(DisplayProcessor.HasStrongRtl(Cps(0xFE91)), "presentation forms are right-to-left letters");
            T.False(DisplayProcessor.HasStrongRtl("RimWorld 123 {0} (x)"), "Latin text, digits and brackets are not");
            T.False(DisplayProcessor.HasStrongRtl(""), "the empty text is not");
            T.False(DisplayProcessor.HasStrongRtl(null), "null is not");
        }

        private static void Options()
        {
            string ar = "\u0645\u0631\u062D\u0628\u0627";

            // reordering without shaping, shaping without reordering
            T.Equal("\u0627\u0628\u062D\u0631\u0645", DisplayProcessor.Process(ar, new DisplayOptions { Shape = false, Reorder = true }), "reorder only: the plain letters in visual order");
            T.Equal(Cps(0xFEE3, 0xFEAE, 0xFEA3, 0xFE92, 0xFE8E), DisplayProcessor.Process(ar, ShapedOnly), "shape only: presentation forms in typing order");
            T.Equal(Cps(0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3), DisplayProcessor.Process(ar, Full), "shape and reorder");

            // the signature identifies the combination
            var seen = new HashSet<string>();
            foreach (bool shape in new[] { false, true })
            {
                foreach (bool reorder in new[] { false, true })
                {
                    foreach (bool iso in new[] { false, true })
                    {
                        foreach (bool rtl in new[] { false, true })
                        {
                            var o = new DisplayOptions { Shape = shape, Reorder = reorder, LtrIsolates = iso, BaseRtl = rtl };
                            T.True(seen.Add(o.Signature()), "every option combination has its own signature: " + o.Signature());
                        }
                    }
                }
            }

            // the pipeline's own shortcut: neither shaping nor reordering leaves the text alone
            var p = new ArabicPipeline(null, new DisplayOptions { Shape = false, Reorder = false });
            T.Equal(ar, p.RenderDisplay(ar), "no shaping and no reordering: the text is not touched");
            p = new ArabicPipeline(null, null);
            T.Equal(Cps(0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3), p.RenderDisplay(ar), "the default options shape and reorder");
            T.True(p.RenderDisplay(null) == null && p.RenderDisplay("") == "", "null and the empty text stay as they are");
        }
    }
}
