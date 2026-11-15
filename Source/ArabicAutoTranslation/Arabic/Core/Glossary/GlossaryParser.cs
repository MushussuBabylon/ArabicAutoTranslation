using System;
using System.Collections.Generic;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Reads Arabic/ArabicGlossary.txt (format documented in the header of that file). The parser never throws:
    /// a bad line becomes a <see cref="GlossaryIssue"/> and the entry is skipped.
    /// </summary>
    internal static class GlossaryParser
    {
        private static readonly string[] SlashSeparator = { " / " };

        /// <summary>Package ids of the DLC a section name stands for when the header does not name them.</summary>
        private static string[] DefaultPackagesFor(string section)
        {
            switch (section.ToLowerInvariant())
            {
                case "core": return new[] { "ludeon.rimworld" };
                case "royalty": return new[] { "ludeon.rimworld.royalty" };
                case "ideology": return new[] { "ludeon.rimworld.ideology" };
                case "biotech": return new[] { "ludeon.rimworld.biotech" };
                case "anomaly": return new[] { "ludeon.rimworld.anomaly" };
                case "odyssey": return new[] { "ludeon.rimworld.odyssey" };
                default: return new string[0];
            }
        }

        internal static GlossaryData Parse(string text)
        {
            var data = new GlossaryData();
            if (string.IsNullOrEmpty(text)) return data;
            if (text[0] == '\uFEFF') text = text.Substring(1);

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string section = null;
            string[] packages = new string[0];
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            int order = 0;

            for (int n = 0; n < lines.Length; n++)
            {
                int lineNo = n + 1;
                var line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.StartsWith("@glossary-version", StringComparison.Ordinal))
                {
                    var v = line.Substring("@glossary-version".Length).Trim();
                    if (v.Length > 0) data.DeclaredVersion = v;
                    else AddIssue(data, lineNo, "@glossary-version needs a value");
                    continue;
                }

                if (line[0] == '[')
                {
                    if (line[line.Length - 1] != ']')
                    {
                        AddIssue(data, lineNo, "unterminated section header");
                        continue;
                    }
                    var inner = line.Substring(1, line.Length - 2).Split('|');
                    section = inner[0].Trim();
                    if (section.Length == 0)
                    {
                        AddIssue(data, lineNo, "empty section name");
                        section = null;
                        continue;
                    }
                    if (inner.Length > 1 && inner[1].Trim().Length > 0)
                    {
                        var list = new List<string>();
                        foreach (var p in inner[1].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var id = p.Trim().ToLowerInvariant();
                            if (id.Length > 0) list.Add(id);
                        }
                        packages = list.ToArray();
                    }
                    else
                    {
                        packages = DefaultPackagesFor(section);
                    }
                    continue;
                }

                if (section == null)
                {
                    AddIssue(data, lineNo, "entry before any [Section]");
                    continue;
                }

                var entry = ParseEntry(data, line, lineNo, section, packages, order);
                if (entry == null) continue;
                order++;

                // a unique id: explicit, or section.slug, with a counter for duplicates
                var baseId = entry.Id;
                if (string.IsNullOrEmpty(baseId)) baseId = section.ToLowerInvariant() + "." + Slug(entry.English[0]);
                var uid = baseId;
                int counter = 2;
                while (!usedIds.Add(uid))
                {
                    if (!string.IsNullOrEmpty(entry.Id) && counter == 2)
                        AddIssue(data, lineNo, "duplicate id \"" + entry.Id + "\"");
                    uid = baseId + "-" + counter;
                    counter++;
                }
                entry.Id = uid;
                entry.Signature = ComputeSignature(entry);
                data.Entries.Add(entry);
            }

            ulong h = Fnv64.Start();
            h = Fnv64.AddField(h, data.DeclaredVersion);
            foreach (var e in data.Entries) h = Fnv64.AddU64(h, e.Signature);
            data.ContentHash = Fnv64.Hex(h);
            return data;
        }

        private static GlossaryEntry ParseEntry(GlossaryData data, string line, int lineNo, string section, string[] packages, int order)
        {
            var fields = line.Split('|');
            for (int i = 0; i < fields.Length; i++) fields[i] = fields[i].Trim();
            if (fields.Length < 2 || fields[0].Length == 0 || fields[1].Length == 0)
            {
                AddIssue(data, lineNo, "an entry needs \"English | Arabic\"");
                return null;
            }

            var e = new GlossaryEntry();
            e.Section = section;
            e.SectionPackages = packages;
            e.Order = order;
            e.Line = lineNo;
            e.Arabic = fields[1];

            var written = new List<string>();
            var surfaces = new List<string>();
            foreach (var part in fields[0].Split(SlashSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var a = part.Trim();
                if (a.Length == 0) continue;
                written.Add(a);
                var s = StripLeadingThe(a);
                if (s.Length == 0)
                {
                    AddIssue(data, lineNo, "empty English term");
                    continue;
                }
                surfaces.Add(s);
            }
            if (surfaces.Count == 0)
            {
                AddIssue(data, lineNo, "no English term");
                return null;
            }
            e.EnglishAsWritten = written.ToArray();
            e.English = surfaces.ToArray();

            for (int i = 2; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.Length == 0) continue;
                int eq = f.IndexOf('=');
                var key = (eq < 0 ? f : f.Substring(0, eq)).Trim().ToLowerInvariant();
                var value = eq < 0 ? "" : f.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "mode":
                        switch (value.ToLowerInvariant())
                        {
                            case "any": e.Mode = GlossaryMode.Any; break;
                            case "whole": e.Mode = GlossaryMode.Whole; break;
                            case "start": e.Mode = GlossaryMode.Start; break;
                            default: AddIssue(data, lineNo, "unknown mode \"" + value + "\""); break;
                        }
                        break;
                    case "cs": e.CaseSensitive = true; break;
                    case "adj": e.Adjective = true; break;
                    case "guard": e.Guard = true; break;
                    case "pl": e.ArabicPlural = NonEmpty(value); break;
                    case "fem": e.ArabicFeminine = NonEmpty(value); break;
                    case "fpl": e.ArabicFemininePlural = NonEmpty(value); break;
                    case "def": e.ArabicDefinite = NonEmpty(value); break;
                    case "indef": e.ArabicIndefinite = NonEmpty(value); break;
                    case "enpl":
                        {
                            var list = new List<string>();
                            foreach (var p in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var t = p.Trim();
                                if (t.Length > 0) list.Add(t);
                            }
                            e.EnglishPlural = list.ToArray();
                            break;
                        }
                    case "mod":
                        {
                            var list = new List<string>();
                            foreach (var p in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var t = p.Trim().ToLowerInvariant();
                                if (t.Length > 0) list.Add(t);
                            }
                            e.WhenMods = list.Count > 0 ? list.ToArray() : null;
                            break;
                        }
                    case "key": e.WhenKey = NonEmpty(value); break;
                    case "id": e.Id = NonEmpty(value); break;
                    case "derived": e.Derived = value; break;
                    case "note": e.Note = value; break;
                    default:
                        AddIssue(data, lineNo, "unknown attribute \"" + key + "\"");
                        break;
                }
            }
            return e;
        }

        private static string NonEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static void AddIssue(GlossaryData data, int line, string message)
        {
            data.Issues.Add(new GlossaryIssue { Line = line, Message = message });
        }

        /// <summary>"The Empire" -> "Empire". Only a leading "The " is removed.</summary>
        internal static string StripLeadingThe(string s)
        {
            if (s.Length > 4 && string.Compare(s, 0, "the ", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return s.Substring(4).Trim();
            return s;
        }

        internal static string Slug(string s)
        {
            var sb = new StringBuilder();
            bool dash = false;
            foreach (var ch in s.ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    if (dash && sb.Length > 0) sb.Append('-');
                    sb.Append(ch);
                    dash = false;
                }
                else
                {
                    dash = true;
                }
            }
            return sb.Length == 0 ? "term" : sb.ToString();
        }

        /// <summary>Hash of everything that decides how an entry matches and what it produces.</summary>
        internal static ulong ComputeSignature(GlossaryEntry e)
        {
            ulong h = Fnv64.Start();
            h = Fnv64.AddField(h, e.Section);
            h = Fnv64.AddField(h, string.Join(",", e.SectionPackages));
            foreach (var s in e.English) h = Fnv64.AddField(h, s.ToLowerInvariant());
            h = Fnv64.AddField(h, "|pl:" + string.Join(";", e.EnglishPlural));
            h = Fnv64.AddField(h, e.Arabic);
            h = Fnv64.AddField(h, e.ArabicPlural);
            h = Fnv64.AddField(h, e.ArabicFeminine);
            h = Fnv64.AddField(h, e.ArabicFemininePlural);
            h = Fnv64.AddField(h, e.ArabicDefinite);
            h = Fnv64.AddField(h, e.ArabicIndefinite);
            h = Fnv64.AddField(h, e.Mode.ToString());
            h = Fnv64.AddField(h, e.CaseSensitive ? "cs" : "");
            h = Fnv64.AddField(h, e.Adjective ? "adj" : "");
            h = Fnv64.AddField(h, e.Guard ? "guard" : "");
            h = Fnv64.AddField(h, e.WhenMods == null ? null : string.Join(";", e.WhenMods));
            h = Fnv64.AddField(h, e.WhenKey);
            return h;
        }
    }
}
