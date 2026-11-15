using System;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// The UAX #9 engine (Bidi): 558 level vectors and 558 visual-order vectors generated from the reference
    /// implementation (validated against Chromium / ICU / FriBidi), plus explicit cases that can be checked by hand.
    /// </summary>
    internal static class BidiTests
    {
        internal static void Run()
        {
            T.Group("bidi: reference levels");
            ReferenceLevels();

            T.Group("bidi: reference visual order");
            ReferenceOrder();

            T.Group("bidi: explicit cases");
            ExplicitCases();
        }

        private static string Describe(string text, int para)
        {
            return "[" + T.Show(text) + "] with " + (para < 0 ? "automatic" : para == 0 ? "left-to-right" : "right-to-left") + " paragraph";
        }

        private static void ReferenceLevels()
        {
            foreach (var v in GoldenVectors.Levels)
            {
                var r = Bidi.ResolveLevels(CodePoints.From(v.Text), v.Para);
                string d = Describe(v.Text, v.Para);
                T.Equal(v.Paragraph, r.Paragraph, "paragraph level of " + d);
                if (!T.EqualInts(v.Keep, r.Keep, "characters that survive X9 in " + d)) continue;
                var levels = new int[r.Keep.Length];
                for (int k = 0; k < levels.Length; k++) levels[k] = r.Levels[r.Keep[k]];
                T.EqualInts(v.Levels, levels, "resolved levels of " + d);
            }
        }

        private static void ReferenceOrder()
        {
            foreach (var v in GoldenVectors.Order)
            {
                int[] index, cps;
                int paragraph;
                Bidi.Reorder(CodePoints.From(v.Text), v.Para, out index, out cps, out paragraph);
                string d = Describe(v.Text, v.Para);
                T.Equal(v.Paragraph, paragraph, "paragraph level (reorder) of " + d);
                T.EqualInts(v.Index, index, "visual order of " + d);
                T.EqualInts(v.CodePoints, cps, "visual code points (mirrored brackets) of " + d);
            }
        }

        private static int[] Levels(string text, int para, out int[] keep, out int paragraph)
        {
            var r = Bidi.ResolveLevels(CodePoints.From(text), para);
            keep = r.Keep;
            paragraph = r.Paragraph;
            var levels = new int[keep.Length];
            for (int k = 0; k < levels.Length; k++) levels[k] = r.Levels[keep[k]];
            return levels;
        }

        private static void ExplicitCases()
        {
            int[] keep;
            int par;

            // P2 / P3: the first strong character decides
            T.EqualInts(new[] { 0, 0, 0 }, Levels("abc", -1, out keep, out par), "Latin text is left-to-right");
            T.Equal(0, par, "the paragraph of Latin text is left-to-right");
            T.EqualInts(new[] { 1, 1, 1 }, Levels("\u05D0\u05D1\u05D2", -1, out keep, out par), "Hebrew text is right-to-left");
            T.Equal(1, par, "the paragraph of Hebrew text is right-to-left");
            T.EqualInts(new[] { 0, 0, 0, 1, 1 }, Levels("ab \u05D0\u05D1", -1, out keep, out par), "Latin first: the Hebrew run is level 1 in a level 0 paragraph");
            T.EqualInts(new[] { 2, 2, 2 }, Levels("abc", 1, out keep, out par), "Latin text inside a forced right-to-left paragraph is level 2");
            T.Equal(1, par, "a forced paragraph level is returned as it is");

            // W2 / W7: European digits after an Arabic letter become Arabic numbers (level 2 in an RTL paragraph)
            T.EqualInts(new[] { 1, 1, 2, 2, 2 }, Levels("\u0627 123", -1, out keep, out par), "digits after an Arabic letter");
            T.EqualInts(new[] { 1, 1, 2, 2, 1, 1 }, Levels("\u0627 12 \u0628", -1, out keep, out par), "digits between two Arabic words");

            // X9: embedding controls disappear
            T.EqualInts(new[] { 0, 2, 4 }, Keep("a\u202Bb\u202Cc"), "RLE and PDF are removed from the text");
            T.EqualInts(new[] { 0, 2, 0 }, Levels("a\u202Bb\u202Cc", -1, out keep, out par), "the letter inside an RLE embedding is level 2");

            // isolates stay in the text
            T.EqualInts(new[] { 0, 1, 2, 3, 4 }, Keep("a\u2067b\u2069c"), "RLI and PDI are kept");

            // nothing at all
            var r = Bidi.ResolveLevels(new int[0], -1);
            T.True(r.Keep.Length == 0 && r.Paragraph == 0, "the empty paragraph has no characters and level 0");

            // L2: visual order
            T.EqualInts(new[] { 2, 1, 0 }, Order("\u05D0\u05D1\u05D2", -1), "a Hebrew word is reversed");
            T.EqualInts(new[] { 0, 1, 2, 4, 3 }, Order("ab \u05D0\u05D1", -1), "only the Hebrew run of a Latin paragraph is reversed");
            T.EqualInts(new[] { 2, 3, 4, 1, 0 }, Order("\u0627 123", -1), "numbers keep their own order inside right-to-left text");
            T.EqualInts(new[] { 5, 4, 2, 3, 1, 0 }, Order("\u0627 12 \u0628", -1), "a number between two Arabic words");

            // L4: brackets are mirrored in right-to-left runs
            int[] index, cps;
            int paragraph;
            Bidi.Reorder(CodePoints.From("(\u0627)"), 1, out index, out cps, out paragraph);
            T.EqualInts(new[] { 2, 1, 0 }, index, "(alef) is reversed");
            T.EqualInts(new[] { 0x28, 0x627, 0x29 }, cps, "the brackets of a right-to-left run are mirrored");

            // a base letter keeps its marks behind it
            T.EqualInts(new[] { 2, 0, 1 }, Order("\u0628\u064B\u0627", 1), "a letter and its mark stay together");

            // the visual text of the empty paragraph
            Bidi.Reorder(new int[0], -1, out index, out cps, out paragraph);
            T.True(index.Length == 0 && cps.Length == 0 && paragraph == 0, "nothing to reorder in the empty paragraph");
        }

        private static int[] Keep(string text)
        {
            return Bidi.ResolveLevels(CodePoints.From(text), -1).Keep;
        }

        private static int[] Order(string text, int para)
        {
            int[] index, cps;
            int paragraph;
            Bidi.Reorder(CodePoints.From(text), para, out index, out cps, out paragraph);
            return index;
        }
    }
}
