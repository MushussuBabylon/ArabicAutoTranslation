using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// The mandatory glossary: the shipped data file against the terminology glossary it was made from, the parser, the
    /// versioning, the matching rules (word boundaries, longest first, plurals, articles, context, DLC scope, guards) and the
    /// marker round trip that puts approved terms into a translation.
    /// </summary>
    internal static class GlossaryTests
    {
        private static readonly GlossaryContext Core = new GlossaryContext("ludeon.rimworld", "Keyed.Test", "");
        private static GlossaryIndex _mini;

        private static GlossaryIndex Mini
        {
            get
            {
                if (_mini == null) _mini = new GlossaryIndex(GlossaryParser.Parse(Fixtures.MiniGlossary));
                return _mini;
            }
        }

        internal static void Run()
        {
            T.Group("glossary: shipped data file");
            ShippedFile();

            T.Group("glossary: every reference term is present");
            CompletenessAgainstReference();

            T.Group("glossary: parser and versions");
            ParserIssues();
            ParserFeatures();
            Versioning();

            T.Group("glossary: matching rules");
            WholeTexts();
            MarkedTexts();
            CompoundTerms();
            Context();
            Guards();
            Plurals();
            Possessives();
            Signatures();

            T.Group("glossary: markers and restoring");
            Restoring();

            T.Group("glossary: shipped terms in context");
            ShippedTerms();
        }

        private static string Offline(GlossaryIndex index, string source, GlossaryContext ctx)
        {
            var plan = index.Plan(source, ctx, true);
            return plan.Offline ? plan.OfflineArabic : null;
        }

        private static string Marked(GlossaryIndex index, string source, GlossaryContext ctx)
        {
            return index.Plan(source, ctx, true).Marked;
        }

        private static string Units(GlossaryIndex index, string source, GlossaryContext ctx)
        {
            var plan = index.Plan(source, ctx, true);
            var parts = new List<string>();
            foreach (var u in plan.Units) parts.Add(u.Arabic);
            return string.Join(" # ", parts.ToArray());
        }

        // ------------------------------------------------------------------------------------------ shipped file

        private static readonly string[] Sections = { "Core", "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey", "Standardized" };

        private static void ShippedFile()
        {
            var data = Fixtures.ShippedData;
            foreach (var issue in data.Issues) T.Fail("glossary file problem: " + issue);
            T.Equal(0, data.Issues.Count, "the shipped glossary parses without any problem");
            T.True(data.Entries.Count >= 336, "the shipped glossary has all its entries (" + data.Entries.Count + ")");
            T.True(data.DeclaredVersion.Length > 0, "the glossary declares a version");
            T.True(data.Version.StartsWith("gl-" + data.DeclaredVersion + ".", StringComparison.Ordinal), "the version is the declared one plus a content hash");
            T.Equal(16, data.ContentHash.Length, "the content hash has 16 digits");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in data.Entries)
            {
                string where = "[" + e.Section + "] " + e.EnglishAsWritten[0] + " (line " + e.Line + ")";
                T.True(!string.IsNullOrEmpty(e.Id) && ids.Add(e.Id), "unique id: " + where);
                T.True(Array.IndexOf(Sections, e.Section) >= 0, "known section: " + where);
                T.True(ArabicText.ContainsArabic(e.Arabic), "the approved text is Arabic: " + where);
                T.False(ArabicShaper.HasPresentationForms(e.Arabic + e.ArabicPlural + e.ArabicFeminine + e.ArabicFemininePlural + e.ArabicDefinite + e.ArabicIndefinite),
                    "terms are stored as plain letters, not presentation forms: " + where);
                T.Equal(e.Arabic.Trim(), e.Arabic, "no stray spaces around the approved text: " + where);
                T.Equal(e.Arabic, ArabicText.NormalizeOrthography(e.Arabic), "the approved text follows the orthography rules: " + where);
                string expectedPackage = e.Section == "Core" ? "ludeon.rimworld" : "ludeon.rimworld." + e.Section.ToLowerInvariant();
                if (e.Section == "Standardized") T.Equal(0, e.SectionPackages.Length, "Standardized applies to every DLC: " + where);
                else T.True(e.SectionPackages.Length == 1 && e.SectionPackages[0] == expectedPackage, "DLC scope of the section: " + where);
            }

            foreach (var section in Sections)
            {
                int n = 0;
                foreach (var e in data.Entries) if (e.Section == section) n++;
                T.True(n > 0, "the section " + section + " has entries");
            }
        }

        // ------------------------------------------------------------------------------------------ completeness

        private sealed class RefEntry
        {
            internal string Section;
            internal List<string> Aliases = new List<string>();
            internal string Arabic;
            internal List<string> Remarks = new List<string>();
        }

        private static List<RefEntry> ParseReference(string text)
        {
            var headings = new Dictionary<string, string>
            {
                { "CORE / BASE GAME", "Core" },
                { "ROYALTY", "Royalty" },
                { "IDEOLOGY", "Ideology" },
                { "BIOTECH", "Biotech" },
                { "ANOMALY", "Anomaly" },
                { "ODYSSEY", "Odyssey" },
                { "STANDARDIZED TERMS (ADDED IN THE TERMINOLOGY REVIEW)", "Standardized" }
            };
            var remark = new Regex(@"\(([^)]*)\)");
            var result = new List<RefEntry>();
            string section = null;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string heading;
                if (headings.TryGetValue(line, out heading))
                {
                    section = heading;
                    continue;
                }
                int arrow = line.IndexOf('\u2192');
                if (section == null || arrow < 0) continue;

                string left = line.Substring(0, arrow).Trim();
                string right = line.Substring(arrow + 1).Trim();
                string key = left.ToLowerInvariant();
                if (key == "left / right sight sensor")
                {
                    result.Add(Single(section, "Left sight sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0628\u0635\u0631\u064A \u0627\u0644\u0623\u064A\u0633\u0631"));
                    result.Add(Single(section, "Right sight sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0628\u0635\u0631\u064A \u0627\u0644\u0623\u064A\u0645\u0646"));
                    continue;
                }
                if (key == "left / right hearing sensor")
                {
                    result.Add(Single(section, "Left hearing sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0633\u0645\u0639\u064A \u0627\u0644\u0623\u064A\u0633\u0631"));
                    result.Add(Single(section, "Right hearing sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0633\u0645\u0639\u064A \u0627\u0644\u0623\u064A\u0645\u0646"));
                    continue;
                }
                if (key == "arm")
                {
                    result.Add(Single(section, "Arm", "\u0627\u0644\u0630\u0631\u0627\u0639"));
                    result.Add(Single(section, "Left arm", "\u0627\u0644\u0630\u0631\u0627\u0639 \u0627\u0644\u064A\u0633\u0631\u0649"));
                    result.Add(Single(section, "Right arm", "\u0627\u0644\u0630\u0631\u0627\u0639 \u0627\u0644\u064A\u0645\u0646\u0649"));
                    continue;
                }

                var r = new RefEntry { Section = section };
                foreach (Match m in remark.Matches(right)) r.Remarks.Add(m.Groups[1].Value.Trim());
                r.Arabic = remark.Replace(right, "").Trim();
                foreach (var alias in left.Split(new[] { " / " }, StringSplitOptions.None)) r.Aliases.Add(alias.Trim());
                result.Add(r);
            }
            return result;
        }

        private static RefEntry Single(string section, string english, string arabic)
        {
            var r = new RefEntry { Section = section, Arabic = arabic };
            r.Aliases.Add(english);
            return r;
        }

        private static List<GlossaryEntry> Find(GlossaryData data, string section, string english, string arabic)
        {
            var hits = new List<GlossaryEntry>();
            foreach (var e in data.Entries)
            {
                if (e.Section != section) continue;
                bool named = false;
                foreach (var a in e.EnglishAsWritten)
                {
                    if (string.Equals(a, english, StringComparison.OrdinalIgnoreCase)) { named = true; break; }
                }
                if (named && (arabic == null || e.Arabic == arabic)) hits.Add(e);
            }
            return hits;
        }

        private static bool Any(List<GlossaryEntry> hits, Func<GlossaryEntry, bool> test)
        {
            foreach (var h in hits) if (test(h)) return true;
            return false;
        }

        private static void CompletenessAgainstReference()
        {
            var data = Fixtures.ShippedData;
            var reference = ParseReference(Fixtures.ReferenceGlossaryText);
            int pairs = 0;
            var plural = new Regex(@"^plural:\s*(.+)$");
            var feminine = new Regex(@"^feminine:\s*([^,]+?)\s*(?:,\s*plural:\s*(.+))?$");

            foreach (var r in reference)
            {
                foreach (var english in r.Aliases)
                {
                    pairs++;
                    var hits = Find(data, r.Section, english, r.Arabic);
                    if (!T.True(hits.Count > 0, "reference term present: [" + r.Section + "] " + english + " -> " + T.Show(r.Arabic))) continue;

                    foreach (var remarkText in r.Remarks)
                    {
                        var mf = feminine.Match(remarkText);
                        var mp = plural.Match(remarkText);
                        if (mf.Success)
                        {
                            string fem = mf.Groups[1].Value.Trim();
                            T.True(Any(hits, h => h.ArabicFeminine == fem), "feminine form of [" + r.Section + "] " + english + ": " + T.Show(fem));
                            if (mf.Groups[2].Success)
                            {
                                string pl = mf.Groups[2].Value.Trim();
                                T.True(Any(hits, h => h.ArabicPlural == pl), "plural form of [" + r.Section + "] " + english + ": " + T.Show(pl));
                            }
                        }
                        else if (mp.Success)
                        {
                            string pl = mp.Groups[1].Value.Trim();
                            T.True(Any(hits, h => h.ArabicPlural == pl), "plural form of [" + r.Section + "] " + english + ": " + T.Show(pl));
                        }
                    }
                }
            }
            T.True(pairs >= 330, "the reference glossary has its terms (" + pairs + " pairs read)");

            // the terms the project instructions name on top of the reference
            string[][] extras =
            {
                new[] { "Standardized", "Medicine", "\u062F\u0648\u0627\u0621" },
                new[] { "Standardized", "Ancient Stockpile", "\u0645\u062E\u0632\u0648\u0646" },
                new[] { "Standardized", "Cook", "\u0637\u0628\u0627\u062E" },
                new[] { "Standardized", "Left arm", "\u0627\u0644\u0630\u0631\u0627\u0639 \u0627\u0644\u064A\u0633\u0631\u0649" },
                new[] { "Standardized", "Right arm", "\u0627\u0644\u0630\u0631\u0627\u0639 \u0627\u0644\u064A\u0645\u0646\u0649" },
                new[] { "Standardized", "Left sight sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0628\u0635\u0631\u064A \u0627\u0644\u0623\u064A\u0633\u0631" },
                new[] { "Standardized", "Right sight sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0628\u0635\u0631\u064A \u0627\u0644\u0623\u064A\u0645\u0646" },
                new[] { "Standardized", "Left hearing sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0633\u0645\u0639\u064A \u0627\u0644\u0623\u064A\u0633\u0631" },
                new[] { "Standardized", "Right hearing sensor", "\u0627\u0644\u0645\u0633\u062A\u0634\u0639\u0631 \u0627\u0644\u0633\u0645\u0639\u064A \u0627\u0644\u0623\u064A\u0645\u0646" },
                new[] { "Standardized", "Raider", "\u063A\u0627\u0632\u064A" },
                new[] { "Ideology", "Raider", "\u0627\u0644\u063A\u0632\u0648" },
                new[] { "Standardized", "Investigate", "\u062A\u062D\u0642\u064A\u0642 \u0641\u064A" },
                new[] { "Core", "Spot", "\u0645\u0643\u0627\u0646" },
                new[] { "Odyssey", "Fishing Spot", "\u0645\u0648\u0642\u0639 \u0635\u064A\u062F \u0627\u0644\u0633\u0645\u0643" },
                new[] { "Standardized", "Body", "\u062C\u0633\u0645" },
                new[] { "Ideology", "Flesh Purity", "\u0646\u0642\u0627\u0621 \u0627\u0644\u062C\u0633\u062F" },
                new[] { "Ideology", "Medicine Maker", "\u0635\u0627\u0646\u0639 \u0627\u0644\u0623\u062F\u0648\u064A\u0629" },
                new[] { "Biotech", "Tunneler", "\u062D\u0641\u0627\u0631" },
                new[] { "Ideology", "Tunneler", "\u0646\u0627\u0633\u0643 \u0627\u0644\u0623\u0646\u0641\u0627\u0642" }
            };
            foreach (var x in extras)
            {
                T.True(Find(data, x[0], x[1], x[2]).Count > 0, "required term present: [" + x[0] + "] " + x[1] + " -> " + T.Show(x[2]));
            }
            var fleshbeast = Find(data, "Anomaly", "Fleshbeast", "\u0648\u062D\u0634 \u0627\u0644\u0644\u062D\u0645");
            T.True(fleshbeast.Count > 0 && fleshbeast[0].ArabicIndefinite == "\u0648\u062D\u0634 \u0644\u062D\u0645", "Fleshbeast carries its indefinite form");
        }

        // ------------------------------------------------------------------------------------------ parser

        private static void ParserIssues()
        {
            var d = GlossaryParser.Parse("Colonist | \u0645\u0633\u062A\u0639\u0645\u0631\n");
            T.Equal(1, d.Issues.Count, "an entry before any section is reported");
            T.Equal(0, d.Entries.Count, "and skipped");
            if (d.Issues.Count > 0) T.Contains(d.Issues[0].Message, "before any", "the message names the problem");

            d = GlossaryParser.Parse("[Core]\nColonist | \u0645\u0633\u062A\u0639\u0645\u0631 | bogus=1\n");
            T.Equal(1, d.Issues.Count, "an unknown attribute is reported");
            T.Equal(1, d.Entries.Count, "but the entry is kept");

            d = GlossaryParser.Parse("[Core\nColonist | x\n");
            T.Equal(2, d.Issues.Count, "an unterminated section header leaves the entry without a section");

            d = GlossaryParser.Parse("[Core]\nColonist\n | x\nA | \n");
            T.Equal(3, d.Issues.Count, "entries without an Arabic text or an English text are reported");
            T.Equal(0, d.Entries.Count, "and skipped");

            d = GlossaryParser.Parse("[Core]\nA | x | id=dup\nB | y | id=dup\n");
            T.Equal(1, d.Issues.Count, "a duplicate id is reported");
            T.Equal(2, d.Entries.Count, "both entries are kept");
            if (d.Entries.Count == 2) T.True(d.Entries[0].Id != d.Entries[1].Id, "the second one gets another id");

            d = GlossaryParser.Parse("[Core]\nA | x | mode=weird\n");
            T.Equal(1, d.Issues.Count, "an unknown mode is reported");

            d = GlossaryParser.Parse("@glossary-version\n[ ]\n");
            T.Equal(2, d.Issues.Count, "a version without value and an empty section name are reported");

            // the parser never throws, whatever it is given
            T.Equal(0, GlossaryParser.Parse(null).Entries.Count, "null");
            T.Equal(0, GlossaryParser.Parse("").Entries.Count, "the empty text");
            GlossaryParser.Parse("\u0001\u0002 garbage ||| [[[ ]]] = = |");
            GlossaryParser.Parse(new string('|', 1000));
            GlossaryParser.Parse("[Core]\n" + new string('x', 5000) + " | " + new string('y', 5000) + "\n");
            T.True(true, "garbage input does not throw");
        }

        private static void ParserFeatures()
        {
            // BOM and Windows line breaks
            string bom = ((char)0xFEFF).ToString();
            var d = GlossaryParser.Parse(bom + "@glossary-version 3\r\n[Core]\r\nColonist | \u0645\u0633\u062A\u0639\u0645\u0631\r\n");
            T.Equal("3", d.DeclaredVersion, "the declared version is read after a BOM");
            T.Equal(1, d.Entries.Count, "one entry");
            if (d.Entries.Count == 1) T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", d.Entries[0].Arabic, "no carriage return in the Arabic text");

            // the mini glossary
            var mini = GlossaryParser.Parse(Fixtures.MiniGlossary);
            T.Equal(0, mini.Issues.Count, "the mini glossary is well formed");
            T.Equal("5", mini.DeclaredVersion, "its declared version");
            var colonist = ById(mini, "core.colonist");
            T.True(colonist != null, "the default id is section.slug");
            if (colonist != null)
            {
                T.Equal(3, colonist.Line, "line numbers are 1-based");
                T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", colonist.ArabicPlural, "plural attribute");
                T.Equal("Core", colonist.Section, "section");
                T.True(colonist.Mode == GlossaryMode.Any && !colonist.CaseSensitive && !colonist.Guard && !colonist.Adjective, "defaults");
            }
            var psychic = ById(mini, "core.psychic");
            T.True(psychic != null && psychic.Mode == GlossaryMode.Whole && psychic.Adjective, "mode=whole and adj");
            T.True(ById(mini, "core.psychic-drone") != null, "the slug of a two-word term");
            var will = ById(mini, "core.will");
            T.True(will != null && will.CaseSensitive, "cs");
            var spot = ById(mini, "core.spot");
            T.True(spot != null && spot.Guard, "guard");
            var inv = ById(mini, "core.investigate");
            T.True(inv != null && inv.Mode == GlossaryMode.Start, "mode=start");
            var empire = ById(mini, "core.empire");
            T.True(empire != null && empire.EnglishAsWritten.Length == 2 && empire.EnglishAsWritten[0] == "The Empire" && empire.English[0] == "Empire",
                "aliases and a leading The");
            var mech = ById(mini, "core.mechanoid");
            T.True(mech != null && mech.English.Length == 2 && mech.English[1] == "Mech", "an alias");
            var meme = ById(mini, "ideology.raider");
            T.True(meme != null && meme.WhenKey == "MemeDef" && meme.Mode == GlossaryMode.Whole, "an explicit id and key=");
            T.True(ById(mini, "royalty.foo") != null && ById(mini, "ideology.foo") != null, "the same English text in two sections");
            var std = ById(mini, "standardized.raider");
            T.True(std != null && std.SectionPackages.Length == 0, "Standardized has no package restriction");
            if (colonist != null) T.True(colonist.SectionPackages.Length == 1 && colonist.SectionPackages[0] == "ludeon.rimworld", "Core is the base game package");
            if (ById(mini, "royalty.foo") != null) T.Equal("ludeon.rimworld.royalty", ById(mini, "royalty.foo").SectionPackages[0], "Royalty package");

            var packages = GlossaryParser.Parse("[Custom | My.Mod, other.mod]\nA | x\n");
            T.True(packages.Entries.Count == 1 && packages.Entries[0].SectionPackages.Length == 2 && packages.Entries[0].SectionPackages[0] == "my.mod",
                "explicit package ids are lower-cased");

            var mods = GlossaryParser.Parse("[Core]\nA | x | mod=A.B;c.d | key=Def.label | note=hello world\n");
            T.True(mods.Entries.Count == 1 && mods.Entries[0].WhenMods != null && mods.Entries[0].WhenMods.Length == 2 && mods.Entries[0].WhenMods[0] == "a.b" &&
                   mods.Entries[0].WhenKey == "Def.label" && mods.Entries[0].Note == "hello world", "mod=, key= and note=");
            if (mods.Entries.Count == 1) T.Equal(2, mods.Entries[0].Specificity, "two context conditions");
        }

        private static GlossaryEntry ById(GlossaryData data, string id)
        {
            foreach (var e in data.Entries) if (e.Id == id) return e;
            return null;
        }

        private static void Versioning()
        {
            var a = GlossaryParser.Parse(Fixtures.MiniGlossary);
            var b = GlossaryParser.Parse(Fixtures.MiniGlossary);
            T.Equal(a.Version, b.Version, "the same text gives the same version");
            T.True(a.Version.StartsWith("gl-5.", StringComparison.Ordinal), "the version starts with the declared one");

            var declared = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("@glossary-version 5", "@glossary-version 6"));
            T.True(a.Version != declared.Version, "a new declared version changes the version");

            var edited = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C", "Turret | \u0645\u0646\u0627\u0631\u0629"));
            T.True(a.Version != edited.Version, "editing a term changes the version");
            T.Equal("gl-5", edited.Version.Substring(0, 4), "the declared part stays the same");

            // only the edited entry's signature changes
            int changed = 0;
            for (int i = 0; i < a.Entries.Count; i++)
            {
                if (a.Entries[i].Signature != edited.Entries[i].Signature)
                {
                    changed++;
                    T.Equal("core.turret", a.Entries[i].Id, "the changed signature belongs to the edited entry");
                }
            }
            T.Equal(1, changed, "exactly one signature changed");

            var removed = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C\n", ""));
            T.True(a.Version != removed.Version, "removing a term changes the version");
            T.Equal(a.Entries.Count - 1, removed.Entries.Count, "one entry less");

            var reordered = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C\nSlag | \u062E\u0631\u062F\u0629\n", "Slag | \u062E\u0631\u062F\u0629\nTurret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C\n"));
            T.True(a.Version != reordered.Version, "the order of the entries is part of the version (it decides ties)");

            var attribute = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C", "Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C | cs"));
            T.True(a.Version != attribute.Version, "a matching attribute is part of the version");

            var note = GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Slag | \u062E\u0631\u062F\u0629\n", "Slag | \u062E\u0631\u062F\u0629 | note=just a remark\n"));
            T.Equal(a.Version, note.Version, "a note does not change the version (it does not influence any translation)");

            // the data signature of the pipeline
            var pipeline = Fixtures.PipelineFor(Fixtures.MiniGlossary, null);
            T.Equal(a.Version, pipeline.GlossaryVersion, "the pipeline reports the glossary version");
            T.Equal("gl-none", new ArabicPipeline(null, null).GlossaryVersion, "no glossary");
        }

        // ------------------------------------------------------------------------------------------ matching

        private static void WholeTexts()
        {
            var m = Mini;
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "Colonist", Core), "a term alone");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "colonist", Core), "the case of the English text does not matter");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "COLONIST", Core), "capitals");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", Offline(m, "Colonists", Core), "the English plural gives the Arabic plural");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "The colonist", Core), "the definite article is added");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "A colonist", Core), "an indefinite article adds nothing");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", Offline(m, "the colonists", Core), "definite plural");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631.", Offline(m, "Colonist.", Core), "punctuation around the term stays");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631!", Offline(m, "Colonist!", Core), "an exclamation mark stays");
            T.Equal("  \u0645\u0633\u062A\u0639\u0645\u0631  ", Offline(m, "  Colonist  ", Core), "spaces around the term stay");
            T.Equal("<b>\u0645\u0633\u062A\u0639\u0645\u0631</b>", Offline(m, "<b>Colonist</b>", Core), "tags around the term stay");
            T.Equal("\\n\u0645\u0633\u062A\u0639\u0645\u0631", Offline(m, "\\nColonist", Core), "an escaped line break before the term stays");
            T.Equal("\u062E\u0631\u062F\u0629", Offline(m, "Slag", Core), "another term");
            T.Equal("\u0645\u064A\u0643\u0627\u0646\u0648\u064A\u062F", Offline(m, "Mech", Core), "an alias");
            T.Equal("\u0645\u064A\u0643\u0627\u0646\u0648\u064A\u062F", Offline(m, "Mechanoid", Core), "the main name");
            T.Equal("\u0627\u0644\u0625\u0645\u0628\u0631\u0627\u0637\u0648\u0631\u064A\u0629", Offline(m, "Empire", Core), "a term with a leading The in the glossary");
            T.Equal("\u0627\u0644\u0625\u0645\u0628\u0631\u0627\u0637\u0648\u0631\u064A\u0629", Offline(m, "The Empire", Core), "the article of a term that has it already");
            T.Equal("\u0646\u0641\u0633\u064A", Offline(m, "Psychic", Core), "an adjective as a complete label");
            T.Equal("\u0637\u0646\u064A\u0646 \u0646\u0641\u0633\u064A", Offline(m, "Psychic Drone", Core), "a two-word term beats the adjective inside it");
            T.Equal("\u0625\u0631\u0627\u062F\u0629", Offline(m, "Will", Core), "a case-sensitive term with a capital");

            // not whole texts, not terms, not word matches
            T.True(Offline(m, "will", Core) == null && Marked(m, "I will go", Core) == null, "a case-sensitive term is not matched in lower case");
            T.True(Offline(m, "Hello world", Core) == null && Marked(m, "Hello world", Core) == null, "no term, no plan");
            T.True(Offline(m, "Colonists and turrets", Core) == null, "two terms are not an offline text");
            T.True(Offline(m, "Colonistic", Core) == null && Marked(m, "Colonistic", Core) == null, "a longer word is not the term");
            T.True(Offline(m, "Anticolonist", Core) == null && Marked(m, "Anticolonist", Core) == null, "a word that contains the term is not the term");
            T.True(Offline(m, "{Colonist}", Core) == null && Marked(m, "{Colonist}", Core) == null, "a term inside a placeholder is left alone");
            T.True(Offline(m, "[Colonist]", Core) == null && Marked(m, "[Colonist]", Core) == null, "a term inside square brackets is left alone");
            T.True(Offline(m, "<Colonist>", Core) == null && Marked(m, "<Colonist>", Core) == null, "a term inside a tag is left alone");
            T.True(Offline(m, "A psychic attack", Core) == null && Marked(m, "A psychic attack", Core) == null, "an adjective inside a sentence is left alone");
            T.True(Offline(m, "", Core) == null && Marked(m, "", Core) == null, "the empty text");
            T.True(Offline(m, null, Core) == null, "null");
            T.False(m.Scan(null, Core).HasMatches, "scanning null finds nothing");
            T.Equal(1, m.Scan("Colonist", null).Matches.Count, "a missing context means no context");
        }

        private static void MarkedTexts()
        {
            var m = Mini;
            T.Equal("{GL0} is hungry.", Marked(m, "The colonist is hungry.", Core), "a term in a sentence becomes a marker (with its article)");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", Units(m, "The colonist is hungry.", Core), "the unit carries the definite form");
            T.Equal("{GL0} and {GL1}", Marked(m, "A colonist and a turret", Core), "two terms, two markers");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631 # \u0628\u0631\u062C", Units(m, "A colonist and a turret", Core), "two units with their indefinite forms");
            T.Equal("{PAWN_nameDef} is {GL0}", Marked(m, "{PAWN_nameDef} is a colonist", Core), "placeholders are not touched");
            T.Equal("{GL0} you go?", Marked(m, "Will you go?", Core), "a case-sensitive term at the start of a sentence");
            T.Equal("{GL0} {0}", Marked(m, "Investigate {0}", Core), "a start-only term at the start");
            T.True(Marked(m, "Please investigate {0}", Core) == null, "a start-only term elsewhere is not matched");
            T.Equal("Hello {GL0}, how are you?", Marked(m, "Hello colonist, how are you?", Core), "a term between punctuation");
            T.Equal("{GL0}\n{GL1}", Marked(m, "Colonist\nTurret", Core), "terms on separate lines");
            T.Equal("<b>{GL0}</b> is here", Marked(m, "<b>Colonist</b> is here", Core), "tags around a term stay");

            // the plan never changes the source or loses text
            string source = "Send the colonists to the turret, now!";
            var plan = m.Plan(source, Core, true);
            T.Equal(source, plan.Source, "the plan keeps the source");
            T.Equal("Send {GL0} to {GL1}, now!", plan.Marked, "several markers, articles included");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646 # \u0627\u0644\u0628\u0631\u062C", Units(m, source, Core), "definite plural and singular");

            // no markers requested: the scan still finds the terms, the plan does not rewrite the text
            var noMarkers = m.Plan("The colonist is hungry.", Core, false);
            T.True(noMarkers.Marked == null && noMarkers.Units.Count == 0 && noMarkers.Scan.Matches.Count == 1 && !noMarkers.Offline, "a plan without markers");

            // a text that already contains a marker cannot be protected with markers
            var collision = m.Plan("The colonist {GL0}", Core, true);
            T.True(collision.Scan.MarkerCollision && collision.Marked == null && collision.Units.Count == 0, "a marker in the source disables markers");
            T.True(m.Plan("{GL0}", Core, true).Marked == null, "a marker alone");
        }

        private static void CompoundTerms()
        {
            var m = Mini;
            T.Equal("{GL0}", Marked(m, "Slag chunk", Core), "two adjacent nouns are one unit");
            T.Equal("\u0643\u062A\u0644\u0629 \u062E\u0631\u062F\u0629", Units(m, "Slag chunk", Core), "head first, modifier after it");
            T.Equal("\u0643\u062A\u0644 \u062E\u0631\u062F\u0629", Units(m, "Slag chunks", Core), "plural head");
            T.Equal("\u0643\u062A\u0644\u0629 \u0627\u0644\u062E\u0631\u062F\u0629", Units(m, "The slag chunk", Core), "a definite modifier keeps its article");
            T.Equal("{GL0} and {GL1}", Marked(m, "Slag and chunk", Core), "terms apart are not a compound");
            T.Equal("{GL0}\n{GL1}", Marked(m, "Slag\nchunk", Core), "terms on different lines are not a compound");
            T.True(Offline(m, "Slag chunk", Core) == null, "a compound is translated through its marker, not offline");
            T.Equal("\u062E\u0631\u062F\u0629", Offline(m, "Slag", Core), "the modifier alone");
            T.Equal("\u0643\u062A\u0644\u0629", Offline(m, "Chunk", Core), "the head alone");

            // longest first: the two-word term wins over the shorter terms inside it
            T.Equal("\u0637\u0646\u064A\u0646 \u0646\u0641\u0633\u064A", Offline(m, "Psychic Drone", Core), "two words beat one");
            T.Equal("{GL0} is loud", Marked(m, "Psychic drone is loud", Core), "inside a sentence");
        }

        private static void Context()
        {
            var m = Mini;
            var none = GlossaryContext.None;
            var ideology = new GlossaryContext("ludeon.rimworld.ideology", "MemeDef.label", "");
            var ideologyOther = new GlossaryContext("ludeon.rimworld.ideology", "ThingDef.label", "");
            var royalty = new GlossaryContext("ludeon.rimworld.royalty", "ThingDef.label", "");

            // the same English text in different DLC sections: the section of the mod decides, then the file order
            T.Equal("\u0641\u0648", Offline(m, "Foo", none), "no mod: the first section in the file");
            T.Equal("\u0641\u0648", Offline(m, "Foo", royalty), "Royalty text");
            T.Equal("\u0641\u0648\u0648", Offline(m, "Foo", ideology), "Ideology text");
            T.Equal("\u0641\u0648\u0648", Offline(m, "Foo", ideologyOther), "Ideology text, another def type");
            T.Equal("\u0641\u0648", Offline(m, "Foo", Core), "a base game text falls back to the first section");

            // key conditions: the Ideology meme only inside a MemeDef
            T.Equal("\u0627\u0644\u063A\u0632\u0648", Offline(m, "Raider", ideology), "the Ideology meme Raider");
            T.Equal("\u063A\u0627\u0632\u064A", Offline(m, "Raider", ideologyOther), "every other Raider (Ideology mod, another def type)");
            T.Equal("\u063A\u0627\u0632\u064A", Offline(m, "Raider", none), "no context at all");
            T.Equal("\u063A\u0627\u0632\u064A", Offline(m, "Raider", Core), "a Keyed string");
            T.Equal("\u063A\u0627\u0632\u064A", Offline(m, "Raider", royalty), "another mod");
            T.Equal("\u063A\u0632\u0627\u0629", Offline(m, "Raiders", ideology), "the plural exists only for the standardized term");
            T.Equal("{GL0} attack", Marked(m, "Raider attack", ideology), "the whole-text meme does not apply inside a sentence");
            T.Equal("\u063A\u0627\u0632\u064A", Units(m, "Raider attack", ideology), "which leaves the standardized term");
            var any = new GlossaryContext("ludeon.rimworld.ideology", "", "") { AnyContext = true };
            T.Equal("\u0627\u0644\u063A\u0632\u0648", Offline(m, "Raider", any), "an unknown context counts every term as applicable (used to migrate old cache entries)");

            // grammatical hints of the def field
            var female = new GlossaryContext("ludeon.rimworld", "PawnKindDef.labelFemale", "female");
            var plural = new GlossaryContext("ludeon.rimworld", "PawnKindDef.labelPlural", "plural");
            T.Equal("\u0645\u063A\u062A\u0627\u0644", Offline(m, "Assassin", Core), "the base form");
            T.Equal("\u0645\u063A\u062A\u0627\u0644\u0629", Offline(m, "Assassin", female), "labelFemale asks for the feminine form");
            T.Equal("\u0645\u063A\u062A\u0627\u0644\u064A\u0646", Offline(m, "Assassin", plural), "labelPlural asks for the plural form");
            T.Equal("\u0645\u063A\u062A\u0627\u0644\u064A\u0646", Offline(m, "Assassins", female), "an English plural stays plural");
            T.Equal("female", GlossaryContext.HintForField("labelFemale"), "hint of labelFemale");
            T.Equal("plural", GlossaryContext.HintForField("labelPlural"), "hint of labelPlural");
            T.Equal("femaleplural", GlossaryContext.HintForField("labelFemalePlural"), "hint of labelFemalePlural");
            T.Equal("", GlossaryContext.HintForField("label"), "no hint for label");
            T.Equal("", GlossaryContext.HintForField(null), "no hint for null");
            var ctx = new GlossaryContext(" LUDEON.RimWorld ", null, " Female ");
            T.True(ctx.ModPackageId == "ludeon.rimworld" && ctx.Key == "" && ctx.Hint == "female", "a context normalizes its fields");
        }

        private static void Guards()
        {
            var m = Mini;
            // a noun that is also a verb is enforced as a noun only
            T.Equal("\u0645\u0643\u0627\u0646", Offline(m, "Spot", Core), "the noun alone");
            T.Equal("\u0623\u0645\u0627\u0643\u0646", Offline(m, "Spots", Core), "the noun, plural");
            T.Equal("\u0645\u0643\u0627\u0646", Offline(m, "A spot", Core), "with an article");
            T.Equal("\u0627\u0644\u0645\u0643\u0627\u0646", Offline(m, "The spot", Core), "definite");
            T.Equal("Sleeping {GL0}", Marked(m, "Sleeping spot", Core), "as the head of a noun phrase");
            T.Equal("{GL0} is free.", Marked(m, "Spot is free.", Core), "at the start, followed by a verb");
            T.Equal("{GL0} is free.", Marked(m, "The spot is free.", Core), "with an article, followed by a verb");
            T.True(Marked(m, "Spot the enemy", Core) == null, "the verb at the start of a sentence (followed by an article)");
            T.True(Marked(m, "Spot your enemy", Core) == null, "the verb followed by a pronoun");
            T.True(Marked(m, "to spot the enemy", Core) == null, "the verb after to");
            T.True(Marked(m, "Please spot the enemy.", Core) == null, "the verb after please");
            T.True(Marked(m, "You can spot them", Core) == null, "the verb after a modal verb");
            T.True(Offline(m, "Spot the enemy", Core) == null, "and never an offline text");
            T.Equal("Look: {GL0} is free.", Marked(m, "Look: spot is free.", Core), "after a colon the noun is fine");
        }

        private static void Plurals()
        {
            T.Equal("Colonists", string.Join("|", GlossaryIndex.PluralsOf("Colonist")), "regular plural");
            T.Equal("Flies", string.Join("|", GlossaryIndex.PluralsOf("Fly")), "consonant + y");
            T.Equal("Days", string.Join("|", GlossaryIndex.PluralsOf("Day")), "vowel + y");
            T.Equal("Boxes", string.Join("|", GlossaryIndex.PluralsOf("Box")), "x");
            T.Equal("Buses", string.Join("|", GlossaryIndex.PluralsOf("Bus")), "s");
            T.Equal("Churches", string.Join("|", GlossaryIndex.PluralsOf("Church")), "ch");
            T.Equal("Dishes", string.Join("|", GlossaryIndex.PluralsOf("Dish")), "sh");
            T.Equal("Mech Clusters", string.Join("|", GlossaryIndex.PluralsOf("Mech Cluster")), "the last word is the head");
            T.Equal(0, GlossaryIndex.PluralsOf("").Length, "nothing");
            T.True(GlossaryIndex.IsWordChar('a') && GlossaryIndex.IsWordChar('7') && GlossaryIndex.IsWordChar('_') && !GlossaryIndex.IsWordChar(' ') && !GlossaryIndex.IsWordChar('{'),
                "word characters");
        }

        private static void Possessives()
        {
            var ix = new GlossaryIndex(GlossaryParser.Parse(
                "[Standardized]\n" +
                "Driver's child | \u0637\u0641\u0644 \u0627\u0644\u0633\u0627\u0626\u0642\n" +
                "Elders' cook | \u0637\u0628\u0627\u062E \u0627\u0644\u0634\u064A\u0648\u062E\n" +
                "Driver | \u0633\u0627\u0626\u0642\n" +
                "Cave-born child | \u0637\u0641\u0644 \u0645\u0648\u0644\u0648\u062F \u0641\u064A \u0627\u0644\u0643\u0647\u0641\n"));
            var none = GlossaryContext.None;
            T.Equal("\u0637\u0641\u0644 \u0627\u0644\u0633\u0627\u0626\u0642", Offline(ix, "driver's child", none), "a possessive term");
            T.Equal("\u0637\u0641\u0644 \u0627\u0644\u0633\u0627\u0626\u0642", Offline(ix, "Driver\u2019s child", none), "with the typographic apostrophe");
            T.Equal("\u0637\u0641\u0644 \u0627\u0644\u0633\u0627\u0626\u0642", Offline(ix, "DRIVER'S CHILD", none), "in capitals");
            T.Equal("\u0637\u0628\u0627\u062E \u0627\u0644\u0634\u064A\u0648\u062E", Offline(ix, "elders' cook", none), "a plural possessive");
            T.Equal("Meet {GL0}.", Marked(ix, "Meet driver's child.", none), "inside a text, with its 's");
            T.Equal("{GL0}'s seat", Marked(ix, "driver's seat", none), "a text whose possessive is not a term keeps its 's");
            T.Equal("{GL0} child", Marked(ix, "driver child", none), "without the apostrophe it is not the possessive term");
            T.Equal("Ask {GL0} now", Marked(ix, "Ask elders' cook now", none), "a plural possessive inside a text");
            T.Equal("\u0637\u0641\u0644 \u0645\u0648\u0644\u0648\u062F \u0641\u064A \u0627\u0644\u0643\u0647\u0641", Offline(ix, "cave-born child", none), "a term with a hyphen");
            T.Equal("\u0637\u0641\u0644 \u0645\u0648\u0644\u0648\u062F \u0641\u064A \u0627\u0644\u0643\u0647\u0641", Offline(ix, "Cave born child", none), "and the same term written with spaces");
            T.Equal("Meet {GL0}.", Marked(ix, "Meet cave-born child.", none), "a hyphenated term inside a text");
            T.True(Marked(ix, "cave - born child", none) == null, "a spaced hyphen does not join the words");
        }

        private static void Signatures()
        {
            var m = Mini;
            string none = GlossaryIndex.SignatureOf(m.Scan("Hello world", Core));
            T.Equal("0", none, "no term, no signature");
            T.Equal("0", GlossaryIndex.SignatureOf(null), "null scan");

            string colonist = GlossaryIndex.SignatureOf(m.Scan("Colonist", Core));
            T.True(colonist != "0" && colonist.Length == 16, "a term gives a 16-digit signature");
            T.Equal(colonist, GlossaryIndex.SignatureOf(m.Scan("colonist", Core)), "the case of the text does not matter");
            T.True(GlossaryIndex.SignatureOf(m.Scan("Colonists", Core)) != colonist, "the plural form is part of the signature");
            T.True(GlossaryIndex.SignatureOf(m.Scan("The colonist", Core)) != colonist, "the definite article is part of the signature");
            T.True(GlossaryIndex.SignatureOf(m.Scan("A colonist", Core)) != GlossaryIndex.SignatureOf(m.Scan("The colonist", Core)), "definite and indefinite differ");
            T.Equal(GlossaryIndex.SignatureOf(m.Scan("Colonist is hungry", Core)), GlossaryIndex.SignatureOf(m.Scan("Colonist", Core)),
                "the signature only depends on the terms found, not on the other words");

            // a changed term changes exactly the signatures of the texts that contain it
            var edited = new GlossaryIndex(GlossaryParser.Parse(Fixtures.MiniGlossary.Replace("Turret | \u0628\u0631\u062C", "Turret | \u0645\u0646\u0627\u0631\u0629")));
            T.Equal(GlossaryIndex.SignatureOf(m.Scan("The colonist is hungry.", Core)), GlossaryIndex.SignatureOf(edited.Scan("The colonist is hungry.", Core)),
                "a text without the edited term keeps its signature");
            T.True(GlossaryIndex.SignatureOf(m.Scan("The turret is old.", Core)) != GlossaryIndex.SignatureOf(edited.Scan("The turret is old.", Core)),
                "a text with the edited term gets a new signature");
            T.True(GlossaryIndex.SignatureOf(m.Scan("Hello", Core)) == GlossaryIndex.SignatureOf(edited.Scan("Hello", Core)), "a text without terms stays 0");

            // a newly added term changes the signature of texts that now contain it
            var added = new GlossaryIndex(GlossaryParser.Parse(Fixtures.MiniGlossary + "[Core | ludeon.rimworld]\nHello | \u0645\u0631\u062D\u0628\u0627\n"));
            T.True(GlossaryIndex.SignatureOf(m.Scan("Hello there", Core)) != GlossaryIndex.SignatureOf(added.Scan("Hello there", Core)), "a new term affects the texts it matches");
        }

        // ------------------------------------------------------------------------------------------ restoring

        private static void Restoring()
        {
            var m = Mini;
            var plan = m.Plan("The colonist is hungry.", Core, true);
            string restored;
            T.True(GlossaryIndex.Restore(plan, "{GL0} \u062C\u0627\u0626\u0639.", out restored), "a kept marker is restored");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", restored, "with the approved term");
            T.True(GlossaryIndex.Restore(plan, "{ GL0 } \u062C\u0627\u0626\u0639.", out restored) && restored == "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", "spaces inside the marker are tolerated");
            T.True(GlossaryIndex.Restore(plan, "{gl0} \u062C\u0627\u0626\u0639.", out restored) == false, "a marker in another case is lost");
            T.True(GlossaryIndex.HasMarker("{GL0}") && GlossaryIndex.HasMarker("x { GL12 } y") && !GlossaryIndex.HasMarker("{0}") && !GlossaryIndex.HasMarker("") && !GlossaryIndex.HasMarker(null),
                "marker detection");

            T.False(GlossaryIndex.Restore(plan, "\u062C\u0627\u0626\u0639.", out restored), "a lost marker fails");
            T.Equal("\u062C\u0627\u0626\u0639.", restored, "and the text stays as it is");
            T.False(GlossaryIndex.Restore(plan, "{GL0} \u0648 {GL0}", out restored), "a duplicated marker fails");
            T.False(GlossaryIndex.Restore(plan, "{GL1} \u062C\u0627\u0626\u0639", out restored), "an unknown marker fails");
            T.False(GlossaryIndex.Restore(plan, "{GL0} {GL1}", out restored), "an extra marker fails");
            T.False(GlossaryIndex.Restore(plan, null, out restored), "null fails");

            var noUnits = m.Plan("Hello world", Core, true);
            T.True(GlossaryIndex.Restore(noUnits, "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", out restored) && restored == "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", "a plan without markers returns the text");
            T.True(GlossaryIndex.Restore(null, "x", out restored) && restored == "x", "no plan");

            // two markers
            var two = m.Plan("A colonist and a turret", Core, true);
            T.True(GlossaryIndex.Restore(two, "{GL1} \u0648 {GL0}", out restored) && restored == "\u0628\u0631\u062C \u0648 \u0645\u0633\u062A\u0639\u0645\u0631", "markers may change places");

            // lam + the article: li + al -> lil
            var give = m.Plan("Give it to the colonist", Core, true);
            T.Equal("Give it to {GL0}", give.Marked, "the plan of the lam test");
            T.True(GlossaryIndex.Restore(give, "\u0623\u0639\u0637\u0647 \u0644{GL0}", out restored) && restored == "\u0623\u0639\u0637\u0647 \u0644\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "the alef of the article is dropped after a prefixed lam");
            T.True(GlossaryIndex.Restore(give, "\u0644{GL0}", out restored) && restored == "\u0644\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "also at the start of the text");
            T.True(GlossaryIndex.Restore(give, "\u0643\u0644{GL0}", out restored) && restored == "\u0643\u0644\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "a lam that ends another word is not a prefix");
            T.True(GlossaryIndex.Restore(give, "\u0623\u0639\u0637\u0647 {GL0}", out restored) && restored == "\u0623\u0639\u0637\u0647 \u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "without a prefix the article stays");

            // the restored text never contains a marker, and the approved text is the one of the plan
            var many = m.Plan("The colonist, the turret, the colonist and the slag chunk.", Core, true);
            T.Equal(4, many.Units.Count, "four units");
            var sb = new StringBuilder();
            for (int i = 0; i < many.Units.Count; i++) sb.Append("{GL").Append(i).Append("} ");
            T.True(GlossaryIndex.Restore(many, sb.ToString(), out restored), "four markers restore");
            T.False(GlossaryIndex.HasMarker(restored), "no marker is left");
            foreach (var u in many.Units) T.Contains(restored, u.Arabic, "the unit text is in the result: " + T.Show(u.Arabic));
        }

        // ------------------------------------------------------------------------------------------ shipped terms

        private static void ShippedTerms()
        {
            var ix = Fixtures.ShippedIndex;
            var none = GlossaryContext.None;
            var biotech = new GlossaryContext("ludeon.rimworld.biotech", "ThingDef.label", "");
            var ideologyMeme = new GlossaryContext("ludeon.rimworld.ideology", "MemeDef.label", "");
            var ideologyThing = new GlossaryContext("ludeon.rimworld.ideology", "ThingDef.label", "");

            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(ix, "Colonist", none), "Colonist");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", Offline(ix, "Colonists", none), "Colonists");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", Offline(ix, "The colonist", none), "The colonist");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", Offline(ix, "A colonist", none), "A colonist");

            // one English word, different meanings by context (the reason glossary entries are not blind replacements)
            T.Equal("\u063A\u0627\u0632\u064A", Offline(ix, "Raider", none), "Raider (standardized)");
            T.Equal("\u063A\u0627\u0632\u064A", Offline(ix, "Raider", ideologyThing), "Raider in an Ideology thing");
            T.Equal("\u0627\u0644\u063A\u0632\u0648", Offline(ix, "Raider", ideologyMeme), "Raider as an Ideology meme");
            T.Equal("\u063A\u0632\u0627\u0629", Offline(ix, "Raiders", none), "Raiders");
            T.Equal("\u062D\u0641\u0627\u0631", Offline(ix, "Tunneler", biotech), "Tunneler (Biotech mechanoid)");
            T.Equal("\u062D\u0641\u0627\u0631", Offline(ix, "Tunneler", none), "Tunneler without context");
            T.Equal("\u0646\u0627\u0633\u0643 \u0627\u0644\u0623\u0646\u0641\u0627\u0642", Offline(ix, "Tunneler", ideologyMeme), "Tunneler as an Ideology meme");

            // verbs that are also nouns
            T.Equal("\u0645\u0643\u0627\u0646", Offline(ix, "Spot", none), "Spot");
            T.True(Marked(ix, "Spot the enemy", none) == null, "Spot the enemy is not the noun");
            T.Equal("Sleeping {GL0}", Marked(ix, "Sleeping spot", none), "Sleeping spot");

            // adjective: only as a label
            T.Equal("\u0646\u0641\u0633\u064A", Offline(ix, "Psychic", none), "Psychic");
            T.True(Marked(ix, "A psychic attack", none) == null, "A psychic attack");

            // longest first
            T.Equal("\u0639\u0646\u0642\u0648\u062F \u0645\u064A\u0643\u0627\u0646\u0648\u064A\u062F\u0627\u062A", Offline(ix, "Mech Cluster", none), "Mech Cluster");
            T.Equal("\u0639\u0646\u0627\u0642\u064A\u062F \u0645\u064A\u0643\u0627\u0646\u0648\u064A\u062F\u0627\u062A", Offline(ix, "Mech Clusters", none), "Mech Clusters");
            T.Equal("{GL0}", Marked(ix, "Slag chunk", none), "Slag chunk is one unit");
            T.Equal("\u0643\u062A\u0644\u0629 \u062E\u0631\u062F\u0629", Units(ix, "Slag chunk", none), "Slag chunk, Arabic word order");

            // explicit definite / indefinite forms
            T.Equal("\u0648\u062D\u0634 \u0644\u062D\u0645", Offline(ix, "A fleshbeast", none), "an indefinite form of the glossary");
            T.Equal("\u0648\u062D\u0634 \u0627\u0644\u0644\u062D\u0645", Offline(ix, "Fleshbeast", none), "the approved form");
            T.Equal("\u0648\u062D\u0634 \u0627\u0644\u0644\u062D\u0645", Offline(ix, "The fleshbeast", none), "a phrase keeps its approved form as the definite form");

            // texts without terms
            T.False(ix.Scan("Hello world", none).HasMatches, "Hello world has no term");
            T.True(Marked(ix, "Hello world", none) == null && Offline(ix, "Hello world", none) == null, "and no plan");
        }
    }
}
