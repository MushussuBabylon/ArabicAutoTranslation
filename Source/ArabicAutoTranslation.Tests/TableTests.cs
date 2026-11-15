using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>The Unicode tables the engine is driven by, the version identifiers, the hash and the code point helpers.</summary>
    internal static class TableTests
    {
        internal static void Run()
        {
            T.Group("unicode tables");
            BidiClasses();
            Marks();
            JoiningTypes();
            PresentationForms();
            MirrorsAndBrackets();
            DataSignature();

            T.Group("versions, hash, code points");
            Versions();
            Hashing();
            CodePointHelpers();
        }

        private static string U(int cp)
        {
            return "U+" + cp.ToString("X4", CultureInfo.InvariantCulture);
        }

        private static void Cls(int cp, BidiClass expected)
        {
            var actual = UnicodeData.ClassOf(cp);
            T.Check(actual == expected, "bidi class of " + U(cp), actual == expected ? null : "expected " + expected + " but was " + actual);
        }

        private static void BidiClasses()
        {
            Cls(0x61, BidiClass.L);
            Cls(0x5D0, BidiClass.R);
            Cls(0x627, BidiClass.AL);
            Cls(0x31, BidiClass.EN);
            Cls(0x660, BidiClass.AN);
            Cls(0x6F0, BidiClass.EN);
            Cls(0xB2, BidiClass.EN);
            Cls(0x2B, BidiClass.ES);
            Cls(0x2D, BidiClass.ES);
            Cls(0x2212, BidiClass.ES);
            Cls(0x25, BidiClass.ET);
            Cls(0x24, BidiClass.ET);
            Cls(0xB0, BidiClass.ET);
            Cls(0x2030, BidiClass.ET);
            Cls(0x66A, BidiClass.ET);
            Cls(0x2C, BidiClass.CS);
            Cls(0x2E, BidiClass.CS);
            Cls(0x3A, BidiClass.CS);
            Cls(0xA0, BidiClass.CS);
            Cls(0x60C, BidiClass.CS);
            Cls(0x66B, BidiClass.AN);
            Cls(0x66C, BidiClass.AN);
            Cls(0x64B, BidiClass.NSM);
            Cls(0x300, BidiClass.NSM);
            Cls(0x200C, BidiClass.BN);
            Cls(0x200D, BidiClass.BN);
            Cls(0xAD, BidiClass.BN);
            Cls(0x0A, BidiClass.B);
            Cls(0x0D, BidiClass.B);
            Cls(0x85, BidiClass.B);
            Cls(0x2029, BidiClass.B);
            Cls(0x09, BidiClass.S);
            Cls(0x20, BidiClass.WS);
            Cls(0x2028, BidiClass.WS);
            Cls(0x28, BidiClass.ON);
            Cls(0x2066, BidiClass.LRI);
            Cls(0x2067, BidiClass.RLI);
            Cls(0x2068, BidiClass.FSI);
            Cls(0x2069, BidiClass.PDI);
            Cls(0x202A, BidiClass.LRE);
            Cls(0x202B, BidiClass.RLE);
            Cls(0x202C, BidiClass.PDF);
            Cls(0x202D, BidiClass.LRO);
            Cls(0x202E, BidiClass.RLO);
            Cls(0x61F, BidiClass.AL);
            Cls(0x61B, BidiClass.AL);
            Cls(0x640, BidiClass.AL);
            Cls(0x61C, BidiClass.AL);
            Cls(0xFB50, BidiClass.AL);
            Cls(0xFE91, BidiClass.AL);
            Cls(0xFEFC, BidiClass.AL);
            Cls(0x200E, BidiClass.L);
            Cls(0x200F, BidiClass.R);

            // code points beyond the BMP
            Cls(0x10800, BidiClass.R);
            Cls(0x1E900, BidiClass.R);
            Cls(0x1EE00, BidiClass.AL);
            Cls(0x1D7CE, BidiClass.EN);
            Cls(0x1F600, BidiClass.ON);
            Cls(0x20000, BidiClass.L);
            Cls(0xE0001, BidiClass.BN);

            // the helpers built on the classes
            T.True(UnicodeData.IsStrongRtl(0x5D0), "Hebrew alef is strong right-to-left");
            T.True(UnicodeData.IsStrongRtl(0x627), "Arabic alef is strong right-to-left");
            T.False(UnicodeData.IsStrongRtl(0x61), "Latin a is not strong right-to-left");
            T.False(UnicodeData.IsStrongRtl(0x31), "a European digit is not strong right-to-left");
            T.False(UnicodeData.IsStrongRtl(0x660), "an Arabic-Indic digit is not strong right-to-left");
            T.True(UnicodeData.IsWordLike(0x61) && UnicodeData.IsWordLike(0x627) && UnicodeData.IsWordLike(0x31) && UnicodeData.IsWordLike(0x660),
                "letters and digits are word-like");
            T.False(UnicodeData.IsWordLike(0x20) || UnicodeData.IsWordLike(0x28) || UnicodeData.IsWordLike(0x25),
                "space, brackets and signs are not word-like");
        }

        private static void Marks()
        {
            int[] marks = { 0x64B, 0x651, 0x670, 0x300, 0x20D0, 0x6D6, 0xFE00, 0x1D167 };
            foreach (int cp in marks) T.True(UnicodeData.IsMark(cp), U(cp) + " is a combining mark");
            int[] notMarks = { 0x627, 0x640, 0x6E5, 0x200D, 0x61, 0x20 };
            foreach (int cp in notMarks) T.False(UnicodeData.IsMark(cp), U(cp) + " is not a combining mark");
        }

        private static void Join(int cp, JoinType expected)
        {
            var actual = UnicodeData.JoinTypeOf(cp);
            T.Check(actual == expected, "joining type of " + U(cp), actual == expected ? null : "expected " + expected + " but was " + actual);
        }

        private static void JoiningTypes()
        {
            // dual joining: connect on both sides
            int[] dual = { 0x628, 0x62A, 0x62B, 0x62C, 0x62D, 0x62E, 0x633, 0x634, 0x635, 0x636, 0x637, 0x638, 0x639, 0x63A, 0x641, 0x642,
                           0x643, 0x644, 0x645, 0x646, 0x647, 0x649, 0x64A, 0x626, 0x6A9, 0x6C1, 0x67E, 0x6AF, 0x6CC };
            foreach (int cp in dual) Join(cp, JoinType.D);

            // right joining: connect to the previous letter only
            int[] right = { 0x627, 0x623, 0x625, 0x622, 0x62F, 0x630, 0x631, 0x632, 0x648, 0x624, 0x629, 0x671 };
            foreach (int cp in right) Join(cp, JoinType.R);

            Join(0x621, JoinType.U);     // hamza never joins
            Join(0x640, JoinType.C);     // tatweel
            Join(0x200D, JoinType.C);    // zero width joiner
            Join(0x200C, JoinType.U);    // zero width non-joiner
            Join(0x64B, JoinType.T);     // marks are transparent
            Join(0x651, JoinType.T);
            Join(0x670, JoinType.T);
            Join(0x2066, JoinType.T);    // format characters are transparent
            Join(0x61, JoinType.U);
            Join(0x20, JoinType.U);
        }

        private static void Forms(int baseCp, int iso, int fin, int ini, int med)
        {
            var f = UnicodeData.FormsOf(baseCp);
            if (!T.True(f != null && f.Length == 4, "forms of " + U(baseCp) + " exist")) return;
            T.EqualInts(new[] { iso, fin, ini, med }, f, "forms (isolated, final, initial, medial) of " + U(baseCp));
        }

        private static void PresentationForms()
        {
            Forms(0x628, 0xFE8F, 0xFE90, 0xFE91, 0xFE92);   // beh
            Forms(0x644, 0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0);   // lam
            Forms(0x647, 0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC);   // heh
            Forms(0x64A, 0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4);   // yeh
            Forms(0x649, 0xFEEF, 0xFEF0, 0xFBE8, 0xFBE9);   // alef maksura
            Forms(0x627, 0xFE8D, 0xFE8E, 0, 0);             // alef: right joining, no initial / medial
            Forms(0x629, 0xFE93, 0xFE94, 0, 0);             // teh marbuta
            Forms(0x67E, 0xFB56, 0xFB57, 0xFB58, 0xFB59);   // peh
            Forms(0x6A9, 0xFB8E, 0xFB8F, 0xFB90, 0xFB91);   // keheh
            T.True(UnicodeData.FormsOf(0x61) == null, "Latin letters have no Arabic forms");

            int[] lig = UnicodeData.LamAlefOf(0x627);
            T.True(lig != null && lig.Length == 2, "lam-alef ligature exists");
            if (lig != null && lig.Length == 2) T.EqualInts(new[] { 0xFEFB, 0xFEFC }, lig, "lam + alef ligature (isolated, final)");
            lig = UnicodeData.LamAlefOf(0x623);
            if (lig != null && lig.Length == 2) T.EqualInts(new[] { 0xFEF7, 0xFEF8 }, lig, "lam + alef with hamza above (isolated, final)");
            else T.Fail("lam + alef with hamza above has a ligature");
            lig = UnicodeData.LamAlefOf(0x625);
            if (lig != null && lig.Length == 2) T.EqualInts(new[] { 0xFEF9, 0xFEFA }, lig, "lam + alef with hamza below (isolated, final)");
            else T.Fail("lam + alef with hamza below has a ligature");
            lig = UnicodeData.LamAlefOf(0x622);
            if (lig != null && lig.Length == 2) T.EqualInts(new[] { 0xFEF5, 0xFEF6 }, lig, "lam + alef with madda (isolated, final)");
            else T.Fail("lam + alef with madda has a ligature");
            T.True(UnicodeData.LamAlefOf(0x628) == null, "lam + beh is not a ligature");
            T.True(UnicodeData.LamAlefOf(0x671) == null, "lam + alef wasla is not a ligature");

            int b;
            T.True(UnicodeData.TryUnshape(0xFE91, out b) && b == 0x628, "FE91 unshapes to beh");
            T.True(UnicodeData.TryUnshape(0xFE8D, out b) && b == 0x627, "FE8D unshapes to alef");
            T.True(UnicodeData.TryUnshape(0xFE80, out b) && b == 0x621, "FE80 unshapes to hamza");
            T.True(UnicodeData.TryUnshape(0xFB56, out b) && b == 0x67E, "FB56 unshapes to peh");
            T.False(UnicodeData.TryUnshape(0x628, out b), "a base letter is not a presentation form");
            T.False(UnicodeData.TryUnshape(0x61, out b), "Latin a is not a presentation form");
            int alef;
            T.True(UnicodeData.TryUnshapeLamAlef(0xFEFB, out alef) && alef == 0x627, "FEFB unshapes to lam + alef");
            T.True(UnicodeData.TryUnshapeLamAlef(0xFEF7, out alef) && alef == 0x623, "FEF7 unshapes to lam + alef with hamza above");
            T.True(UnicodeData.TryUnshapeLamAlef(0xFEFC, out alef) && alef == 0x627, "FEFC unshapes to lam + alef");
            T.False(UnicodeData.TryUnshapeLamAlef(0x644, out alef), "a base lam is not a ligature");
        }

        private static void MirrorsAndBrackets()
        {
            T.Equal(0x29, UnicodeData.MirrorOf(0x28), "( mirrors to )");
            T.Equal(0x28, UnicodeData.MirrorOf(0x29), ") mirrors to (");
            T.Equal(0x5D, UnicodeData.MirrorOf(0x5B), "[ mirrors to ]");
            T.Equal(0x7D, UnicodeData.MirrorOf(0x7B), "{ mirrors to }");
            T.Equal(0x3E, UnicodeData.MirrorOf(0x3C), "< mirrors to >");
            T.Equal(0xBB, UnicodeData.MirrorOf(0xAB), "left guillemet mirrors to the right one");
            T.Equal(0x61, UnicodeData.MirrorOf(0x61), "a letter does not mirror");

            int close;
            T.True(UnicodeData.TryGetClosingBracket(0x28, out close) && close == 0x29, "( closes with )");
            T.True(UnicodeData.TryGetClosingBracket(0x5B, out close) && close == 0x5D, "[ closes with ]");
            T.True(UnicodeData.TryGetClosingBracket(0x2329, out close) && close == 0x3009, "the angle bracket U+2329 closes with its canonical equivalent U+3009");
            T.False(UnicodeData.TryGetClosingBracket(0x29, out close), ") does not open a pair");
            T.False(UnicodeData.TryGetClosingBracket(0x61, out close), "a letter does not open a pair");
            T.Equal(0x3008, UnicodeData.CanonBracket(0x2329), "canonical form of U+2329");
            T.Equal(0x3009, UnicodeData.CanonBracket(0x232A), "canonical form of U+232A");
            T.Equal(0x28, UnicodeData.CanonBracket(0x28), "( is its own canonical form");
            T.True(UnicodeData.IsClosingBracket(0x29), ") is a closing bracket");
            T.True(UnicodeData.IsClosingBracket(0x3009), "U+3009 is a closing bracket");
            T.False(UnicodeData.IsClosingBracket(0x28), "( is not a closing bracket");
        }

        /// <summary>The signature constant has to describe the arrays that are really compiled in.</summary>
        private static void DataSignature()
        {
            var arrays = new int[][]
            {
                ArabicTables.BidiRanges, ArabicTables.MirrorPairs, ArabicTables.BracketPairs, ArabicTables.MarkRanges,
                ArabicTables.JoinTable, ArabicTables.FormRows, ArabicTables.LamAlefRows, ArabicTables.UnshapePairs,
                ArabicTables.UnshapeLamAlef
            };
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder();
                foreach (var a in arrays)
                {
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (i > 0) text.Append(',');
                        text.Append(a[i].ToString(CultureInfo.InvariantCulture));
                    }
                    text.Append(';');
                }
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(text.ToString()));
                var hex = new StringBuilder();
                foreach (byte x in hash) hex.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                T.Equal(hex.ToString().Substring(0, 16), ArabicTables.DataSignature, "the data signature matches the compiled tables");
            }
            T.Equal(16, ArabicTables.DataSignature.Length, "the data signature is 16 characters long");
            T.True(ArabicTables.BidiRanges.Length % 3 == 0, "bidi ranges come in triplets");
            T.True(ArabicTables.FormRows.Length % 5 == 0, "form rows come in groups of five");
            T.True(ArabicTables.LamAlefRows.Length % 3 == 0, "lam-alef rows come in triplets");
        }

        private static void Versions()
        {
            T.Equal(1, ArabicVersions.CacheFormat, "cache format");
            T.Equal("ar", ArabicVersions.Language, "cache language");
            T.True(ArabicVersions.ShapingVersion.StartsWith("shape-", StringComparison.Ordinal) &&
                   ArabicVersions.ShapingVersion.EndsWith(ArabicTables.DataSignature, StringComparison.Ordinal),
                "the shaping version carries the data signature");
            T.True(ArabicVersions.BidiVersion.StartsWith("uax9-", StringComparison.Ordinal) &&
                   ArabicVersions.BidiVersion.EndsWith(ArabicTables.DataSignature, StringComparison.Ordinal),
                "the BiDi version carries the data signature");
            T.True(ArabicVersions.DisplayVersion.Length > 0 && ArabicVersions.PipelineVersion.Length > 0, "display and pipeline versions are set");
            T.True(ArabicVersions.ShapingVersion != ArabicVersions.BidiVersion, "shaping and BiDi versions are distinct identifiers");

            var o = new DisplayOptions();
            T.Equal("S1R1I1B1", o.Signature(), "default display options signature");
            o.Shape = false;
            o.LtrIsolates = false;
            T.Equal("S0R1I0B1", o.Signature(), "changed display options signature");
            var d = DisplayOptions.Default();
            T.True(d.Shape && d.Reorder && d.LtrIsolates && d.BaseRtl, "the default options shape, reorder, isolate and use an RTL paragraph");
        }

        private static void Hashing()
        {
            foreach (var v in GoldenVectors.Fnv)
            {
                T.Equal(v[1], Fnv64.Hex(Fnv64.Of(v[0])), "FNV-1a of [" + T.Show(v[0]) + "]");
            }
            T.Equal(GoldenVectors.FnvU64, Fnv64.Hex(Fnv64.AddU64(Fnv64.Start(), 0x0123456789ABCDEFUL)), "FNV-1a of a 64-bit value");
            T.Equal("cbf29ce484222325", Fnv64.Hex(Fnv64.Start()), "the FNV offset basis");
            T.True(Fnv64.AddField(Fnv64.AddField(Fnv64.Start(), "ab"), "c") != Fnv64.AddField(Fnv64.AddField(Fnv64.Start(), "a"), "bc"),
                "AddField separates its fields");
            T.True(Fnv64.Add(Fnv64.Start(), (string)null) == Fnv64.Add(Fnv64.Start(), '\0'), "a null string hashes like a NUL character");
            T.Equal(16, Fnv64.Hex(12345UL).Length, "the hex form always has 16 digits");
            T.Equal("0000000000003039", Fnv64.Hex(12345UL), "the hex form is lower case and zero padded");
            T.True(Fnv64.Of("a") != Fnv64.Of("b"), "different texts hash differently");
        }

        private static void CodePointHelpers()
        {
            string astral = char.ConvertFromUtf32(0x1F600);
            int[] cps = CodePoints.From("a" + astral + "b");
            T.EqualInts(new[] { 97, 0x1F600, 98 }, cps, "a surrogate pair is one code point");
            T.Equal("a" + astral + "b", CodePoints.ToString(cps), "code points round-trip to the same text");

            string lone = ((char)0xD83D).ToString();
            cps = CodePoints.From(lone + "a");
            T.EqualInts(new[] { 0xD83D, 97 }, cps, "an unpaired surrogate is kept as its own value");
            T.Equal(lone + "a", CodePoints.ToString(cps), "an unpaired surrogate round-trips");

            string reversed = ((char)0xDE00).ToString() + ((char)0xD83D).ToString();
            T.Equal(reversed, CodePoints.ToString(CodePoints.From(reversed)), "a reversed surrogate pair round-trips unchanged");

            T.Equal(0, CodePoints.From(null).Length, "no code points in null");
            T.Equal(0, CodePoints.From("").Length, "no code points in the empty text");

            var sb = new StringBuilder();
            CodePoints.Append(sb, 0x41);
            CodePoints.Append(sb, 0x1F600);
            T.Equal("A" + astral, sb.ToString(), "Append handles BMP and supplementary code points");

            var list = new List<int>();
            CodePoints.AddString(list, "x" + astral);
            T.EqualInts(new[] { 0x78, 0x1F600 }, list.ToArray(), "AddString appends the code points of a text");
        }
    }
}
