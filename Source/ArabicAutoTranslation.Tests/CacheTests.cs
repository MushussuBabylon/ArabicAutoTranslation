using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// The persistent Arabic cache: the text escaping and the file format, the thread-safe store and its atomic file, the
    /// checks that run when the file is loaded (glossary, shaping, BiDi and option changes, unprocessed entries) and the
    /// migration of the old cache that held raw provider text.
    /// </summary>
    internal static class CacheTests
    {
        private static readonly GlossaryContext Core = new GlossaryContext("ludeon.rimworld", "Keyed.Test", "");
        private static readonly GlossaryContext IdeologyMeme = new GlossaryContext("ludeon.rimworld.ideology", "MemeDef.label", "");

        // one backslash, and the two characters that start the escape of a character: spelled out so that no tool or
        // editor can turn them into the character itself
        private const string Bs = "\\";
        private const string BsU = "\\" + "u";

        private static volatile bool _stop;

        internal static void Run()
        {
            T.Group("cache: text escaping");
            Escaping();

            T.Group("cache: file format");
            EntryBasics();
            Roundtrip();
            Compactness();
            ParsingRobustness();

            T.Group("cache: the store");
            StoreRules();
            Concurrency();

            T.Group("cache: the file");
            FileLifecycle();
            DamagedFiles();
            FailedWrites();

            T.Group("cache: checking entries");
            CurrentEntries();
            ChangedProcessing();
            GlossaryChanges();
            InvalidEntries();
            LoadedFiles();

            T.Group("cache: migrating the old cache");
            LegacyMigration();
            ArabicDetection();
            Reports();

            T.Group("cache: one file per mod");
            FileNames();
            ModFiles();
            SameNameMods();
            OldFileSplit();

            T.Group("cache: texts translated before a fix");
            StaleMultiplierEntries();
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static ArabicPipeline MiniPipeline()
        {
            return Fixtures.PipelineFor(Fixtures.MiniGlossary, null);
        }

        /// <summary>An entry the way the game makes it: the fake provider's answer went through the whole pipeline.</summary>
        private static ArabicCacheEntry Translated(ArabicPipeline p, string key, string source, GlossaryContext ctx, string answer)
        {
            var res = p.TranslateText(source, ctx, c => answer);
            if (!res.Ok) throw new InvalidOperationException("test setup failed for [" + source + "]: " + res.Problem);
            return ArabicCacheEntry.From(key, source, ctx, res, p);
        }

        /// <summary>An entry with texts of the test's choosing and the current processing stamps.</summary>
        private static ArabicCacheEntry Plain(ArabicPipeline p, string key, string source, string logical, string display)
        {
            var e = new ArabicCacheEntry { Key = key, Source = source, Logical = logical, Display = display };
            e.StampCurrent(p);
            return e;
        }

        private static CacheHeader HeaderOf(ArabicPipeline p)
        {
            return new CacheHeader
            {
                GlossaryVersion = p.GlossaryVersion,
                PipelineVersion = ArabicVersions.PipelineVersion,
                ShapingVersion = ArabicVersions.ShapingVersion,
                BidiVersion = ArabicVersions.BidiVersion,
                DisplayVersion = p.DisplayVersion
            };
        }

        private static void SameEntry(ArabicCacheEntry x, ArabicCacheEntry y, string what)
        {
            T.Equal(x.Key, y.Key, what + ": key");
            T.Equal(x.Source, y.Source, what + ": source");
            T.Equal(x.Logical, y.Logical, what + ": logical text");
            T.Equal(x.Display, y.Display, what + ": display text");
            T.Equal((int)x.Status, (int)y.Status, what + ": status");
            T.Equal((int)x.Origin, (int)y.Origin, what + ": origin");
            T.Equal(x.GlossaryVersion, y.GlossaryVersion, what + ": glossary version");
            T.Equal(x.GlossarySig, y.GlossarySig, what + ": glossary signature");
            T.Equal(x.PipelineVersion, y.PipelineVersion, what + ": pipeline version");
            T.Equal(x.ShapingVersion, y.ShapingVersion, what + ": shaping version");
            T.Equal(x.BidiVersion, y.BidiVersion, what + ": bidi version");
            T.Equal(x.DisplayVersion, y.DisplayVersion, what + ": display version");
            T.Equal(x.Format, y.Format, what + ": format");
            T.Equal(x.Language, y.Language, what + ": language");
            T.Equal(x.ModId, y.ModId, what + ": mod id");
            T.Equal(x.ContextKey, y.ContextKey, what + ": context key");
            T.Equal(x.ContextHint, y.ContextHint, what + ": context hint");
        }

        private static bool ThrowsXml(string xml)
        {
            try
            {
                ArabicCacheFormat.Parse(xml);
                return false;
            }
            catch (XmlException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------ escaping

        private static void Escaping()
        {
            T.Equal("", ArabicCacheFormat.Escape(null), "null");
            T.Equal("", ArabicCacheFormat.Escape(""), "the empty text");

            string plain = "\u0645\u0631\u062D\u0628\u0627 RimWorld {0} <b>x</b> & 'q' \"d\" %";
            T.True(object.ReferenceEquals(plain, ArabicCacheFormat.Escape(plain)), "text without special characters is returned as it is");

            T.Equal("a" + Bs + Bs + "b", ArabicCacheFormat.Escape("a" + Bs + "b"), "a backslash");
            T.Equal("a" + Bs + "nb", ArabicCacheFormat.Escape("a\nb"), "a line feed");
            T.Equal("a" + Bs + "rb", ArabicCacheFormat.Escape("a\rb"), "a carriage return");
            T.Equal("a" + Bs + "tb", ArabicCacheFormat.Escape("a\tb"), "a tab");
            T.Equal("a" + BsU + "0001b", ArabicCacheFormat.Escape("a" + (char)1 + "b"), "a control character");
            T.Equal(BsU + "001F", ArabicCacheFormat.Escape(((char)0x1F).ToString()), "the last control character");
            T.Equal(BsU + "FFFE" + BsU + "FFFF", ArabicCacheFormat.Escape(((char)0xFFFE).ToString() + (char)0xFFFF), "the two noncharacters XML cannot carry");
            T.Equal(BsU + "D83D", ArabicCacheFormat.Escape(((char)0xD83D).ToString()), "a lone high surrogate");
            T.Equal(BsU + "DE00", ArabicCacheFormat.Escape(((char)0xDE00).ToString()), "a lone low surrogate");
            T.Equal(BsU + "D83Dx", ArabicCacheFormat.Escape(((char)0xD83D).ToString() + "x"), "a high surrogate before an ordinary character");

            string pair = char.ConvertFromUtf32(0x1F600);
            T.Equal(pair, ArabicCacheFormat.Escape(pair), "a valid surrogate pair stays");
            T.Equal("a" + pair + "b", ArabicCacheFormat.Escape("a" + pair + "b"), "also between other text");
            T.Equal(((char)0x2028).ToString() + (char)0x7F, ArabicCacheFormat.Escape(((char)0x2028).ToString() + (char)0x7F), "characters XML can carry stay");

            // every text comes back as it was
            string[] samples =
            {
                "", "plain", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", "a" + Bs + "b", Bs, Bs + Bs, Bs + "n", Bs + "u0041", Bs + "n" + Bs, "x\ny\r\nz\tw",
                "a" + (char)1 + (char)2 + (char)0x1F + "b", ((char)0xD83D).ToString(), ((char)0xDE00).ToString(),
                ((char)0xDE00).ToString() + (char)0xD83D, "a" + pair + "b", ((char)0xFFFE).ToString() + (char)0xFFFF,
                "{0} <b>x</b> & \"q\" 'r' " + Bs + "n" + "\n", new string('x', 1000) + "\n" + new string('y', 1000)
            };
            foreach (var s in samples)
            {
                string back = ArabicCacheFormat.Unescape(ArabicCacheFormat.Escape(s));
                T.Equal(s, back, "round trip [" + T.Show(s) + "]");
            }

            // reading is lenient
            T.True(ArabicCacheFormat.Unescape(null) == null, "null");
            T.Equal("", ArabicCacheFormat.Unescape(""), "the empty text");
            T.True(object.ReferenceEquals("abc", ArabicCacheFormat.Unescape("abc")), "text without a backslash is returned as it is");
            T.Equal("a" + Bs + "qb", ArabicCacheFormat.Unescape("a" + Bs + "qb"), "an unknown escape stays as it is");
            T.Equal("abc" + Bs, ArabicCacheFormat.Unescape("abc" + Bs), "a trailing backslash stays");
            T.Equal(BsU + "12", ArabicCacheFormat.Unescape(BsU + "12"), "a truncated character escape stays");
            T.Equal(BsU + "zzzz", ArabicCacheFormat.Unescape(BsU + "zzzz"), "an invalid character escape stays");
            T.Equal("\u00E9", ArabicCacheFormat.Unescape(BsU + "00e9"), "lower case digits are read");
            T.Equal("\u00E9", ArabicCacheFormat.Unescape(BsU + "00E9"), "upper case digits are read");
        }

        // ------------------------------------------------------------------------------------------ entries

        private static void EntryBasics()
        {
            var p = MiniPipeline();
            var e = Translated(p, "ludeon.rimworld:The colonist is hungry.", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639.");

            T.True(e.Status == CacheStatus.Final, "an entry made by the pipeline is final");
            T.True(e.Origin == PipelineOrigin.Translator, "origin");
            T.Equal(ArabicVersions.Language, e.Language, "language");
            T.Equal(ArabicVersions.CacheFormat, e.Format, "format");
            T.Equal(p.GlossaryVersion, e.GlossaryVersion, "glossary version is stamped");
            T.Equal(ArabicVersions.PipelineVersion, e.PipelineVersion, "pipeline version is stamped");
            T.Equal(ArabicVersions.ShapingVersion, e.ShapingVersion, "shaping version is stamped");
            T.Equal(ArabicVersions.BidiVersion, e.BidiVersion, "bidi version is stamped");
            T.Equal(p.DisplayVersion, e.DisplayVersion, "display version is stamped");
            T.Equal("ludeon.rimworld", e.ModId, "the mod of the context");
            T.Equal("Keyed.Test", e.ContextKey, "the key of the context");
            T.Equal("", e.ContextHint, "the hint of the context");
            T.Equal(p.GlossarySignatureOf("The colonist is hungry.", Core), e.GlossarySig, "the glossary signature");
            T.False(e.DisplayOutdated(p), "a current entry is not outdated");

            var ctx = e.ToGlossaryContext();
            T.True(ctx.ModPackageId == "ludeon.rimworld" && ctx.Key == "Keyed.Test" && ctx.Hint == "", "the context can be rebuilt");

            var copy = e.Clone();
            copy.Display = "x";
            copy.Key = "other";
            T.True(e.Display != "x" && e.Key != "other", "a clone is independent");

            // every identifier on its own makes the display form outdated
            var c1 = e.Clone(); c1.PipelineVersion = "pipe-0";
            var c2 = e.Clone(); c2.ShapingVersion = "shape-0";
            var c3 = e.Clone(); c3.BidiVersion = "uax9-0";
            var c4 = e.Clone(); c4.DisplayVersion = "display-0;S1R1I1B1";
            T.True(c1.DisplayOutdated(p) && c2.DisplayOutdated(p) && c3.DisplayOutdated(p) && c4.DisplayOutdated(p), "each identifier is checked");
            var c5 = e.Clone(); c5.GlossaryVersion = "gl-0.old";
            T.False(c5.DisplayOutdated(p), "the glossary does not concern the display form");

            // another option set is another display version
            var shapeOnly = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Reorder = false });
            T.True(e.DisplayOutdated(shapeOnly), "other display options make the display form outdated");

            // an entry without a context
            var res = p.TranslateText("Hello world", Core, c => "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645");
            var noCtx = ArabicCacheEntry.From("k", "Hello world", null, res, p);
            T.True(noCtx.ModId == "" && noCtx.ContextKey == "" && noCtx.ContextHint == "", "no context, no context fields");
            T.Equal("0", noCtx.GlossarySig, "no terms, signature 0");
        }

        // ------------------------------------------------------------------------------------------ file format

        private static void Roundtrip()
        {
            var p = MiniPipeline();
            var header = HeaderOf(p);
            header.LegacyHandled = true;

            var entries = new List<ArabicCacheEntry>();
            var a = Translated(p, "ludeon.rimworld:The colonist is hungry.", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639.");
            entries.Add(a);

            string awkward = "line1\nline2\r\nline3\ttab " + Bs + " backslash <tag a=\"1\"> & 'q' " + char.ConvertFromUtf32(0x1F600) + " " +
                             (char)1 + (char)0xD83D + (char)0xFFFE + "  two  spaces ";
            entries.Add(Plain(p, "mod.b:" + awkward, awkward, awkward, awkward));

            var withContext = a.Clone();
            withContext.Key = "k-context";
            withContext.ModId = "ludeon.rimworld.ideology";
            withContext.ContextKey = "MemeDef.label";
            withContext.ContextHint = "female";
            withContext.GlossarySig = "0123456789abcdef";
            withContext.Origin = PipelineOrigin.UserEdit;
            entries.Add(withContext);

            var old = a.Clone();
            old.Key = "k-old";
            old.GlossaryVersion = "gl-0.old";
            old.PipelineVersion = "pipe-0";
            old.ShapingVersion = "shape-0";
            old.BidiVersion = "uax9-0";
            old.DisplayVersion = "display-0";
            old.Format = 7;
            old.Language = "en";
            old.Origin = PipelineOrigin.Migrated;
            entries.Add(old);

            var corrected = a.Clone();
            corrected.Key = "k-corrected";
            corrected.Status = CacheStatus.Corrected;
            corrected.Origin = PipelineOrigin.Offline;
            entries.Add(corrected);

            var raw = a.Clone();
            raw.Key = "k-raw";
            raw.Status = CacheStatus.Raw;
            raw.Origin = PipelineOrigin.Regenerated;
            entries.Add(raw);

            string xml = ArabicCacheFormat.Serialize(entries, header);
            var parsed = ArabicCacheFormat.Parse(xml);

            T.Equal(entries.Count, parsed.Entries.Count, "every entry is read back");
            T.Equal(0, parsed.Malformed, "none is malformed");
            T.Equal(ArabicVersions.CacheFormat, parsed.Header.Format, "header: format");
            T.Equal(ArabicVersions.Language, parsed.Header.Language, "header: language");
            T.Equal(header.GlossaryVersion, parsed.Header.GlossaryVersion, "header: glossary version");
            T.Equal(header.PipelineVersion, parsed.Header.PipelineVersion, "header: pipeline version");
            T.Equal(header.ShapingVersion, parsed.Header.ShapingVersion, "header: shaping version");
            T.Equal(header.BidiVersion, parsed.Header.BidiVersion, "header: bidi version");
            T.Equal(header.DisplayVersion, parsed.Header.DisplayVersion, "header: display version");
            T.True(parsed.Header.LegacyHandled, "header: the migration flag");

            for (int i = 0; i < entries.Count && i < parsed.Entries.Count; i++)
            {
                SameEntry(entries[i], parsed.Entries[i], "entry " + i);
            }

            // the file is plain XML: no control characters, no noncharacters, no unpaired surrogates
            bool clean = true;
            for (int i = 0; i < xml.Length; i++)
            {
                char c = xml[i];
                if ((c < ' ' && c != '\n' && c != '\r' && c != '\t') || c == '\uFFFE' || c == '\uFFFF') clean = false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < xml.Length && char.IsLowSurrogate(xml[i + 1])) i++;
                    else clean = false;
                }
                else if (char.IsLowSurrogate(c)) clean = false;
            }
            T.True(clean, "the file text can be written as UTF-8 XML");
            T.True(xml.Contains("<" + ArabicCacheFormat.RootName + " "), "the root element");
            T.True(xml.Contains("<" + ArabicCacheFormat.EntryName + " "), "the entry elements");

            // the order of the entries is kept, and an empty cache is a valid file
            var reversed = new List<ArabicCacheEntry>(entries);
            reversed.Reverse();
            var parsedReversed = ArabicCacheFormat.Parse(ArabicCacheFormat.Serialize(reversed, header));
            T.True(parsedReversed.Entries.Count == entries.Count && parsedReversed.Entries[0].Key == entries[entries.Count - 1].Key, "entries are written in the order given");
            var empty = ArabicCacheFormat.Parse(ArabicCacheFormat.Serialize(new List<ArabicCacheEntry>(), header));
            T.True(empty.Entries.Count == 0 && empty.Header.GlossaryVersion == header.GlossaryVersion, "an empty cache");
        }

        private static void Compactness()
        {
            var p = MiniPipeline();
            var header = HeaderOf(p);

            // an entry that matches the header only stores what the header does not say
            var plain = Plain(p, "k", "src", "logical", "display");
            string xml = ArabicCacheFormat.Serialize(new[] { plain }, header);
            foreach (var attr in new[] { " gv=", " pv=", " sv=", " bv=", " dv=", " f=", " lg=", " gs=", " m=", " ck=", " ch=" })
            {
                T.False(xml.Contains(attr), "no" + attr + "attribute for an entry that matches the header");
            }
            T.True(xml.Contains(" k=\"k\"") && xml.Contains(" s=\"src\"") && xml.Contains(" l=\"logical\"") && xml.Contains(" d=\"display\""), "the texts are stored");
            T.True(xml.Contains(" st=\"f\"") && xml.Contains(" o=\"t\""), "status and origin are stored");

            var full = plain.Clone();
            full.GlossarySig = "abc";
            full.ModId = "m1";
            full.ContextKey = "K.f";
            full.ContextHint = "female";
            full.GlossaryVersion = "g";
            full.PipelineVersion = "p";
            full.ShapingVersion = "s";
            full.BidiVersion = "b";
            full.DisplayVersion = "d";
            full.Format = 3;
            full.Language = "en";
            xml = ArabicCacheFormat.Serialize(new[] { full }, header);
            foreach (var attr in new[] { " gv=", " pv=", " sv=", " bv=", " dv=", " f=", " lg=", " gs=", " m=", " ck=", " ch=" })
            {
                T.True(xml.Contains(attr), "the" + attr + "attribute is written when it differs");
            }
        }

        private static void ParsingRobustness()
        {
            T.True(ThrowsXml(""), "the empty text is damaged");
            T.True(ThrowsXml(null), "null is damaged");
            T.True(ThrowsXml("garbage"), "text that is not XML is damaged");
            T.True(ThrowsXml("<other/>"), "XML that is not a cache is damaged");
            T.True(ThrowsXml("<ArabicCache"), "a truncated file is damaged");
            T.True(ThrowsXml("<ArabicCache><E k=\"a\""), "a file cut inside an entry is damaged");
            T.True(ThrowsXml("<!DOCTYPE x [<!ENTITY a \"b\">]><ArabicCache/>"), "a document type declaration is refused");

            var bare = ArabicCacheFormat.Parse("<ArabicCache/>");
            T.True(bare.Entries.Count == 0 && bare.Malformed == 0, "a cache without entries");
            T.Equal(0, bare.Header.Format, "a missing format reads as 0, which no entry accepts");
            T.False(bare.Header.LegacyHandled, "a missing migration flag reads as not migrated");

            string doc =
                "<ArabicCache format=\"1\" language=\"ar\" glossary=\"g\" pipeline=\"p\" shaping=\"s\" bidi=\"b\" display=\"d\" legacyHandled=\"0\">" +
                "<E k=\"k1\" s=\"a &amp; b\" l=\"l1\" d=\"d1\" st=\"f\" o=\"u\" gs=\"abc\" m=\"mod.x\" ck=\"Key.field\" ch=\"female\" gv=\"g2\" pv=\"p2\" sv=\"s2\" bv=\"b2\" dv=\"d2\" f=\"3\" lg=\"en\" />" +
                "<E k=\"k2\" s=\"s2\" l=\"l2\" d=\"d2\" />" +
                "<E k=\"k3\" s=\"s3\" l=\"l3\" />" +
                "<E k=\"k4\" s=\"\" l=\"\" d=\"\" st=\"c\" o=\"o\" />" +
                "<E k=\"k5\" s=\"5\" l=\"5\" d=\"5\" st=\"f\" o=\"m\" />" +
                "<E k=\"k6\" s=\"6\" l=\"6\" d=\"6\" st=\"f\" o=\"g\" />" +
                "<E k=\"k7\" s=\"7\" l=\"7\" d=\"7\" st=\"f\" o=\"x\" />" +
                "<E k=\"a" + Bs + "nb\" s=\"s\" l=\"l\" d=\"d\" st=\"f\" />" +
                "<Unknown k=\"k9\" />" +
                "</ArabicCache>";
            var parsed = ArabicCacheFormat.Parse(doc);
            T.Equal(7, parsed.Entries.Count, "seven readable entries");
            T.Equal(1, parsed.Malformed, "one entry lacks its display text");
            if (parsed.Entries.Count == 7)
            {
                var k1 = parsed.Entries[0];
                T.True(k1.Key == "k1" && k1.Source == "a & b" && k1.Logical == "l1" && k1.Display == "d1", "texts, with the XML entity read");
                T.True(k1.Status == CacheStatus.Final && k1.Origin == PipelineOrigin.UserEdit, "status and origin");
                T.True(k1.GlossarySig == "abc" && k1.ModId == "mod.x" && k1.ContextKey == "Key.field" && k1.ContextHint == "female", "signature and context");
                T.True(k1.GlossaryVersion == "g2" && k1.PipelineVersion == "p2" && k1.ShapingVersion == "s2" && k1.BidiVersion == "b2" && k1.DisplayVersion == "d2", "own versions");
                T.True(k1.Format == 3 && k1.Language == "en", "own format and language");

                var k2 = parsed.Entries[1];
                T.True(k2.Status == CacheStatus.Raw, "a missing status never reads as final");
                T.True(k2.Origin == PipelineOrigin.Translator, "a missing origin is the translator");
                T.True(k2.GlossaryVersion == "g" && k2.PipelineVersion == "p" && k2.ShapingVersion == "s" && k2.BidiVersion == "b" && k2.DisplayVersion == "d",
                    "versions come from the header");
                T.True(k2.Format == 1 && k2.Language == "ar" && k2.GlossarySig == "0", "format, language and signature defaults");

                var k4 = parsed.Entries[2];
                T.True(k4.Key == "k4" && k4.Source == "" && k4.Logical == "" && k4.Display == "", "empty texts are read as empty");
                T.True(k4.Status == CacheStatus.Corrected && k4.Origin == PipelineOrigin.Offline, "corrected, offline");
                T.True(parsed.Entries[3].Origin == PipelineOrigin.Migrated, "origin m");
                T.True(parsed.Entries[4].Origin == PipelineOrigin.Regenerated, "origin g");
                T.True(parsed.Entries[5].Origin == PipelineOrigin.Translator, "an unknown origin is the translator");
                T.Equal("a\nb", parsed.Entries[6].Key, "escapes in attributes are read");
            }
        }

        // ------------------------------------------------------------------------------------------ store

        private static void StoreRules()
        {
            var p = MiniPipeline();
            var store = new ArabicCacheStore(null);
            T.True(store.FilePath == null && store.Count == 0 && !store.IsDirty && !store.LegacyHandled && store.LastError == null && !store.LoadedFromBackup,
                "a new store is empty and clean");

            var good = Plain(p, "k1", "src", "logical", "display");
            T.True(store.Put(good), "a final entry is accepted");
            T.Equal(1, store.Count, "one entry");
            ArabicCacheEntry got;
            T.True(store.TryGet("k1", out got) && object.ReferenceEquals(got, good), "and returned as it was stored");
            T.True(store.ContainsKey("k1") && !store.ContainsKey("k2") && !store.ContainsKey(null), "ContainsKey");
            T.True(!store.TryGet(null, out got) && got == null, "a null key finds nothing");
            T.True(!store.TryGet("k2", out got) && got == null, "an unknown key finds nothing");
            T.True(store.IsDirty, "a new entry makes the store dirty");

            // only fully processed entries are ever stored
            var raw = good.Clone(); raw.Key = "raw"; raw.Status = CacheStatus.Raw;
            var corrected = good.Clone(); corrected.Key = "corrected"; corrected.Status = CacheStatus.Corrected;
            var noDisplay = good.Clone(); noDisplay.Key = "nd"; noDisplay.Display = "";
            var noLogical = good.Clone(); noLogical.Key = "nl"; noLogical.Logical = "";
            var noKey = good.Clone(); noKey.Key = null;
            var emptyKey = good.Clone(); emptyKey.Key = "";
            foreach (var bad in new[] { raw, corrected, noDisplay, noLogical, noKey, emptyKey })
            {
                T.False(store.Put(bad), "Put refuses [" + bad.Key + "] status " + bad.Status);
                T.False(store.PutLoaded(bad), "PutLoaded refuses [" + bad.Key + "] status " + bad.Status);
            }
            T.False(store.Put(null), "Put refuses null");
            T.False(store.PutLoaded(null), "PutLoaded refuses null");
            T.Equal(1, store.Count, "none of them was stored");
            T.False(store.ContainsKey("raw") || store.ContainsKey("corrected"), "raw text is never a cache hit");

            // an entry is replaced as a whole
            var newer = good.Clone();
            newer.Logical = "logical 2";
            newer.Display = "display 2";
            T.True(store.Put(newer), "an entry can be replaced");
            T.True(store.TryGet("k1", out got) && object.ReferenceEquals(got, newer), "the new object is returned");
            T.True(good.Display == "display" && good.Logical == "logical", "the old object was not changed");
            T.Equal(1, store.Count, "still one entry");

            // dirty tracking
            var loaded = new ArabicCacheStore(null);
            loaded.PutLoaded(good);
            T.False(loaded.IsDirty, "an entry read from the file does not make the cache dirty");
            T.Equal(1, loaded.Count, "but it is there");
            loaded.MarkDirty();
            T.True(loaded.IsDirty, "MarkDirty");

            var clean = new ArabicCacheStore(null);
            clean.Clear();
            T.False(clean.IsDirty, "clearing an empty store changes nothing");

            // removing and clearing
            T.True(store.Remove("k1"), "Remove");
            T.False(store.Remove("k1"), "Remove of a missing key");
            T.False(store.Remove(null), "Remove of null");
            T.Equal(0, store.Count, "empty");
            store.Put(Plain(p, "a", "s", "l", "d"));
            store.Put(Plain(p, "b", "s", "l", "d"));
            var snapshot = store.Snapshot();
            store.Put(Plain(p, "c", "s", "l", "d"));
            T.Equal(2, snapshot.Count, "a snapshot does not follow the store");
            snapshot.Clear();
            T.Equal(3, store.Count, "and changing it does not change the store");
            store.Clear();
            T.Equal(0, store.Count, "Clear");

            // a store without a file
            T.False(store.Save(p), "a memory-only store cannot be saved");
            T.True(store.ReadFile() == null, "or read");
        }

        private static void Concurrency()
        {
            var p = MiniPipeline();
            var store = new ArabicCacheStore(null);
            var errors = new List<string>();
            const int writers = 6;
            const int perWriter = 300;

            var threads = new List<Thread>();
            for (int w = 0; w < writers; w++)
            {
                int id = w;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < perWriter; i++)
                        {
                            var e = new ArabicCacheEntry { Key = "k" + (i % 40), Source = "s" + id, Logical = "L" + id + "_" + i, Display = "D" + id + "_" + i };
                            e.StampCurrent(p);
                            store.Put(e);
                            if (i % 7 == 0) store.Snapshot();
                            if (i % 11 == 0)
                            {
                                ArabicCacheEntry got;
                                store.TryGet("k" + (i % 40), out got);
                            }
                            if (i % 50 == 49) store.Remove("k" + (i % 40));
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex.GetType().Name + ": " + ex.Message);
                    }
                }));
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            T.Equal(0, errors.Count, "no thread failed: " + string.Join("; ", errors.ToArray()));
            T.True(store.Count <= 40, "no more entries than keys (" + store.Count + ")");
            bool whole = true;
            foreach (var e in store.Snapshot())
            {
                // an entry is always one writer's complete entry, never a mixture of two
                string l = e.Logical.Substring(1);
                string d = e.Display.Substring(1);
                int cut = l.IndexOf('_');
                if (l != d || cut < 1 || e.Source.Substring(1) != l.Substring(0, cut)) whole = false;
            }
            T.True(whole, "every entry is one writer's complete entry");

            // writers and a saver at the same time: the file stays a complete cache
            string dir = Fixtures.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "ArabicCache.xml");
                var filed = new ArabicCacheStore(path);
                errors.Clear();
                _stop = false;
                var saver = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 400 && !_stop; i++)
                        {
                            filed.Save(p);
                            Thread.Sleep(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add("saver " + ex.GetType().Name + ": " + ex.Message);
                    }
                });
                var puts = new List<Thread>();
                for (int w = 0; w < 4; w++)
                {
                    int id = w;
                    puts.Add(new Thread(() =>
                    {
                        try
                        {
                            for (int i = 0; i < 150; i++)
                            {
                                var e = new ArabicCacheEntry { Key = "w" + id + "_" + i, Source = "s", Logical = "l" + i, Display = "d" + i };
                                e.StampCurrent(p);
                                filed.Put(e);
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add("writer " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }));
                }
                saver.Start();
                foreach (var t in puts) t.Start();
                foreach (var t in puts) t.Join();
                _stop = true;
                saver.Join();

                T.Equal(0, errors.Count, "writers and saver finished without errors: " + string.Join("; ", errors.ToArray()));
                T.True(filed.Save(p), "the final save works: " + filed.LastError);
                T.Equal(600, filed.Count, "every entry is in the store");
                var reread = ArabicCacheFormat.Parse(File.ReadAllText(path, Encoding.UTF8));
                T.Equal(600, reread.Entries.Count, "and in the file");
                T.False(File.Exists(path + ".tmp"), "no temporary file is left");
            }
            finally
            {
                _stop = true;
                Fixtures.DeleteTempDir(dir);
            }
        }

        // ------------------------------------------------------------------------------------------ the file

        private static void FileLifecycle()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "sub" + Path.DirectorySeparatorChar + "ArabicCache.xml");
                var p = MiniPipeline();
                var store = new ArabicCacheStore(path);
                T.True(store.ReadFile() == null && store.LastError == null, "no file is no error");

                store.Put(Translated(p, "k1", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639."));
                store.Put(Translated(p, "k2", "Colonist", Core, "x"));
                store.Put(Translated(p, "k3", "Hello world", Core, "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"));
                store.LegacyHandled = true;
                T.True(store.IsDirty, "new entries");
                T.True(store.Save(p), "saved: " + store.LastError);
                T.True(File.Exists(path), "the file exists (the folder was created)");
                T.False(File.Exists(path + ".tmp"), "the temporary file is gone");
                T.False(File.Exists(path + ".bak"), "the first save has nothing to back up");
                T.False(store.IsDirty, "saved means clean");
                T.True(store.LastError == null, "no error");

                store.Put(Translated(p, "k4", "Slag", Core, "x"));
                T.True(store.IsDirty, "a new entry makes it dirty again");
                T.True(store.Save(p), "saved again: " + store.LastError);
                T.True(File.Exists(path + ".bak"), "the previous generation stays as the backup");
                T.False(File.Exists(path + ".tmp"), "no temporary file");

                // the file: UTF-8 without a byte order mark
                byte[] bytes = File.ReadAllBytes(path);
                T.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no byte order mark");
                string text = Encoding.UTF8.GetString(bytes);
                T.True(text.Contains(p.GlossaryVersion), "the header names the glossary version");
                T.True(text.Contains(ArabicVersions.ShapingVersion), "the header names the shaping version");

                // after a restart: the entries come back exactly as they were, and nothing is processed again
                var again = new ArabicCacheStore(path);
                var report = ArabicCacheMaintenance.LoadAndCheck(again, p);
                T.Equal(4, report.Loaded, "four entries read");
                T.Equal(4, report.Valid, "all are valid as they are");
                T.False(report.Changed, "nothing had to change");
                T.Equal(4, again.Count, "four entries in the store");
                T.False(again.IsDirty, "a clean load does not make the cache dirty");
                T.True(again.LegacyHandled, "the migration flag survives");
                T.False(again.LoadedFromBackup, "the main file was used");
                foreach (var e in store.Snapshot())
                {
                    ArabicCacheEntry back;
                    if (T.True(again.TryGet(e.Key, out back), "entry " + e.Key + " survives the restart")) SameEntry(e, back, "restart " + e.Key);
                }

                // a cache hit is the stored display string itself
                ArabicCacheEntry hit;
                T.True(again.TryGet("k1", out hit) && hit.Display == p.RenderDisplay("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639."), "the stored text is the display-ready text");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        private static void DamagedFiles()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "ArabicCache.xml");
                var p = MiniPipeline();
                var store = new ArabicCacheStore(path);
                store.Put(Translated(p, "k1", "Colonist", Core, "x"));
                store.Put(Translated(p, "k2", "Hello world", Core, "\u0645\u0631\u062D\u0628\u0627"));
                store.Save(p);
                store.Put(Translated(p, "k3", "Slag", Core, "x"));
                store.Save(p);

                // the main file is damaged: the previous generation is used and the cache is rewritten
                File.WriteAllText(path, "<<< not xml", new UTF8Encoding(false));
                var s1 = new ArabicCacheStore(path);
                var r1 = ArabicCacheMaintenance.LoadAndCheck(s1, p);
                T.True(s1.LoadedFromBackup, "the backup was used");
                T.Equal(2, s1.Count, "it holds the previous generation");
                T.Equal(2, r1.Valid, "both entries are valid");
                T.True(s1.IsDirty, "the cache is marked for rewriting");
                T.True(s1.LastError != null && s1.LastError.Contains(path), "the problem is reported");
                T.True(File.Exists(path + ".corrupt"), "the damaged file is kept for inspection");
                T.True(s1.Save(p), "the cache is rewritten: " + s1.LastError);
                var s2 = new ArabicCacheStore(path);
                ArabicCacheMaintenance.LoadAndCheck(s2, p);
                T.True(!s2.LoadedFromBackup && s2.Count == 2, "after the rewrite the main file is used again");

                // both files damaged: nothing is loaded and nothing throws
                File.WriteAllText(path, "x", new UTF8Encoding(false));
                File.WriteAllText(path + ".bak", "y", new UTF8Encoding(false));
                var s3 = new ArabicCacheStore(path);
                var r3 = ArabicCacheMaintenance.LoadAndCheck(s3, p);
                T.True(s3.Count == 0 && r3.Loaded == 0 && !r3.Changed, "nothing is loaded");
                T.True(s3.LastError != null, "the problem is reported");
                T.False(s3.LoadedFromBackup, "the backup was not used");
                T.False(s3.IsDirty, "an empty cache is not dirty");

                // XML that is not a cache counts as damaged too
                File.WriteAllText(path, "<settings/>", new UTF8Encoding(false));
                File.Delete(path + ".bak");
                var s4 = new ArabicCacheStore(path);
                T.True(s4.ReadFile() == null && s4.LastError != null, "a foreign XML file is damaged");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        private static void FailedWrites()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                var p = MiniPipeline();
                string path = Path.Combine(dir, "c.xml");
                var store = new ArabicCacheStore(path);
                store.Put(Translated(p, "k1", "Colonist", Core, "x"));
                T.True(store.Save(p), "first save: " + store.LastError);
                byte[] before = File.ReadAllBytes(path);

                // the temporary file cannot be created: the save fails and nothing that exists is touched
                Directory.CreateDirectory(path + ".tmp");
                store.Put(Translated(p, "k2", "Slag", Core, "x"));
                T.False(store.Save(p), "the write fails");
                T.True(store.LastError != null, "the problem is reported");
                T.True(store.IsDirty, "the cache stays dirty, so the next save tries again");
                T.True(File.Exists(path) && SameBytes(before, File.ReadAllBytes(path)), "the previous file is untouched");
                T.Equal(2, store.Count, "the entries in memory are untouched");
                Directory.Delete(path + ".tmp");
                T.True(store.Save(p), "the next save works: " + store.LastError);
                T.False(store.IsDirty, "clean again");
                T.True(store.LastError == null, "the error is gone");

                // a location that cannot exist
                string blocker = Path.Combine(dir, "blocker");
                File.WriteAllText(blocker, "x");
                var bad = new ArabicCacheStore(Path.Combine(blocker, "ArabicCache.xml"));
                bad.Put(Translated(p, "k1", "Colonist", Core, "x"));
                T.False(bad.Save(p), "a location that cannot exist");
                T.True(bad.LastError != null && bad.IsDirty && bad.Count == 1, "reported, still dirty, entries kept");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        // ------------------------------------------------------------------------------------------ checks

        private static void CurrentEntries()
        {
            var p = MiniPipeline();
            string[][] cases =
            {
                new[] { "The colonist is hungry.", "{GL0} \u062C\u0627\u0626\u0639." },
                new[] { "Colonist", "x" },
                new[] { "Hello world", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645" },
                new[] { "Built {0} successfully!", "\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!" },
                new[] { "Hello <b>world</b>!", "\u0645\u0631\u062D\u0628\u0627 <b>\u0639\u0627\u0644\u0645</b>!" }
            };
            foreach (var c in cases)
            {
                var e = Translated(p, "k:" + c[0], c[0], Core, c[1]);
                var report = new CacheReport();
                var kept = ArabicCacheMaintenance.Check(e, p, report);
                T.True(object.ReferenceEquals(kept, e), "a current entry is kept as it is: " + c[0]);
                T.True(report.Valid == 1 && !report.Changed, "and counted as valid: " + c[0]);
            }
        }

        private static void ChangedProcessing()
        {
            var p = MiniPipeline();
            var e = Translated(p, "k", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639.");

            // every revision on its own: the display form is rebuilt from the logical text, nothing else changes
            var mutations = new Dictionary<string, Action<ArabicCacheEntry>>
            {
                { "shaping", x => x.ShapingVersion = "shape-0.old" },
                { "bidi", x => x.BidiVersion = "uax9-0.old" },
                { "pipeline", x => x.PipelineVersion = "pipe-0" },
                { "display", x => x.DisplayVersion = "display-0;S1R1I1B1" }
            };
            foreach (var pair in mutations)
            {
                var old = e.Clone();
                pair.Value(old);
                var report = new CacheReport();
                var kept = ArabicCacheMaintenance.Check(old, p, report);
                if (!T.True(kept != null && !object.ReferenceEquals(kept, old), "an entry with an old " + pair.Key + " revision is rebuilt")) continue;
                T.Equal(1, report.Regenerated, "counted as rebuilt: " + pair.Key);
                T.Equal(0, report.Valid + report.StaleGlossary + report.Rejected, "and nothing else: " + pair.Key);
                T.Equal(e.Display, kept.Display, "the display text is rebuilt: " + pair.Key);
                T.Equal(e.Logical, kept.Logical, "the logical text stays: " + pair.Key);
                T.Equal(e.Source, kept.Source, "the source stays: " + pair.Key);
                T.True(kept.Origin == e.Origin && kept.GlossarySig == e.GlossarySig && kept.Key == e.Key, "origin, signature and key stay: " + pair.Key);
                T.Equal(ArabicVersions.ShapingVersion, kept.ShapingVersion, "shaping stamp renewed: " + pair.Key);
                T.Equal(ArabicVersions.BidiVersion, kept.BidiVersion, "bidi stamp renewed: " + pair.Key);
                T.Equal(ArabicVersions.PipelineVersion, kept.PipelineVersion, "pipeline stamp renewed: " + pair.Key);
                T.Equal(p.DisplayVersion, kept.DisplayVersion, "display stamp renewed: " + pair.Key);
                T.False(kept.DisplayOutdated(p), "no longer outdated: " + pair.Key);
                T.True(old.DisplayOutdated(p), "the checked object itself still carries its old stamp: " + pair.Key);
            }

            // other display options: rebuilt for the new mode, and stable afterwards
            var shapeOnly = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Reorder = false });
            var rep1 = new CacheReport();
            var rebuilt = ArabicCacheMaintenance.Check(e, shapeOnly, rep1);
            T.True(rebuilt != null && rep1.Regenerated == 1, "another display mode rebuilds the entry");
            if (rebuilt != null)
            {
                T.Equal(shapeOnly.RenderDisplay(e.Logical), rebuilt.Display, "for the new mode");
                T.True(rebuilt.Display != e.Display, "which is another text");
                T.Equal(shapeOnly.DisplayVersion, rebuilt.DisplayVersion, "with the new stamp");
                var rep2 = new CacheReport();
                T.True(object.ReferenceEquals(ArabicCacheMaintenance.Check(rebuilt, shapeOnly, rep2), rebuilt) && rep2.Valid == 1, "checking it again changes nothing");
            }
            var logicalMode = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Shape = false, Reorder = false });
            var rep3 = new CacheReport();
            var logicalKept = ArabicCacheMaintenance.Check(e, logicalMode, rep3);
            T.True(logicalKept != null && logicalKept.Display == e.Logical, "in logical mode the display text is the logical text");

            // a final entry whose display text is not display-ready is rebuilt from the logical text
            var tampered = e.Clone();
            tampered.Display = tampered.Logical;
            var rep4 = new CacheReport();
            var fixedEntry = ArabicCacheMaintenance.Check(tampered, p, rep4);
            T.True(fixedEntry != null && rep4.Regenerated == 1, "raw Arabic stored as the display text is rebuilt");
            if (fixedEntry != null) T.Equal(e.Display, fixedEntry.Display, "into the real display text");

            // ... unless the logical text itself is not usable
            var broken = e.Clone();
            broken.Logical = e.Display;
            broken.Display = e.Logical;
            var rep5 = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(broken, p, rep5) == null && rep5.Rejected == 1, "an entry that cannot be rebuilt is dropped");

            var lost = Translated(p, "k2", "Built {0} successfully!", Core, "\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!");
            lost.Logical = "\u062A\u0645 \u0627\u0644\u0628\u0646\u0627\u0621 \u0628\u0646\u062C\u0627\u062D!";
            lost.ShapingVersion = "shape-0.old";
            var rep6 = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(lost, p, rep6) == null && rep6.Rejected == 1, "a placeholder missing in an entry that has to be rebuilt drops it");

            // typed and migrated entries are rebuilt with an approximate source: placeholders may be missing
            var typed = Plain(p, "ku", "Hello {0}", "\u0645\u0631\u062D\u0628\u0627", p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627"));
            typed.Origin = PipelineOrigin.UserEdit;
            typed.ShapingVersion = "shape-0.old";
            var rep7 = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(typed, p, rep7) != null && rep7.Regenerated == 1, "a typed entry may lack a placeholder");
            var migrated = typed.Clone();
            migrated.Key = "km";
            migrated.Origin = PipelineOrigin.Migrated;
            var rep8 = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(migrated, p, rep8) != null && rep8.Regenerated == 1, "so may a migrated entry");
        }

        private static void GlossaryChanges()
        {
            var g1 = MiniPipeline();
            var g2 = Fixtures.PipelineFor(Fixtures.MiniGlossary.Replace("Colonist | \u0645\u0633\u062A\u0639\u0645\u0631", "Colonist | \u0631\u0627\u0626\u062F"), null);
            T.True(g1.GlossaryVersion != g2.GlossaryVersion, "setup: another glossary version");

            var withTerm = Translated(g1, "k1", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639.");
            var unaffected = Translated(g1, "k2", "The turret is old.", Core, "{GL0} \u0642\u062F\u064A\u0645.");
            var noTerms = Translated(g1, "k3", "Hello world", Core, "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645");
            var typedResult = g1.FromUserText("The colonist is hungry.", "\u0627\u0644\u0631\u062C\u0644 \u062C\u0627\u0626\u0639.", Core, false);
            if (!typedResult.Ok) throw new InvalidOperationException("test setup failed: " + typedResult.Problem);
            var typed = ArabicCacheEntry.From("k4", "The colonist is hungry.", Core, typedResult, g1);

            // an edited term: only the entries whose text contains it are dropped
            var report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(withTerm, g2, report) == null && report.StaleGlossary == 1, "the entry with the edited term is dropped");

            report = new CacheReport();
            var kept = ArabicCacheMaintenance.Check(unaffected, g2, report);
            T.True(kept != null && !object.ReferenceEquals(kept, unaffected), "an entry without the edited term is kept as a new object");
            T.True(report.Restamped == 1 && report.Valid == 1 && report.StaleGlossary == 0, "restamped and valid");
            if (kept != null)
            {
                T.Equal(g2.GlossaryVersion, kept.GlossaryVersion, "with the new glossary version");
                T.Equal(unaffected.Display, kept.Display, "the display text is not touched");
                T.Equal(unaffected.GlossarySig, kept.GlossarySig, "nor the signature");
                T.Equal(g1.GlossaryVersion, unaffected.GlossaryVersion, "the old object was not changed");
            }

            report = new CacheReport();
            kept = ArabicCacheMaintenance.Check(noTerms, g2, report);
            T.True(kept != null && report.Restamped == 1 && report.Valid == 1 && kept.Display == noTerms.Display, "a text without terms is only restamped");

            // typed text is never invalidated by the glossary
            report = new CacheReport();
            kept = ArabicCacheMaintenance.Check(typed, g2, report);
            T.True(object.ReferenceEquals(kept, typed) && report.Valid == 1 && report.Restamped == 0 && report.StaleGlossary == 0, "a user's own translation stays");

            // a term that was added
            var g3 = Fixtures.PipelineFor(Fixtures.MiniGlossary.Replace("Slag | \u062E\u0631\u062F\u0629\n", "Slag | \u062E\u0631\u062F\u0629\nHello | \u0645\u0631\u062D\u0628\u0627\n"), null);
            T.True(g3.GlossaryVersion != g1.GlossaryVersion, "setup: a glossary with one more term");
            report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(noTerms, g3, report) == null && report.StaleGlossary == 1, "a text that now contains a glossary term is translated again");
            report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(unaffected, g3, report) != null && report.StaleGlossary == 0, "other texts are not");

            // a term that was removed
            var g4 = Fixtures.PipelineFor(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C\n", ""), null);
            report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(unaffected, g4, report) == null && report.StaleGlossary == 1, "a text whose term was removed is translated again");

            // the context of the entry decides which term applies: only the standardized Raider changes
            var meme = Translated(g1, "kM", "Raider", IdeologyMeme, "x");
            var plain = Translated(g1, "kR", "Raider", Core, "x");
            T.True(meme.Logical == "\u0627\u0644\u063A\u0632\u0648" && plain.Logical == "\u063A\u0627\u0632\u064A", "setup: two meanings of Raider");
            T.True(meme.ModId == "ludeon.rimworld.ideology" && meme.ContextKey == "MemeDef.label", "setup: the meme entry remembers where it came from");
            var g5 = Fixtures.PipelineFor(Fixtures.MiniGlossary.Replace("Raider | \u063A\u0627\u0632\u064A |", "Raider | \u0645\u063A\u064A\u0631 |"), null);
            report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(meme, g5, report) != null && report.StaleGlossary == 0, "the Ideology meme entry is not affected");
            report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(plain, g5, report) == null && report.StaleGlossary == 1, "the standardized entry is");
        }

        private static void InvalidEntries()
        {
            var p = MiniPipeline();
            var e = Translated(p, "k", "Hello world", Core, "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645");

            var cases = new Dictionary<string, Action<ArabicCacheEntry>>
            {
                { "another cache format", x => x.Format = ArabicVersions.CacheFormat + 1 },
                { "format 0", x => x.Format = 0 },
                { "another language", x => x.Language = "en" },
                { "no language", x => x.Language = "" },
                { "raw status", x => x.Status = CacheStatus.Raw },
                { "corrected status", x => x.Status = CacheStatus.Corrected },
                { "no key", x => x.Key = null },
                { "empty key", x => x.Key = "" },
                { "no source", x => x.Source = null },
                { "empty source", x => x.Source = "" },
                { "no logical text", x => x.Logical = null },
                { "empty logical text", x => x.Logical = "" },
                { "no display text", x => x.Display = null },
                { "empty display text", x => x.Display = "" }
            };
            foreach (var pair in cases)
            {
                var bad = e.Clone();
                pair.Value(bad);
                var report = new CacheReport();
                T.True(ArabicCacheMaintenance.Check(bad, p, report) == null && report.Rejected == 1 && report.Valid == 0, "dropped: " + pair.Key);
            }

            var upper = e.Clone();
            upper.Language = "AR";
            var rep = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(upper, p, rep) != null, "the language code is compared without regard to case");
        }

        private static void LoadedFiles()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                var oldGlossary = MiniPipeline();
                var current = Fixtures.PipelineFor(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C", "Turret | \u0645\u0646\u0627\u0631\u0629"), null);
                string path = Path.Combine(dir, "ArabicCache.xml");

                var a = Translated(current, "kA", "The colonist is hungry.", Core, "{GL0} \u062C\u0627\u0626\u0639.");
                var b = Translated(oldGlossary, "kB", "The turret is old.", Core, "{GL0} \u0642\u062F\u064A\u0645.");
                var c = Translated(oldGlossary, "kC", "The colonist is old.", Core, "{GL0} \u0642\u062F\u064A\u0645.");
                var d = a.Clone(); d.Key = "kD"; d.Status = CacheStatus.Raw;
                var e = a.Clone(); e.Key = "kE"; e.Language = "en";
                var f = Translated(current, "kF", "Hello world", Core, "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645");
                f.ShapingVersion = "shape-0.old";

                string xml = ArabicCacheFormat.Serialize(new[] { a, b, c, d, e, f }, HeaderOf(current));
                xml = xml.Replace("</" + ArabicCacheFormat.RootName + ">", "<E k=\"kG\" s=\"x\" l=\"y\" /></" + ArabicCacheFormat.RootName + ">");
                File.WriteAllText(path, xml, new UTF8Encoding(false));

                var store = new ArabicCacheStore(path);
                var report = ArabicCacheMaintenance.LoadAndCheck(store, current);
                T.Equal(6, report.Loaded, "six readable entries");
                T.Equal(2, report.Valid, "valid: the current one and the restamped one");
                T.Equal(1, report.StaleGlossary, "one entry uses a term that changed");
                T.Equal(1, report.Regenerated, "one entry was shaped by an older revision");
                T.Equal(3, report.Rejected, "raw status, other language and the damaged entry are dropped");
                T.Equal(1, report.Restamped, "the entry that does not use the changed term only got a new stamp");
                T.True(report.Changed && store.IsDirty, "the file is marked for rewriting");
                T.Equal(3, store.Count, "three entries remain");

                ArabicCacheEntry got;
                T.True(store.TryGet("kA", out got) && got.Display == a.Display, "kA is as it was");
                T.True(store.TryGet("kC", out got) && got.GlossaryVersion == current.GlossaryVersion && got.Display == c.Display, "kC is restamped, its text untouched");
                T.True(store.TryGet("kF", out got) && got.Display == current.RenderDisplay("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645") && got.ShapingVersion == ArabicVersions.ShapingVersion,
                    "kF is rebuilt");
                T.False(store.ContainsKey("kB") || store.ContainsKey("kD") || store.ContainsKey("kE") || store.ContainsKey("kG"), "the others are gone");

                // saving and loading again: the cleaned cache is stable
                T.True(store.Save(current), "saved: " + store.LastError);
                var again = new ArabicCacheStore(path);
                var report2 = ArabicCacheMaintenance.LoadAndCheck(again, current);
                T.True(report2.Loaded == 3 && report2.Valid == 3 && !report2.Changed, "a second load finds nothing to do: " + report2);
                T.False(again.IsDirty, "and does not make the cache dirty");
                foreach (var entry in store.Snapshot())
                {
                    ArabicCacheEntry back;
                    if (T.True(again.TryGet(entry.Key, out back), "survives: " + entry.Key)) SameEntry(entry, back, "second load " + entry.Key);
                }
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        // ------------------------------------------------------------------------------------------ migration

        private static void LegacyMigration()
        {
            var p = MiniPipeline();
            var store = new ArabicCacheStore(null);
            string shaped = p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627");
            var legacy = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mod.a:Hello world", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"),
                new KeyValuePair<string, string>("unknown.mod:Good bye", "\u0648\u062F\u0627\u0639\u0627"),
                new KeyValuePair<string, string>("No colon here", "\u0644\u0627 \u0646\u0642\u0637\u062A\u064A\u0646 \u0647\u0646\u0627"),
                new KeyValuePair<string, string>("mod.a:Hello {0}extra", "\u0645\u0631\u062D\u0628\u0627"),
                new KeyValuePair<string, string>("ludeon.rimworld:The colonist is hungry.", "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639."),
                new KeyValuePair<string, string>("ludeon.rimworld:Slag chunk", "\u0642\u0637\u0639\u0629 \u062E\u0631\u062F\u0629"),
                new KeyValuePair<string, string>("mod.a:English only", "Hello there"),
                new KeyValuePair<string, string>("mod.a:Already shaped", shaped),
                new KeyValuePair<string, string>("mod.a:Hello", "\u0645\u0631\u062D\u0628\u0627 {1}"),
                new KeyValuePair<string, string>("", "\u0645\u0631\u062D\u0628\u0627"),
                new KeyValuePair<string, string>(null, "\u0645\u0631\u062D\u0628\u0627"),
                new KeyValuePair<string, string>("mod.a:Empty value", "")
            };

            var report = ArabicCacheMaintenance.MigrateLegacy(store, p, legacy);
            T.Equal(4, report.LegacyAdopted, "four old entries have no glossary term and are adopted");
            T.Equal(5, report.LegacyDropped, "five are translated again: glossary terms, not Arabic, already shaped, an invented placeholder");
            T.Equal(4, store.Count, "four entries in the store");

            ArabicCacheEntry e;
            if (T.True(store.TryGet("mod.a:Hello world", out e), "the first entry is adopted"))
            {
                T.True(e.Status == CacheStatus.Final && e.Origin == PipelineOrigin.Migrated, "it is final, from the migration");
                T.Equal("Hello world", e.Source, "the source is the key without the mod id");
                T.Equal("mod.a", e.ModId, "the mod id");
                T.Equal("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", e.Logical, "the old text is the logical text");
                T.Equal(p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"), e.Display, "the display text went through the stage");
                T.True(ArabicShaper.HasPresentationForms(e.Display) && !ArabicPipeline.HasUnshapedLetters(e.Display), "and is display-ready");
                T.Equal("0", e.GlossarySig, "no glossary terms");
                T.False(e.DisplayOutdated(p), "current stamps");
                T.Equal(p.GlossaryVersion, e.GlossaryVersion, "current glossary stamp");
            }
            if (T.True(store.TryGet("unknown.mod:Good bye", out e), "an entry of the old unknown mod is adopted"))
            {
                T.Equal("", e.ModId, "without a mod id");
                T.Equal("Good bye", e.Source, "source");
            }
            if (T.True(store.TryGet("No colon here", out e), "an entry without a mod id is adopted"))
            {
                T.True(e.ModId == "" && e.Source == "No colon here", "the key is the source");
            }
            if (T.True(store.TryGet("mod.a:Hello {0}extra", out e), "the key may carry additional text"))
            {
                T.Equal("Hello {0}extra", e.Source, "the source guess");
                T.Equal("\u0645\u0631\u062D\u0628\u0627", e.Logical, "the old text");
            }
            T.False(store.ContainsKey("ludeon.rimworld:The colonist is hungry."), "an entry with a glossary term is not adopted");
            T.False(store.ContainsKey("ludeon.rimworld:Slag chunk"), "neither is a compound");
            T.False(store.ContainsKey("mod.a:English only"), "text that is not Arabic is not adopted");
            T.False(store.ContainsKey("mod.a:Already shaped"), "text that is already shaped is not adopted");
            T.False(store.ContainsKey("mod.a:Hello"), "an invented placeholder is not adopted");
            foreach (var kept in store.Snapshot()) T.True(kept.Status == CacheStatus.Final, "only final entries are in the store: " + kept.Key);

            // doing it again changes nothing
            var again = ArabicCacheMaintenance.MigrateLegacy(store, p, legacy);
            T.Equal(0, again.LegacyAdopted, "a second run adopts nothing");
            T.Equal(4, store.Count, "and the store is the same");

            // what is processed already wins over the old text
            var other = new ArabicCacheStore(null);
            var existing = Plain(p, "mod.a:Hello world", "Hello world", "\u0646\u0635 \u0622\u062E\u0631", p.RenderDisplay("\u0646\u0635 \u0622\u062E\u0631"));
            other.Put(existing);
            ArabicCacheMaintenance.MigrateLegacy(other, p, legacy);
            T.True(other.TryGet("mod.a:Hello world", out e) && object.ReferenceEquals(e, existing), "an existing entry is never overwritten by an old one");

            // the adopted entries survive the file
            string dir = Fixtures.NewTempDir();
            try
            {
                var filed = new ArabicCacheStore(Path.Combine(dir, "ArabicCache.xml"));
                ArabicCacheMaintenance.MigrateLegacy(filed, p, legacy);
                filed.LegacyHandled = true;
                T.True(filed.Save(p), "saved: " + filed.LastError);
                var reloaded = new ArabicCacheStore(filed.FilePath);
                var rep = ArabicCacheMaintenance.LoadAndCheck(reloaded, p);
                T.True(rep.Loaded == 4 && rep.Valid == 4 && !rep.Changed && reloaded.LegacyHandled, "adopted entries are valid after a restart: " + rep);
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }

            // without a glossary every Arabic entry qualifies
            var bare = new ArabicPipeline(null, null);
            var bareStore = new ArabicCacheStore(null);
            var bareReport = ArabicCacheMaintenance.MigrateLegacy(bareStore, bare, legacy);
            T.Equal(6, bareReport.LegacyAdopted, "without a glossary the two entries with terms are adopted as well");
        }

        private static void ArabicDetection()
        {
            T.False(ArabicCacheMaintenance.ContainsArabicLetter(null), "null");
            T.False(ArabicCacheMaintenance.ContainsArabicLetter(""), "the empty text");
            T.False(ArabicCacheMaintenance.ContainsArabicLetter("Hello 123 {0}"), "Latin text");
            T.True(ArabicCacheMaintenance.ContainsArabicLetter("\u0645\u0631\u062D\u0628\u0627"), "Arabic text");
            T.True(ArabicCacheMaintenance.ContainsArabicLetter("abc " + (char)0x06A4), "a letter of the extended range");
            T.True(ArabicCacheMaintenance.ContainsArabicLetter("abc " + (char)0x0750), "a letter of the supplement");
            T.False(ArabicCacheMaintenance.ContainsArabicLetter(((char)0x0663).ToString()), "an Arabic-Indic digit is not a letter");
            T.False(ArabicCacheMaintenance.ContainsArabicLetter(((char)0xFE91).ToString()), "a presentation form is not counted: shaped text is not old raw text");

            T.True(ArabicCacheMaintenance.MostlyArabic(new[] { "\u0645\u0631\u062D\u0628\u0627", "hello" }), "half Arabic");
            T.False(ArabicCacheMaintenance.MostlyArabic(new[] { "\u0645\u0631\u062D\u0628\u0627", "a", "b" }), "a third Arabic");
            T.False(ArabicCacheMaintenance.MostlyArabic(new string[0]), "no texts");
            T.True(ArabicCacheMaintenance.MostlyArabic(new[] { "", null, "\u0645\u0631\u062D\u0628\u0627" }), "empty texts are not counted");
            T.False(ArabicCacheMaintenance.MostlyArabic(new[] { "", null }), "only empty texts");
            T.Equal("unknown.mod", ArabicCacheMaintenance.LegacyModId, "the mod id the old cache used");
        }

        private static void Reports()
        {
            T.False(new CacheReport().Changed, "an empty report");
            T.False(new CacheReport { Loaded = 5, Valid = 5 }.Changed, "valid entries are not a change");
            T.True(new CacheReport { Regenerated = 1 }.Changed, "regenerated");
            T.True(new CacheReport { StaleGlossary = 1 }.Changed, "stale");
            T.True(new CacheReport { Rejected = 1 }.Changed, "rejected");
            T.True(new CacheReport { LegacyAdopted = 1 }.Changed, "adopted");
            T.True(new CacheReport { LegacyDropped = 1 }.Changed, "dropped");
            T.True(new CacheReport { Restamped = 1 }.Changed, "restamped");
            T.True(new CacheReport { StaleMultiplier = 1 }.Changed, "multiplier texts to translate again");
            var sum = new CacheReport { Loaded = 2, Valid = 1, Rejected = 1 };
            sum.Add(new CacheReport { Loaded = 3, Valid = 2, StaleGlossary = 1, StaleMultiplier = 2 });
            sum.Add(null);
            T.True(sum.Loaded == 5 && sum.Valid == 3 && sum.Rejected == 1 && sum.StaleGlossary == 1 && sum.StaleMultiplier == 2, "reports add up");
            T.True(new CacheReport { StaleMultiplier = 4 }.ToString().Contains("multiplier texts to translate again 4"), "the count is in the text");
            T.False(new CacheReport { Loaded = 1 }.ToString().Contains("multiplier"), "and only when there is one");
            string text = new CacheReport { Loaded = 9, Valid = 8, Regenerated = 1 }.ToString();
            T.True(text.Contains("loaded 9") && text.Contains("valid 8") && text.Contains("re-rendered 1"), "the report as text: " + text);
        }

        // ------------------------------------------------------------------------------------------ one file per mod

        /// <summary>An entry of a mod with the current stamps and plain text (nothing to shape, so it stays valid when loaded).</summary>
        private static ArabicCacheEntry OfMod(ArabicPipeline p, string modId, string source)
        {
            var e = Plain(p, modId + ":" + source, source, "x " + source, "x " + source);
            e.ModId = modId;
            e.Origin = PipelineOrigin.Translator;
            return e;
        }

        private static string NameOfMod(string id)
        {
            if (id == "ray1203.simplecamerasetting") return "Simple Camera Setting";
            if (id == "ludeon.rimworld") return "Core";
            return null;
        }

        private static string FileNamesIn(string folder)
        {
            var names = new List<string>();
            if (Directory.Exists(folder))
            {
                foreach (var f in Directory.GetFiles(folder, "*.xml")) names.Add(Path.GetFileName(f));
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join("|", names);
        }

        private static void FileNames()
        {
            T.Equal("SimpleCameraSetting", ArabicModCache.FileStem("Simple Camera Setting"), "spaces are left out");
            T.Equal("ConvergenceCore", ArabicModCache.FileStem("Convergence: Core"), "a colon and a space are left out");
            T.Equal("abcdefghij", ArabicModCache.FileStem("a/b\\c:d*e?f\"g<h>i|j"), "the characters a file name cannot hold are left out");
            T.Equal("hidden", ArabicModCache.FileStem("..hidden."), "no dots at the ends");
            T.Equal("Cave-Born-Mod", ArabicModCache.FileStem("Cave-Born-Mod"), "a hyphen stays");
            T.Equal("some.mod", ArabicModCache.FileStem("some.mod"), "so does a dot inside");
            T.Equal("", ArabicModCache.FileStem(""), "nothing gives nothing");
            T.Equal("", ArabicModCache.FileStem(null), "null gives nothing");
            T.Equal("", ArabicModCache.FileStem(" ?: "), "only forbidden characters give nothing");
            T.Equal(60, ArabicModCache.FileStem(new string('a', 100)).Length, "at most 60 characters");

            var p = MiniPipeline();
            var withId = Plain(p, "ray1203.x:Hello", "Hello", "x", "x");
            withId.ModId = "own.id";
            T.Equal("own.id", ArabicModCache.ModOf(withId), "the mod id of the entry");
            T.Equal("ray1203.x", ArabicModCache.ModOf(Plain(p, "ray1203.x:Hello", "Hello", "x", "x")), "else the part of the key before the colon");
            T.Equal(ArabicCacheMaintenance.LegacyModId, ArabicModCache.ModOf(Plain(p, "no colon", "no colon", "x", "x")), "else the mod of the old cache");
            T.Equal(ArabicCacheMaintenance.LegacyModId, ArabicModCache.ModOf(Plain(p, ":Hello", "Hello", "x", "x")), "a colon at the start is no mod");
        }

        private static void ModFiles()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                var p = MiniPipeline();
                string folder = Path.Combine(dir, "ArabicCache");
                string oldFile = Path.Combine(dir, "ArabicCache.xml");

                var cache = new ArabicModCache(folder, oldFile, NameOfMod);
                T.True(cache.Count == 0 && !cache.IsDirty && cache.FolderPath == folder && cache.Note == null, "a new cache is empty and clean");
                var first = cache.Load(p);
                T.True(first.Loaded == 0 && !first.Changed && cache.Count == 0 && !cache.IsDirty, "no folder and no old file: nothing to load");
                T.False(Directory.Exists(folder), "loading makes no folder");

                T.True(cache.Put(OfMod(p, "ray1203.simplecamerasetting", "Zoom in")), "an entry of the camera mod");
                cache.Put(OfMod(p, "ray1203.simplecamerasetting", "Zoom out"));
                cache.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                cache.Put(OfMod(p, "some.unknown.mod", "Hello"));
                var raw = OfMod(p, "ludeon.rimworld", "Raw one");
                raw.Status = CacheStatus.Raw;
                T.False(cache.Put(raw) || cache.Put(null), "an entry that is not final, and null, are not accepted");
                T.Equal(4, cache.Count, "four entries");
                T.True(cache.IsDirty, "new entries make it dirty");
                T.True(cache.Save(p), "saved: " + cache.LastError);
                T.False(cache.IsDirty, "saved means clean");
                T.Equal("Core-ar.xml|SimpleCameraSetting-ar.xml|some.unknown.mod-ar.xml", FileNamesIn(folder),
                    "one file per mod, named after the mod (after its package id when the mod is not known)");

                // every file holds the entries of its mod and nothing else
                var camera = new ArabicCacheStore(Path.Combine(folder, "SimpleCameraSetting-ar.xml"));
                var cameraReport = ArabicCacheMaintenance.LoadAndCheck(camera, p);
                T.True(cameraReport.Loaded == 2 && camera.ContainsKey("ray1203.simplecamerasetting:Zoom in") &&
                       camera.ContainsKey("ray1203.simplecamerasetting:Zoom out"), "the file of the camera mod holds its two texts");

                // after a restart the entries are all there, found whatever file they are in
                var again = new ArabicModCache(folder, oldFile, NameOfMod);
                var report = again.Load(p);
                T.True(report.Loaded == 4 && report.Valid == 4 && !report.Changed, "four valid entries read: " + report);
                T.True(again.Count == 4 && !again.IsDirty && again.Note == null, "and nothing to write");
                foreach (var e in cache.Snapshot())
                {
                    ArabicCacheEntry back;
                    if (T.True(again.TryGet(e.Key, out back), "entry " + e.Key + " survives the restart")) SameEntry(e, back, "restart " + e.Key);
                }
                T.True(again.ContainsKey("ludeon.rimworld:Hello world") && !again.ContainsKey("ludeon.rimworld:Zoom in"), "ContainsKey");
                T.Equal(4, again.Snapshot().Count, "the snapshot has every entry");

                // a new text of a known mod goes to its file; a text that is cached already is replaced where it is
                again.Put(OfMod(p, "ray1203.simplecamerasetting", "Zoom reset"));
                var replaced = OfMod(p, "ray1203.simplecamerasetting", "Zoom in");
                replaced.Logical = "x replaced";
                replaced.Display = "x replaced";
                again.Put(replaced);
                T.Equal(5, again.Count, "one more entry, one replaced");
                T.True(again.IsDirty && again.Save(p), "saved: " + again.LastError);
                T.Equal("Core-ar.xml|SimpleCameraSetting-ar.xml|some.unknown.mod-ar.xml", FileNamesIn(folder), "still one file per mod");
                var camera2 = new ArabicCacheStore(Path.Combine(folder, "SimpleCameraSetting-ar.xml"));
                ArabicCacheMaintenance.LoadAndCheck(camera2, p);
                ArabicCacheEntry got;
                T.True(camera2.Count == 3 && camera2.TryGet("ray1203.simplecamerasetting:Zoom in", out got) && got.Logical == "x replaced", "the camera file has three entries, one replaced");

                // the last entry of a mod takes its file away; a text of another mod is not removed by mistake
                T.False(again.Remove("ludeon.rimworld:Zoom in"), "removing what is not there");
                T.True(again.Remove("some.unknown.mod:Hello"), "removing the only entry of a mod");
                T.True(again.IsDirty && again.Save(p), "saved: " + again.LastError);
                T.Equal("Core-ar.xml|SimpleCameraSetting-ar.xml", FileNamesIn(folder), "an emptied mod leaves no file");
                T.False(File.Exists(Path.Combine(folder, "some.unknown.mod-ar.xml.bak")), "and no backup");

                // deleting the file of a mod (with the game closed) forgets that mod only
                File.Delete(Path.Combine(folder, "SimpleCameraSetting-ar.xml"));
                File.Delete(Path.Combine(folder, "SimpleCameraSetting-ar.xml.bak"));
                var third = new ArabicModCache(folder, oldFile, NameOfMod);
                third.Load(p);
                T.True(third.Count == 1 && third.ContainsKey("ludeon.rimworld:Hello world"), "only the other mod is left");
                third.Put(OfMod(p, "ray1203.simplecamerasetting", "Zoom in"));
                T.True(third.Save(p), "saved: " + third.LastError);
                T.Equal("Core-ar.xml|SimpleCameraSetting-ar.xml", FileNamesIn(folder), "the mod gets its file again, with the same name");

                // clearing removes every file
                third.Clear();
                T.True(third.IsDirty && third.Save(p), "cleared and saved: " + third.LastError);
                T.Equal("", FileNamesIn(folder), "no file is left");
                T.True(third.Count == 0 && !third.IsDirty, "empty and clean");

                // a file that is not a cache is left alone and reported; the others load
                File.WriteAllText(Path.Combine(folder, "Broken-ar.xml"), "not xml at all", new UTF8Encoding(false));
                var fourth = new ArabicModCache(folder, oldFile, NameOfMod);
                fourth.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                fourth.Save(p);
                var fifth = new ArabicModCache(folder, oldFile, NameOfMod);
                fifth.Load(p);
                T.True(fifth.Count == 1 && fifth.LastError != null, "the damaged file is reported, the other one loads");
                T.True(File.Exists(Path.Combine(folder, "Broken-ar.xml.corrupt")), "the damaged file is kept for inspection");

                // memory only: everything but the files works
                var memory = new ArabicModCache(null, null, NameOfMod);
                memory.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                T.True(memory.Count == 1 && memory.IsDirty && !memory.Save(p) && memory.FolderPath == null, "a cache without a folder cannot be saved");
                T.True(memory.Load(p).Loaded == 0, "or loaded");

                // the migration flag reaches every file
                var flags = new ArabicModCache(Path.Combine(dir, "flags"), null, NameOfMod);
                flags.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                flags.Put(OfMod(p, "some.unknown.mod", "Hello"));
                flags.Save(p);
                T.False(flags.LegacyHandled, "not handled yet");
                flags.LegacyHandled = true;
                T.True(flags.IsDirty && flags.Save(p), "the flag makes every file dirty: " + flags.LastError);
                var flagged = new ArabicModCache(Path.Combine(dir, "flags"), null, NameOfMod);
                flagged.Load(p);
                T.True(flagged.LegacyHandled && flagged.Count == 2 && !flagged.IsDirty, "the flag survives");
                flagged.Put(OfMod(p, "new.mod", "Fresh"));
                flagged.Save(p);
                var after = new ArabicCacheStore(Path.Combine(Path.Combine(dir, "flags"), "new.mod-ar.xml"));
                ArabicCacheMaintenance.LoadAndCheck(after, p);
                T.True(after.LegacyHandled, "a file made later carries the flag as well");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        private static void SameNameMods()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                var p = MiniPipeline();
                string folder = Path.Combine(dir, "ArabicCache");
                Func<string, string> same = id => "Same Name";

                var cache = new ArabicModCache(folder, null, same);
                cache.Put(OfMod(p, "a.mod", "One"));
                cache.Put(OfMod(p, "b.mod", "Two"));
                cache.Put(OfMod(p, "c.mod", "Three"));
                cache.Put(OfMod(p, "A.MOD", "Four"));
                T.True(cache.Save(p), "saved: " + cache.LastError);
                T.Equal("SameName-ar.xml|SameName.b.mod-ar.xml|SameName.c.mod-ar.xml", FileNamesIn(folder),
                    "mods with one name are told apart by their package id (the package id is not case sensitive)");

                var again = new ArabicModCache(folder, null, same);
                var report = again.Load(p);
                T.True(report.Loaded == 4 && again.Count == 4 && !report.Changed, "all four texts come back: " + report);

                // a name function that fails or answers nothing names the file after the package id
                var broken = new ArabicModCache(Path.Combine(dir, "other"), null, delegate (string id) { throw new InvalidOperationException("no mods yet"); });
                broken.Put(OfMod(p, "x.y", "One"));
                broken.Save(p);
                T.Equal("x.y-ar.xml", FileNamesIn(Path.Combine(dir, "other")), "a failing name lookup gives the package id");

                // a file with the name is taken even when it is not a cache of this mod
                string taken = Path.Combine(dir, "taken");
                Directory.CreateDirectory(taken);
                File.WriteAllText(Path.Combine(taken, "Core-ar.xml"), "foreign", new UTF8Encoding(false));
                var blocked = new ArabicModCache(taken, null, NameOfMod);
                blocked.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                blocked.Save(p);
                T.Equal("Core-ar.xml|Core.ludeon.rimworld-ar.xml", FileNamesIn(taken), "an existing file is not overwritten");
                T.Equal("foreign", File.ReadAllText(Path.Combine(taken, "Core-ar.xml")), "its content is untouched");

                // the same key in two files is kept once
                string twice = Path.Combine(dir, "twice");
                var one = new ArabicCacheStore(Path.Combine(twice, "A-ar.xml"));
                var two = new ArabicCacheStore(Path.Combine(twice, "B-ar.xml"));
                one.Put(OfMod(p, "m.one", "Same text"));
                two.Put(OfMod(p, "m.one", "Same text"));
                two.Put(OfMod(p, "m.one", "Other text"));
                one.Save(p);
                two.Save(p);
                var merged = new ArabicModCache(twice, null, null);
                var mergedReport = merged.Load(p);
                T.True(merged.Count == 2 && mergedReport.Rejected == 1 && merged.IsDirty, "the second copy is dropped and its file rewritten: " + mergedReport);
                T.True(merged.Save(p), "saved: " + merged.LastError);
                T.Equal("A-ar.xml|B-ar.xml", FileNamesIn(twice), "both files stay");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        private static void OldFileSplit()
        {
            string dir = Fixtures.NewTempDir();
            try
            {
                var p = MiniPipeline();
                string folder = Path.Combine(dir, "ArabicCache");
                string oldFile = Path.Combine(dir, "ArabicCache.xml");

                // the single file of an earlier version: a text of the camera mod and one of the game
                var single = new ArabicCacheStore(oldFile);
                single.Put(OfMod(p, "ray1203.simplecamerasetting", "Zoom in"));
                single.Put(OfMod(p, "ludeon.rimworld", "Hello world"));
                single.LegacyHandled = true;
                T.True(single.Save(p), "the old file is written: " + single.LastError);

                // and a newer file of the camera mod that already has one of the same texts, translated differently
                var newer = new ArabicCacheStore(Path.Combine(folder, "SimpleCameraSetting-ar.xml"));
                var newerEntry = OfMod(p, "ray1203.simplecamerasetting", "Zoom in");
                newerEntry.Logical = "x newer";
                newerEntry.Display = "x newer";
                newer.Put(newerEntry);
                T.True(newer.Save(p), "the newer file is written: " + newer.LastError);

                var cache = new ArabicModCache(folder, oldFile, NameOfMod);
                var report = cache.Load(p);
                T.True(report.Loaded == 3 && cache.Count == 2, "three entries read, two kept: " + report);
                ArabicCacheEntry got;
                T.True(cache.TryGet("ray1203.simplecamerasetting:Zoom in", out got) && got.Logical == "x newer", "the file of the mod wins over the old file");
                T.True(cache.TryGet("ludeon.rimworld:Hello world", out got), "the text only the old file had is there");
                T.True(cache.LegacyHandled, "the migration flag of the old file is kept");
                T.True(cache.Note != null && cache.Note.Contains("ArabicCache.xml") && cache.Note.Contains("2 entries"), "the log is told about it");
                T.True(cache.IsDirty && File.Exists(oldFile), "the new files are still to be written, the old file is still there");

                T.True(cache.Save(p), "saved: " + cache.LastError);
                T.False(File.Exists(oldFile), "the old file is gone after the save");
                T.True(File.Exists(oldFile + ".migrated"), "kept as ArabicCache.xml.migrated");
                T.Equal("Core-ar.xml|SimpleCameraSetting-ar.xml", FileNamesIn(folder), "the text of the game went to its own file");
                T.False(cache.IsDirty, "clean");

                var again = new ArabicModCache(folder, oldFile, NameOfMod);
                var report2 = again.Load(p);
                T.True(report2.Loaded == 2 && again.Count == 2 && !again.IsDirty && again.Note == null && again.LegacyHandled, "nothing is left to take over: " + report2);
                T.True(again.TryGet("ray1203.simplecamerasetting:Zoom in", out got) && got.Logical == "x newer", "and the newer text stayed");

                // an old file that cannot be read stays where it is
                string dir2 = Path.Combine(dir, "damaged");
                Directory.CreateDirectory(dir2);
                string oldFile2 = Path.Combine(dir2, "ArabicCache.xml");
                File.WriteAllText(oldFile2, "this is not xml", new UTF8Encoding(false));
                var damaged = new ArabicModCache(Path.Combine(dir2, "ArabicCache"), oldFile2, NameOfMod);
                var damagedReport = damaged.Load(p);
                T.True(damagedReport.Loaded == 0 && damaged.Count == 0 && damaged.LastError != null, "nothing is taken over from a damaged file");
                T.True(!damaged.IsDirty && File.Exists(oldFile2) && damaged.Note == null, "and it is not touched");

                // an old file with nothing in it is retired as well, so that it is not read at every start
                string dir3 = Path.Combine(dir, "empty");
                Directory.CreateDirectory(dir3);
                string oldFile3 = Path.Combine(dir3, "ArabicCache.xml");
                var empty = new ArabicCacheStore(oldFile3);
                empty.LegacyHandled = true;
                empty.MarkDirty();
                T.True(empty.Save(p), "an empty cache file is written: " + empty.LastError);
                var retire = new ArabicModCache(Path.Combine(dir3, "ArabicCache"), oldFile3, NameOfMod);
                retire.Load(p);
                T.True(retire.Count == 0 && retire.IsDirty && retire.LegacyHandled, "an empty old file still has to be retired");
                T.True(retire.Save(p) && !retire.IsDirty, "saved: " + retire.LastError);
                T.True(!File.Exists(oldFile3) && File.Exists(oldFile3 + ".migrated"), "renamed");
                T.False(Directory.Exists(Path.Combine(dir3, "ArabicCache")), "and no folder is made for nothing");
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }

        // ------------------------------------------------------------------------------------------ texts translated before a fix

        private static void StaleMultiplierEntries()
        {
            T.Equal(3, ArabicVersions.PipelineRevision("pipe-3"), "the revision of a pipeline version");
            T.Equal(12, ArabicVersions.PipelineRevision("pipe-12"), "with two digits");
            T.True(ArabicVersions.PipelineRevision(null) == 0 && ArabicVersions.PipelineRevision("") == 0 && ArabicVersions.PipelineRevision("p") == 0 &&
                   ArabicVersions.PipelineRevision("pipe-") == 0 && ArabicVersions.PipelineRevision("pipe-x") == 0 && ArabicVersions.PipelineRevision("pipe--1") == 0 &&
                   ArabicVersions.PipelineRevision("other-5") == 0, "anything else is revision 0");
            T.True(ArabicVersions.PipelineRevision(ArabicVersions.PipelineVersion) >= ArabicVersions.MultiplierFixRevision, "the current version has the fix");

            var p = MiniPipeline();
            Func<string, string, ArabicCacheEntry> old = delegate (string source, string version)
            {
                var e = OfMod(p, "m.one", source);
                e.PipelineVersion = version;
                return e;
            };

            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Spirit root: x{0}", "pipe-2")), "x{0} translated by an old version");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Damage 8x", "pipe-2")), "a number and its sign");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x2.5", "pipe-0")), "an even older version");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x2.5", "unknown")), "a version that is not known");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Hello world", "pipe-2")), "no multiplier in the text");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("A 3x3 box2 max{0}", "pipe-2")), "a dimension, a word and a name are none");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x3", ArabicVersions.PipelineVersion)), "made with the current version");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x3", "pipe-3")), "made with the version that has the fix");

            // a tier numeral at the end ("Dragon scales II") needs the later fix
            T.True(ArabicVersions.PipelineRevision(ArabicVersions.PipelineVersion) >= ArabicVersions.TierFixRevision, "the current version has the tier fix");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Dragon scales II", "pipe-3")), "a tier numeral translated before its fix");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Keen senses I", "pipe-2")), "a single letter, translated even earlier");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Nirvana IV.", "unknown")), "a version that is not known");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Dragon scales II", "pipe-4")), "made with the version that has the tier fix");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Dragon scales II", ArabicVersions.PipelineVersion)), "made with the current version");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Level III boss", "pipe-3")), "a numeral in the middle is none");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("so do I", "pipe-3")), "nor is the pronoun");
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x3", "pipe-3")), "a multiplier is not stale with the multiplier fix");
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(old("Chance x3 II", "pipe-3")), "a multiplier and a numeral: the numeral is what is stale");
            var typedTier = old("Dragon scales II", "pipe-3");
            typedTier.Origin = PipelineOrigin.UserEdit;
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(typedTier), "a numeral text the user typed is kept");
            var offlineTier = old("Dragon scales II", "pipe-3");
            offlineTier.Origin = PipelineOrigin.Offline;
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(offlineTier), "so is one the glossary made alone");

            var typed = old("Chance x2", "pipe-2");
            typed.Origin = PipelineOrigin.UserEdit;
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(typed), "a text the user typed is kept");
            var offline = old("Chance x2", "pipe-2");
            offline.Origin = PipelineOrigin.Offline;
            T.False(ArabicCacheMaintenance.IsStaleMultiplier(offline), "so is one the glossary made alone");
            var migrated = old("Chance x2", "pipe-2");
            migrated.Origin = PipelineOrigin.Migrated;
            T.True(ArabicCacheMaintenance.IsStaleMultiplier(migrated), "an entry of the old cache is translated again");

            // checking one entry
            var report = new CacheReport();
            T.True(ArabicCacheMaintenance.Check(old("Spirit root: x{0}", "pipe-2"), p, report) == null, "a stale entry is dropped");
            T.True(report.StaleMultiplier == 1 && report.Rejected == 0 && report.StaleGlossary == 0 && report.Regenerated == 0 && report.Changed, "and counted as such: " + report);
            var current = OfMod(p, "m.one", "Spirit root: x{0}");
            T.True(ReferenceEquals(ArabicCacheMaintenance.Check(current, p, report), current), "an entry of the current version is kept as it is");
            T.True(report.StaleMultiplier == 1 && report.Valid == 1, "and counted as valid: " + report);

            // loading a file that holds both
            string dir = Fixtures.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "Mod-ar.xml");
                var a = old("Spirit root: x{0}", "pipe-2");
                var b = OfMod(p, "m.one", "Chance x3");
                var c = old("Damage 8x", "pipe-2");
                File.WriteAllText(path, ArabicCacheFormat.Serialize(new[] { a, b, c }, HeaderOf(p)), new UTF8Encoding(false));

                var store = new ArabicCacheStore(path);
                var loaded = ArabicCacheMaintenance.LoadAndCheck(store, p);
                T.True(loaded.Loaded == 3 && loaded.StaleMultiplier == 2 && loaded.Valid == 1, "two stale entries, one valid: " + loaded);
                T.True(store.Count == 1 && store.ContainsKey(b.Key) && !store.ContainsKey(a.Key) && !store.ContainsKey(c.Key), "only the current one is kept");
                T.True(store.IsDirty, "the file is marked for rewriting");
                T.True(store.Save(p), "saved: " + store.LastError);

                var again = new ArabicCacheStore(path);
                var loaded2 = ArabicCacheMaintenance.LoadAndCheck(again, p);
                T.True(loaded2.Loaded == 1 && !loaded2.Changed && !again.IsDirty, "a second load finds nothing to do: " + loaded2);

                // the same through the cache of all mods
                var cache = new ArabicModCache(dir, null, null);
                var cacheReport = cache.Load(p);
                T.True(cacheReport.Loaded == 1 && cache.Count == 1 && !cacheReport.Changed, "through the cache of all mods: " + cacheReport);
            }
            finally
            {
                Fixtures.DeleteTempDir(dir);
            }
        }
    }
}
