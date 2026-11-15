using System;
using System.Collections.Generic;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Brings the Arabic cache up to date when it is loaded: entries made with another glossary, another shaping / BiDi /
    /// display revision or other display options are regenerated or dropped, entries that are not fully processed are
    /// rejected, and the old unprocessed cache is migrated. An entry that is current is not touched at all - its display
    /// string is used as it is, so nothing is ever processed twice.
    /// </summary>
    internal static class ArabicCacheMaintenance
    {
        /// <summary>Mod id the old cache gave to entries without one.</summary>
        internal const string LegacyModId = "unknown.mod";

        /// <summary>Reads the cache file into the store and checks every entry.</summary>
        internal static CacheReport LoadAndCheck(ArabicCacheStore store, ArabicPipeline pipeline)
        {
            var report = new CacheReport();
            var parsed = store.ReadFile();
            if (parsed == null) return report;

            store.LegacyHandled = parsed.Header.LegacyHandled;
            report.Loaded = parsed.Entries.Count;
            report.Rejected += parsed.Malformed;
            foreach (var entry in parsed.Entries)
            {
                var kept = Check(entry, pipeline, report);
                if (kept != null) store.PutLoaded(kept);
            }
            if (report.Changed || store.LoadedFromBackup) store.MarkDirty();
            return report;
        }

        /// <summary>
        /// Checks one entry. Returns the entry to keep (the same object when nothing had to change, a new one when it was
        /// restamped or its display form was regenerated), or null when it has to be dropped.
        /// </summary>
        internal static ArabicCacheEntry Check(ArabicCacheEntry e, ArabicPipeline pipeline, CacheReport report)
        {
            // only a fully processed entry of this format and language can ever be shown
            if (e.Format != ArabicVersions.CacheFormat ||
                !string.Equals(e.Language, ArabicVersions.Language, StringComparison.OrdinalIgnoreCase) ||
                e.Status != CacheStatus.Final ||
                string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Source) ||
                string.IsNullOrEmpty(e.Logical) || string.IsNullOrEmpty(e.Display))
            {
                report.Rejected++;
                return null;
            }

            var current = e;

            // a multiplier sign ("x{0}", "4x") or a tier numeral ("II"): before the revision that protects it the translator was
            // given the bare sign, which it read as a word (the Arabic letter for s, the word "secondly"). The text is translated
            // again; a text the user typed is kept, and so is an entry made by the glossary alone
            if (IsStaleMultiplier(e))
            {
                report.StaleMultiplier++;
                return null;
            }

            // terminology: the entry is stale when a glossary term that applies to its text changed (or a new one applies)
            if (e.Origin != PipelineOrigin.UserEdit &&
                !string.Equals(e.GlossaryVersion, pipeline.GlossaryVersion, StringComparison.Ordinal))
            {
                var signature = pipeline.GlossarySignatureOf(e.Source, e.ToGlossaryContext());
                if (!string.Equals(signature, e.GlossarySig, StringComparison.Ordinal))
                {
                    report.StaleGlossary++;
                    return null;
                }
                current = e.Clone();
                current.GlossaryVersion = pipeline.GlossaryVersion;
                report.Restamped++;
            }

            // shaping / BiDi / display revision or options changed: rebuild the display form from the logical text. The same
            // happens to a final entry whose display text is not display-ready (a hand-edited or damaged file): the pipeline
            // itself never stores such a text, so it must not be shown either
            bool unprocessed = pipeline.Options.Shape && DisplayProcessor.HasStrongRtl(current.Display) &&
                               ArabicPipeline.HasUnshapedLetters(current.Display);
            if (current.DisplayOutdated(pipeline) || unprocessed)
            {
                bool approximate = current.Origin == PipelineOrigin.Migrated || current.Origin == PipelineOrigin.UserEdit;
                var res = pipeline.Regenerate(current.Source, current.Logical, current.GlossarySig, approximate);
                if (!res.Ok)
                {
                    report.Rejected++;
                    return null;
                }
                var rebuilt = current.Clone();
                rebuilt.Logical = res.Logical;
                rebuilt.Display = res.Display;
                rebuilt.StampCurrent(pipeline);
                report.Regenerated++;
                return rebuilt;
            }

            report.Valid++;
            return current;
        }

        /// <summary>
        /// True for a machine translation of a text with a multiplier sign ("x{0}", "4x") or a tier numeral at the end ("Dragon
        /// scales II") that was made before it was kept from the translator. A text the user typed, and an entry the glossary
        /// made alone, are never stale.
        /// </summary>
        internal static bool IsStaleMultiplier(ArabicCacheEntry e)
        {
            if (e.Origin == PipelineOrigin.UserEdit || e.Origin == PipelineOrigin.Offline) return false;
            int revision = ArabicVersions.PipelineRevision(e.PipelineVersion);
            return (revision < ArabicVersions.MultiplierFixRevision && Placeholders.HasMultiplier(e.Source)) ||
                   (revision < ArabicVersions.TierFixRevision && Placeholders.HasRomanTier(e.Source));
        }

        /// <summary>True when the text contains a letter of the Arabic script.</summary>
        internal static bool ContainsArabicLetter(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u0621' && c <= '\u064A') return true;
                if (c >= '\u066E' && c <= '\u06D3') return true;
                if (c >= '\u0750' && c <= '\u077F') return true;
            }
            return false;
        }

        /// <summary>True when most of the (non-empty) texts contain Arabic letters: the cache was made for Arabic.</summary>
        internal static bool MostlyArabic(IEnumerable<string> values)
        {
            int total = 0, arabic = 0;
            foreach (var v in values)
            {
                if (string.IsNullOrEmpty(v)) continue;
                total++;
                if (ContainsArabicLetter(v)) arabic++;
            }
            return total > 0 && arabic * 2 >= total;
        }

        /// <summary>
        /// Migrates the old cache (key -> raw provider text, no glossary, no shaping). Entries whose text contains no
        /// glossary term are processed and adopted; entries that contain terms are dropped so that they are translated
        /// again with the glossary enforced; anything that already has a processed entry is left alone.
        /// </summary>
        internal static CacheReport MigrateLegacy(ArabicCacheStore store, ArabicPipeline pipeline, IEnumerable<KeyValuePair<string, string>> legacy)
        {
            return MigrateLegacy(store.ContainsKey, store.Put, pipeline, legacy);
        }

        /// <summary>The same for the cache of all mods.</summary>
        internal static CacheReport MigrateLegacy(ArabicModCache cache, ArabicPipeline pipeline, IEnumerable<KeyValuePair<string, string>> legacy)
        {
            return MigrateLegacy(cache.ContainsKey, cache.Put, pipeline, legacy);
        }

        private static CacheReport MigrateLegacy(Func<string, bool> contains, Func<ArabicCacheEntry, bool> put,
            ArabicPipeline pipeline, IEnumerable<KeyValuePair<string, string>> legacy)
        {
            var report = new CacheReport();
            foreach (var pair in legacy)
            {
                var key = pair.Key;
                var value = pair.Value;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
                if (contains(key)) continue;

                // a text that is not Arabic at all (another language, or left untranslated) is not adopted
                if (!ContainsArabicLetter(value))
                {
                    report.LegacyDropped++;
                    continue;
                }

                int colon = key.IndexOf(':');
                string modId = colon > 0 ? key.Substring(0, colon) : "";
                string guess = colon >= 0 ? key.Substring(colon + 1) : key;
                if (string.Equals(modId, LegacyModId, StringComparison.Ordinal)) modId = "";

                var ctx = new GlossaryContext(modId, "", "");
                var res = pipeline.FromLegacy(guess, value, ctx);
                if (!res.Ok)
                {
                    report.LegacyDropped++;
                    continue;
                }
                var entry = ArabicCacheEntry.From(key, guess, ctx, res, pipeline);
                if (put(entry)) report.LegacyAdopted++;
                else report.LegacyDropped++;
            }
            return report;
        }
    }
}
