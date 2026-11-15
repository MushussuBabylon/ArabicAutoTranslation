using System;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// How far a stored translation has been processed. Only <see cref="Final"/> entries are ever returned as a cache hit;
    /// the other two exist so that a raw or half-processed text read from any source can never be mistaken for a display text.
    /// </summary>
    internal enum CacheStatus : byte
    {
        /// <summary>Provider output (or an old unprocessed cache entry): not valid for display.</summary>
        Raw = 0,
        /// <summary>Glossary enforced but not yet shaped / reordered: not valid for display.</summary>
        Corrected = 1,
        /// <summary>Glossary, grammar, pronoun rules, BiDi and shaping applied and validated: the display string.</summary>
        Final = 2
    }

    /// <summary>One entry of the persistent Arabic translation cache (treated as immutable once it is stored).</summary>
    internal sealed class ArabicCacheEntry
    {
        /// <summary>The cache key the rest of the mod uses: mod package id, ':' and the normalized original text.</summary>
        internal string Key;
        /// <summary>The original English text the translation belongs to.</summary>
        internal string Source;
        /// <summary>Target language of the translation ("ar").</summary>
        internal string Language = ArabicVersions.Language;
        /// <summary>The finished text in typing order. Input of the display stage when it has to be redone; never displayed.</summary>
        internal string Logical;
        /// <summary>The FINAL display-ready string: shown exactly as it is stored.</summary>
        internal string Display;
        internal CacheStatus Status = CacheStatus.Final;
        internal PipelineOrigin Origin;

        // identifiers of the processing the entry went through
        internal string GlossaryVersion = "";
        /// <summary>Signature of the glossary entries that applied to the source text when the entry was made.</summary>
        internal string GlossarySig = "0";
        internal string PipelineVersion = "";
        internal string ShapingVersion = "";
        internal string BidiVersion = "";
        internal string DisplayVersion = "";
        internal int Format = ArabicVersions.CacheFormat;

        // the context the glossary was applied in (needed to check the entry against a changed glossary)
        internal string ModId = "";
        internal string ContextKey = "";
        internal string ContextHint = "";

        internal ArabicCacheEntry Clone()
        {
            return (ArabicCacheEntry)MemberwiseClone();
        }

        internal GlossaryContext ToGlossaryContext()
        {
            return new GlossaryContext(ModId, ContextKey, ContextHint);
        }

        /// <summary>Fills in the identifiers of the processing that is current for <paramref name="pipeline"/>.</summary>
        internal void StampCurrent(ArabicPipeline pipeline)
        {
            GlossaryVersion = pipeline.GlossaryVersion;
            PipelineVersion = ArabicVersions.PipelineVersion;
            ShapingVersion = ArabicVersions.ShapingVersion;
            BidiVersion = ArabicVersions.BidiVersion;
            DisplayVersion = pipeline.DisplayVersion;
            Format = ArabicVersions.CacheFormat;
        }

        /// <summary>True when shaping, BiDi, display options or the pipeline differ from the current ones (the display form is outdated).</summary>
        internal bool DisplayOutdated(ArabicPipeline pipeline)
        {
            return !string.Equals(PipelineVersion, ArabicVersions.PipelineVersion, StringComparison.Ordinal) ||
                   !string.Equals(ShapingVersion, ArabicVersions.ShapingVersion, StringComparison.Ordinal) ||
                   !string.Equals(BidiVersion, ArabicVersions.BidiVersion, StringComparison.Ordinal) ||
                   !string.Equals(DisplayVersion, pipeline.DisplayVersion, StringComparison.Ordinal);
        }

        /// <summary>Creates the entry for a successful pipeline result.</summary>
        internal static ArabicCacheEntry From(string key, string source, GlossaryContext ctx, PipelineResult result, ArabicPipeline pipeline)
        {
            var e = new ArabicCacheEntry
            {
                Key = key,
                Source = source,
                Logical = result.Logical,
                Display = result.Display,
                Status = CacheStatus.Final,
                Origin = result.Origin,
                GlossarySig = result.GlossarySig ?? "0"
            };
            if (ctx != null)
            {
                e.ModId = ctx.ModPackageId ?? "";
                e.ContextKey = ctx.Key ?? "";
                e.ContextHint = ctx.Hint ?? "";
            }
            e.StampCurrent(pipeline);
            return e;
        }
    }

    /// <summary>What happened to the entries of the cache when it was loaded and checked.</summary>
    internal sealed class CacheReport
    {
        /// <summary>Entries read from the file.</summary>
        internal int Loaded;
        /// <summary>Entries that were valid as they were (nothing was processed).</summary>
        internal int Valid;
        /// <summary>Entries whose display form was rebuilt from the stored logical text (shaping / BiDi / option change).</summary>
        internal int Regenerated;
        /// <summary>Entries whose glossary terms changed: dropped, they are translated again when needed.</summary>
        internal int StaleGlossary;
        /// <summary>Entries that were not fully processed, had another format or language, or were damaged: dropped.</summary>
        internal int Rejected;
        /// <summary>Entries of the old (unprocessed) cache that passed the pipeline and were adopted.</summary>
        internal int LegacyAdopted;
        /// <summary>Entries of the old cache that must be translated again (glossary terms) or failed validation.</summary>
        internal int LegacyDropped;
        /// <summary>Entries whose glossary signature was still current: only their version stamp was renewed.</summary>
        internal int Restamped;
        /// <summary>Machine translations of texts with a multiplier sign ("x{0}", "4x") or a tier numeral ("II") made before it was protected: dropped, translated again when needed.</summary>
        internal int StaleMultiplier;

        internal bool Changed
        {
            get
            {
                return Regenerated > 0 || StaleGlossary > 0 || Rejected > 0 || LegacyAdopted > 0 || LegacyDropped > 0 ||
                       Restamped > 0 || StaleMultiplier > 0;
            }
        }

        /// <summary>Adds the counts of another report (the cache of a mod is loaded file by file).</summary>
        internal void Add(CacheReport other)
        {
            if (other == null) return;
            Loaded += other.Loaded;
            Valid += other.Valid;
            Regenerated += other.Regenerated;
            StaleGlossary += other.StaleGlossary;
            Rejected += other.Rejected;
            LegacyAdopted += other.LegacyAdopted;
            LegacyDropped += other.LegacyDropped;
            Restamped += other.Restamped;
            StaleMultiplier += other.StaleMultiplier;
        }

        public override string ToString()
        {
            return "loaded " + Loaded + ", valid " + Valid + ", re-rendered " + Regenerated + ", glossary-stale " + StaleGlossary +
                   ", rejected " + Rejected + ", old entries adopted " + LegacyAdopted + " / dropped " + LegacyDropped +
                   (StaleMultiplier > 0 ? ", multiplier texts to translate again " + StaleMultiplier : "");
        }
    }
}
