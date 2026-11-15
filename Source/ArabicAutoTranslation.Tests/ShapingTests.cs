using System;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>Contextual shaping (ArabicShaper): golden vectors from the validated reference plus explicit, documented cases.</summary>
    internal static class ShapingTests
    {
        internal static void Run()
        {
            T.Group("shaping: reference vectors");
            ReferenceVectors();

            T.Group("shaping: explicit cases");
            ExplicitCases();
            Properties();
        }

        private static string Cps(params int[] cps)
        {
            return CodePoints.ToString(cps);
        }

        private static string Shape(string logical)
        {
            int[] shaped, owners;
            ArabicShaper.Shape(CodePoints.From(logical), out shaped, out owners);
            return CodePoints.ToString(shaped);
        }

        private static void ReferenceVectors()
        {
            foreach (var v in GoldenVectors.Shaping)
            {
                int[] shaped, owners;
                ArabicShaper.Shape(CodePoints.From(v.Logical), out shaped, out owners);
                T.EqualInts(v.Shaped, shaped, "shaped form of [" + T.Show(v.Logical) + "]");
                T.EqualInts(v.Owners, owners, "source positions of the shaped characters of [" + T.Show(v.Logical) + "]");
            }
        }

        private static void ExplicitCases()
        {
            // positions: isolated, initial, medial, final
            T.Equal(Cps(0xFE8F), Shape("\u0628"), "a single beh is isolated");
            T.Equal(Cps(0xFE91, 0xFEF4, 0xFE96), Shape("\u0628\u064A\u062A"), "beh yeh teh: initial, medial, final");
            T.Equal(Cps(0xFEDB, 0xFE98, 0xFE8E, 0xFE8F), Shape("\u0643\u062A\u0627\u0628"), "kitab: kaf initial, teh medial, alef final, beh isolated");
            T.Equal(Cps(0xFEE7, 0xFE8E), Shape("\u0646\u0627"), "noon alef: initial, final");

            // right-joining letters never connect to the following letter
            T.Equal(Cps(0xFE8D, 0xFEA9, 0xFEAD), Shape("\u0627\u062F\u0631"), "alef dal reh: none of them connects forward");
            T.Equal(Cps(0xFE91, 0xFE8E, 0xFE8F), Shape("\u0628\u0627\u0628"), "beh alef beh: the alef cuts the word");
            T.Equal(Cps(0xFE91, 0xFE94), Shape("\u0628\u0629"), "beh teh marbuta: initial, final");

            // lam + alef ligatures
            T.Equal(Cps(0xFEFB), Shape("\u0644\u0627"), "lam alef alone is the isolated ligature");
            T.Equal(Cps(0xFE91, 0xFEFC), Shape("\u0628\u0644\u0627"), "lam alef after a connecting letter is the final ligature");
            T.Equal(Cps(0xFE91, 0xFEFC, 0xFE8F), Shape("\u0628\u0644\u0627\u0628"), "a ligature inside a word");
            T.Equal(Cps(0xFEF7), Shape("\u0644\u0623"), "lam + alef with hamza above");
            T.Equal(Cps(0xFEF9), Shape("\u0644\u0625"), "lam + alef with hamza below");
            T.Equal(Cps(0xFEF5), Shape("\u0644\u0622"), "lam + alef with madda");
            T.Equal(Cps(0xFE8D, 0xFEDF, 0xFEE0, 0xFEEA), Shape("\u0627\u0644\u0644\u0647"), "Allah: alef, lam initial, lam medial, heh final");
            T.Equal(Cps(0xFEDF, 0x064E, 0xFE8E), Shape("\u0644\u064E\u0627"), "a mark between lam and alef prevents the ligature");
            T.Equal(Cps(0xFEDF, 0x0640, 0xFE8E), Shape("\u0644\u0640\u0627"), "a tatweel between lam and alef prevents the ligature");

            // letters that never join
            T.Equal(Cps(0x0621), Shape("\u0621"), "hamza stays as it is");
            T.Equal(Cps(0xFE8F, 0x0621), Shape("\u0628\u0621"), "hamza is not connected to the letter before it");
            T.Equal(Cps(0x0621, 0xFE8F), Shape("\u0621\u0628"), "hamza does not connect to the letter after it");
            T.Equal(Cps(0xFE89), Shape("\u0626"), "yeh with hamza above alone is isolated");

            // marks are transparent: they do not break the connection and keep their place behind the letter
            T.Equal(Cps(0xFE8F, 0x064E), Shape("\u0628\u064E"), "beh + fatha");
            T.Equal(Cps(0xFE91, 0x064E, 0xFE90), Shape("\u0628\u064E\u0628"), "a mark between two letters does not cut the word");
            T.Equal(Cps(0xFE91, 0x0651, 0x064E, 0xFE90), Shape("\u0628\u0651\u064E\u0628"), "two marks between two letters");

            // tatweel (kashida) connects on both sides and is kept
            T.Equal(Cps(0xFE91, 0x0640, 0xFE92, 0xFE90), Shape("\u0628\u0640\u0628\u0628"), "beh tatweel beh beh");

            // non-Arabic characters pass through and break the connection
            T.Equal("RimWorld", Shape("RimWorld"), "Latin text is unchanged");
            T.Equal("123", Shape("123"), "digits are unchanged");
            T.Equal(Cps(0xFE8F, 0x20, 0xFE8F), Shape("\u0628 \u0628"), "a space cuts the word");
            T.Equal(Cps(0xFE8F, 0x5A, 0xFE8F), Shape("\u0628Z\u0628"), "a Latin letter cuts the word");
            T.Equal("", Shape(""), "the empty text stays empty");

            // zero width joiner and non-joiner
            T.Equal(Cps(0xFE91, 0x200D, 0xFE90), Shape("\u0628\u200D\u0628"), "ZWJ forces the connection");
            T.Equal(Cps(0xFE8F, 0x200C, 0xFE8F), Shape("\u0628\u200C\u0628"), "ZWNJ breaks the connection");

            // the source positions of a ligature
            int[] shaped, owners;
            ArabicShaper.Shape(CodePoints.From("\u0628\u0644\u0627"), out shaped, out owners);
            T.EqualInts(new[] { 0, 1 }, owners, "the lam alef ligature starts at the lam");
            ArabicShaper.Shape(CodePoints.From("\u0628\u0644\u0627\u0628"), out shaped, out owners);
            T.EqualInts(new[] { 0, 1, 3 }, owners, "the letter after a ligature points at its own source position");
            ArabicShaper.Shape(CodePoints.From(""), out shaped, out owners);
            T.True(shaped.Length == 0 && owners.Length == 0, "nothing to shape in an empty text");
        }

        private static void Properties()
        {
            // unshaping brings every reference vector back to the plain text
            foreach (var v in GoldenVectors.Shaping)
            {
                string shaped = Shape(v.Logical);
                T.Equal(v.Logical, ArabicShaper.Unshape(shaped), "unshape(shape(x)) for [" + T.Show(v.Logical) + "]");
                // shaping already shaped text changes nothing after unshaping it
                T.Equal(shaped, Shape(ArabicShaper.Unshape(shaped)), "shape(unshape(shape(x))) for [" + T.Show(v.Logical) + "]");
            }

            T.False(ArabicShaper.HasPresentationForms("\u0645\u0631\u062D\u0628\u0627"), "plain Arabic has no presentation forms");
            T.False(ArabicShaper.HasPresentationForms("RimWorld 123"), "Latin text has no presentation forms");
            T.False(ArabicShaper.HasPresentationForms(""), "the empty text has no presentation forms");
            T.False(ArabicShaper.HasPresentationForms(null), "null has no presentation forms");
            T.True(ArabicShaper.HasPresentationForms(Shape("\u0645\u0631\u062D\u0628\u0627")), "shaped Arabic has presentation forms");
            T.True(ArabicShaper.HasPresentationForms(Cps(0xFEFB)), "a lam alef ligature is a presentation form");

            T.True(ArabicShaper.Unshape(null) == null, "unshaping null gives null");
            T.Equal("", ArabicShaper.Unshape(""), "unshaping the empty text gives the empty text");
            string plain = "\u0645\u0631\u062D\u0628\u0627 RimWorld";
            T.True(object.ReferenceEquals(plain, ArabicShaper.Unshape(plain)), "plain text is returned as the same string");
            T.Equal("\u0644\u0627", ArabicShaper.Unshape(Cps(0xFEFB)), "the isolated ligature unshapes to lam + alef");
            T.Equal("\u0644\u0623", ArabicShaper.Unshape(Cps(0xFEF8)), "the final ligature with hamza unshapes to lam + alef with hamza");
            T.Equal("a\u0628b", ArabicShaper.Unshape("a" + Cps(0xFE8F) + "b"), "unshaping keeps the characters around a form");
        }
    }
}
