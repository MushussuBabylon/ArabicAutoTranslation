using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// The first element of the cache file: the processing identifiers that were current when it was written. An entry
    /// stores an identifier of its own only when it differs from the header (so the file stays small).
    /// </summary>
    internal sealed class CacheHeader
    {
        internal int Format = ArabicVersions.CacheFormat;
        internal string Language = ArabicVersions.Language;
        internal string GlossaryVersion = "";
        internal string PipelineVersion = "";
        internal string ShapingVersion = "";
        internal string BidiVersion = "";
        internal string DisplayVersion = "";
        /// <summary>The old (unprocessed) cache has been migrated already: do not look at it again.</summary>
        internal bool LegacyHandled;
    }

    /// <summary>The content of a cache file as read: no entry has been checked yet.</summary>
    internal sealed class ParsedCache
    {
        internal CacheHeader Header = new CacheHeader();
        internal List<ArabicCacheEntry> Entries = new List<ArabicCacheEntry>();
        /// <summary>Entries that could not be read (attributes missing).</summary>
        internal int Malformed;
    }

    /// <summary>
    /// XML (de)serialization of the Arabic cache. Texts are stored readable, with a private escape for the characters XML
    /// cannot carry or normalizes (line breaks, tabs, control characters, unpaired surrogates) and for the backslash.
    /// </summary>
    internal static class ArabicCacheFormat
    {
        internal const string RootName = "ArabicCache";
        internal const string EntryName = "E";

        // ------------------------------------------------------------------------------------------ escaping

        /// <summary>\ -> \\, LF -> \n, CR -> \r, TAB -> \t, other unsafe characters -> \uXXXX.</summary>
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string rep = null;
                if (c == '\\') rep = "\\\\";
                else if (c == '\n') rep = "\\n";
                else if (c == '\r') rep = "\\r";
                else if (c == '\t') rep = "\\t";
                else if (c < ' ' || c == '\uFFFE' || c == '\uFFFF') rep = "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        if (sb != null) sb.Append(c).Append(s[i + 1]);
                        i++;
                        continue;
                    }
                    rep = "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                }
                else if (char.IsLowSurrogate(c)) rep = "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);

                if (rep != null)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length + 16);
                        sb.Append(s, 0, i);
                    }
                    sb.Append(rep);
                }
                else if (sb != null)
                {
                    sb.Append(c);
                }
            }
            return sb == null ? s : sb.ToString();
        }

        internal static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char n = s[i + 1];
                if (n == '\\') { sb.Append('\\'); i++; }
                else if (n == 'n') { sb.Append('\n'); i++; }
                else if (n == 'r') { sb.Append('\r'); i++; }
                else if (n == 't') { sb.Append('\t'); i++; }
                else if (n == 'u' && TryHex4(s, i + 2, out c))
                {
                    sb.Append(c);
                    i += 5;
                }
                else
                {
                    sb.Append('\\');
                }
            }
            return sb.ToString();
        }

        private static bool TryHex4(string s, int start, out char value)
        {
            value = '\0';
            if (start + 4 > s.Length) return false;
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                char h = s[start + i];
                int d;
                if (h >= '0' && h <= '9') d = h - '0';
                else if (h >= 'A' && h <= 'F') d = h - 'A' + 10;
                else if (h >= 'a' && h <= 'f') d = h - 'a' + 10;
                else return false;
                v = v * 16 + d;
            }
            value = (char)v;
            return true;
        }

        // ------------------------------------------------------------------------------------------ writing

        private static string StatusCode(CacheStatus s)
        {
            return s == CacheStatus.Final ? "f" : (s == CacheStatus.Corrected ? "c" : "r");
        }

        private static string OriginCode(PipelineOrigin o)
        {
            switch (o)
            {
                case PipelineOrigin.Offline: return "o";
                case PipelineOrigin.UserEdit: return "u";
                case PipelineOrigin.Migrated: return "m";
                case PipelineOrigin.Regenerated: return "g";
                default: return "t";
            }
        }

        private static void Differing(XmlWriter w, string name, string value, string headerValue)
        {
            value = value ?? "";
            if (!string.Equals(value, headerValue ?? "", StringComparison.Ordinal))
            {
                w.WriteAttributeString(name, Escape(value));
            }
        }

        /// <summary>The XML text of a cache file.</summary>
        internal static string Serialize(IEnumerable<ArabicCacheEntry> entries, CacheHeader header)
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = " ",
                NewLineHandling = NewLineHandling.Entitize
            };
            using (var ms = new MemoryStream())
            {
                using (var w = XmlWriter.Create(ms, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement(RootName);
                    w.WriteAttributeString("format", header.Format.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("language", header.Language ?? "");
                    w.WriteAttributeString("glossary", Escape(header.GlossaryVersion));
                    w.WriteAttributeString("pipeline", Escape(header.PipelineVersion));
                    w.WriteAttributeString("shaping", Escape(header.ShapingVersion));
                    w.WriteAttributeString("bidi", Escape(header.BidiVersion));
                    w.WriteAttributeString("display", Escape(header.DisplayVersion));
                    w.WriteAttributeString("legacyHandled", header.LegacyHandled ? "1" : "0");

                    foreach (var e in entries)
                    {
                        w.WriteStartElement(EntryName);
                        w.WriteAttributeString("k", Escape(e.Key));
                        w.WriteAttributeString("s", Escape(e.Source));
                        w.WriteAttributeString("l", Escape(e.Logical));
                        w.WriteAttributeString("d", Escape(e.Display));
                        w.WriteAttributeString("st", StatusCode(e.Status));
                        w.WriteAttributeString("o", OriginCode(e.Origin));
                        if (!string.Equals(e.GlossarySig ?? "0", "0", StringComparison.Ordinal)) w.WriteAttributeString("gs", Escape(e.GlossarySig));
                        if (!string.IsNullOrEmpty(e.ModId)) w.WriteAttributeString("m", Escape(e.ModId));
                        if (!string.IsNullOrEmpty(e.ContextKey)) w.WriteAttributeString("ck", Escape(e.ContextKey));
                        if (!string.IsNullOrEmpty(e.ContextHint)) w.WriteAttributeString("ch", Escape(e.ContextHint));
                        Differing(w, "gv", e.GlossaryVersion, header.GlossaryVersion);
                        Differing(w, "pv", e.PipelineVersion, header.PipelineVersion);
                        Differing(w, "sv", e.ShapingVersion, header.ShapingVersion);
                        Differing(w, "bv", e.BidiVersion, header.BidiVersion);
                        Differing(w, "dv", e.DisplayVersion, header.DisplayVersion);
                        if (e.Format != header.Format) w.WriteAttributeString("f", e.Format.ToString(CultureInfo.InvariantCulture));
                        if (!string.Equals(e.Language ?? "", header.Language ?? "", StringComparison.Ordinal)) w.WriteAttributeString("lg", Escape(e.Language));
                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }

        // ------------------------------------------------------------------------------------------ reading

        private static string Attr(XmlReader r, string name)
        {
            var v = r.GetAttribute(name);
            return v == null ? null : Unescape(v);
        }

        private static int ToInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// <summary>
        /// Reads a cache file. Throws <see cref="XmlException"/> when the text is not XML or has no cache element (the
        /// caller treats that as a damaged file). Single entries that lack attributes are counted and skipped.
        /// </summary>
        internal static ParsedCache Parse(string xml)
        {
            var result = new ParsedCache();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            bool sawRoot = false;
            using (var reader = XmlReader.Create(new StringReader(xml ?? ""), settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == RootName)
                    {
                        sawRoot = true;
                        ReadHeader(reader, result.Header);
                    }
                    else if (reader.Name == EntryName && sawRoot)
                    {
                        var e = ReadEntry(reader, result.Header);
                        if (e == null) result.Malformed++;
                        else result.Entries.Add(e);
                    }
                }
            }
            if (!sawRoot) throw new XmlException("not an Arabic translation cache");
            return result;
        }

        private static void ReadHeader(XmlReader r, CacheHeader h)
        {
            h.Format = ToInt(Attr(r, "format"), 0);
            h.Language = Attr(r, "language") ?? "";
            h.GlossaryVersion = Attr(r, "glossary") ?? "";
            h.PipelineVersion = Attr(r, "pipeline") ?? "";
            h.ShapingVersion = Attr(r, "shaping") ?? "";
            h.BidiVersion = Attr(r, "bidi") ?? "";
            h.DisplayVersion = Attr(r, "display") ?? "";
            h.LegacyHandled = Attr(r, "legacyHandled") == "1";
        }

        private static ArabicCacheEntry ReadEntry(XmlReader r, CacheHeader h)
        {
            var key = Attr(r, "k");
            var source = Attr(r, "s");
            var logical = Attr(r, "l");
            var display = Attr(r, "d");
            if (key == null || source == null || logical == null || display == null) return null;

            var e = new ArabicCacheEntry
            {
                Key = key,
                Source = source,
                Logical = logical,
                Display = display,
                GlossaryVersion = Attr(r, "gv") ?? h.GlossaryVersion,
                GlossarySig = Attr(r, "gs") ?? "0",
                PipelineVersion = Attr(r, "pv") ?? h.PipelineVersion,
                ShapingVersion = Attr(r, "sv") ?? h.ShapingVersion,
                BidiVersion = Attr(r, "bv") ?? h.BidiVersion,
                DisplayVersion = Attr(r, "dv") ?? h.DisplayVersion,
                ModId = Attr(r, "m") ?? "",
                ContextKey = Attr(r, "ck") ?? "",
                ContextHint = Attr(r, "ch") ?? "",
                Language = Attr(r, "lg") ?? h.Language
            };

            var f = Attr(r, "f");
            e.Format = f == null ? h.Format : ToInt(f, 0);

            // a missing status must never read as "final"
            var st = Attr(r, "st");
            e.Status = st == "f" ? CacheStatus.Final : (st == "c" ? CacheStatus.Corrected : CacheStatus.Raw);

            switch (Attr(r, "o"))
            {
                case "o": e.Origin = PipelineOrigin.Offline; break;
                case "u": e.Origin = PipelineOrigin.UserEdit; break;
                case "m": e.Origin = PipelineOrigin.Migrated; break;
                case "g": e.Origin = PipelineOrigin.Regenerated; break;
                default: e.Origin = PipelineOrigin.Translator; break;
            }
            return e;
        }
    }
}
