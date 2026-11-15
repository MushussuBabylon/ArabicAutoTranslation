namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Identifiers of the processing stages whose output is stored in the persistent Arabic cache. When one changes
    /// every cached entry that was produced with the old value is regenerated from its stored source text.
    /// </summary>
    internal static class ArabicVersions
    {
        /// <summary>Layout of the entries of the Arabic cache file.</summary>
        internal const int CacheFormat = 1;

        /// <summary>Contextual shaping: algorithm revision + hash of the Unicode data it is driven by.</summary>
        internal const string ShapingVersion = "shape-1." + ArabicTables.DataSignature;

        /// <summary>Bidirectional algorithm (UAX #9): implementation revision + hash of the Unicode data.</summary>
        internal const string BidiVersion = "uax9-1." + ArabicTables.DataSignature;

        /// <summary>Tokenizer, isolates, rich-text handling and line handling of <see cref="DisplayProcessor"/>.</summary>
        internal const string DisplayVersion = "display-1";

        /// <summary>
        /// Everything between the translator's answer and the display stage: glossary marker restoring, orthography,
        /// pronoun-variable rules, placeholder validation. Bump it when one of those rules changes.
        /// </summary>
        internal const string PipelineVersion = "pipe-4";

        /// <summary>
        /// The revision number of a pipeline version ("pipe-3" is 3; anything that does not look like that is 0), so that the
        /// cache can tell an entry made before a known fix from one made after it.
        /// </summary>
        internal static int PipelineRevision(string version)
        {
            const string prefix = "pipe-";
            if (version == null || !version.StartsWith(prefix, System.StringComparison.Ordinal)) return 0;
            int n;
            return int.TryParse(version.Substring(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>
        /// The first revision that keeps a multiplier sign ("x{0}", "4x") from the translator. A machine translation of such a
        /// text that was made before it may hold an Arabic letter in place of the sign, so the cache translates it again.
        /// </summary>
        internal const int MultiplierFixRevision = 3;

        /// <summary>
        /// The first revision that keeps a tier numeral at the end of a text ("Dragon scales II") from the translator, and that
        /// takes the space out in front of the Arabic comma and semicolon. A machine translation of such a text made before it may
        /// hold an ordinal word in place of the numeral, so the cache translates it again.
        /// </summary>
        internal const int TierFixRevision = 4;

        /// <summary>Language code of the translations the Arabic cache holds.</summary>
        internal const string Language = "ar";
    }
}
