using System;
using System.Collections.Generic;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>Where in a text a glossary term may be enforced.</summary>
    internal enum GlossaryMode
    {
        /// <summary>Anywhere, as a whole word / phrase.</summary>
        Any,
        /// <summary>Only when the term is the complete text (a label); other text around it is not allowed.</summary>
        Whole,
        /// <summary>Only at the start of the text (e.g. "Investigate {0}").</summary>
        Start
    }

    /// <summary>Grammatical form of an approved term that an English surface form asks for.</summary>
    internal enum TermForm
    {
        Singular,
        Plural,
        Feminine,
        FemininePlural
    }

    /// <summary>What the caller knows about the string being translated (used by context dependent terms).</summary>
    internal sealed class GlossaryContext
    {
        /// <summary>Package id of the mod the text comes from (lower case), or empty.</summary>
        internal string ModPackageId = "";
        /// <summary>
        /// Where the text comes from: the Keyed key, or "DefType.fieldName" for a def field (for example
        /// "MemeDef.label"), or empty when unknown.
        /// </summary>
        internal string Key = "";
        /// <summary>Optional grammatical hint of the field: "female" (labelFemale), "plural" (labelPlural), "femaleplural".</summary>
        internal string Hint = "";
        /// <summary>The context is unknown: entries restricted to a mod or a key count as applicable (used for the migration check).</summary>
        internal bool AnyContext;

        internal static readonly GlossaryContext None = new GlossaryContext();

        internal GlossaryContext()
        {
        }

        internal GlossaryContext(string modPackageId, string key, string hint)
        {
            ModPackageId = (modPackageId ?? "").Trim().ToLowerInvariant();
            Key = key ?? "";
            Hint = (hint ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>The hint the name of a def field implies: labelFemale -> female, labelPlural -> plural ...</summary>
        internal static string HintForField(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return "";
            var f = fieldName.ToLowerInvariant();
            if (f.IndexOf("female", StringComparison.Ordinal) >= 0)
                return f.IndexOf("plural", StringComparison.Ordinal) >= 0 ? "femaleplural" : "female";
            if (f.IndexOf("plural", StringComparison.Ordinal) >= 0) return "plural";
            return "";
        }
    }

    /// <summary>One approved term of the glossary (immutable after parsing).</summary>
    internal sealed class GlossaryEntry
    {
        internal string Id;
        /// <summary>Glossary section (Core, Royalty, Ideology, Biotech, Anomaly, Odyssey, Standardized).</summary>
        internal string Section;
        /// <summary>Package ids of the DLC the section belongs to (empty for Standardized: every DLC).</summary>
        internal string[] SectionPackages = new string[0];
        /// <summary>The English text exactly as written in the glossary file (aliases included).</summary>
        internal string[] EnglishAsWritten;
        /// <summary>English singular surfaces (the term and its aliases, without a leading "The").</summary>
        internal string[] English;
        /// <summary>Additional explicit English plural surfaces.</summary>
        internal string[] EnglishPlural = new string[0];
        /// <summary>Approved Arabic term (singular / base form), as written in the glossary.</summary>
        internal string Arabic;
        internal string ArabicPlural;
        internal string ArabicFeminine;
        internal string ArabicFemininePlural;
        /// <summary>Explicit definite form of <see cref="Arabic"/> (needed for words that change when the article is added).</summary>
        internal string ArabicDefinite;
        /// <summary>Explicit indefinite form of <see cref="Arabic"/>.</summary>
        internal string ArabicIndefinite;
        internal GlossaryMode Mode = GlossaryMode.Any;
        /// <summary>The term only matches when written with a capital first letter.</summary>
        internal bool CaseSensitive;
        /// <summary>Adjective: it follows its noun in Arabic, so it is only enforced as a complete label.</summary>
        internal bool Adjective;
        /// <summary>Noun that is also an English verb: not enforced where it is used as a verb.</summary>
        internal bool Guard;
        /// <summary>Package ids the term is restricted to (any of); null = no restriction.</summary>
        internal string[] WhenMods;
        /// <summary>Text the context key must contain; null = no restriction.</summary>
        internal string WhenKey;
        /// <summary>Attributes that were derived by rule and are not part of the supplied glossary.</summary>
        internal string Derived = "";
        internal string Note = "";
        /// <summary>Position in the glossary file (earlier entries win ties).</summary>
        internal int Order;
        /// <summary>1-based line number in the glossary file (diagnostics).</summary>
        internal int Line;
        /// <summary>Hash of everything that influences how the entry is matched and rendered.</summary>
        internal ulong Signature;

        /// <summary>Number of context conditions: more specific entries win over general ones.</summary>
        internal int Specificity
        {
            get
            {
                int n = 0;
                if (WhenMods != null && WhenMods.Length > 0) n++;
                if (WhenKey != null) n++;
                return n;
            }
        }

        /// <summary>The Arabic text of one grammatical form, with the usual fallbacks.</summary>
        internal string FormText(TermForm form)
        {
            switch (form)
            {
                case TermForm.Plural:
                    return ArabicPlural ?? Arabic;
                case TermForm.Feminine:
                    return ArabicFeminine ?? Arabic;
                case TermForm.FemininePlural:
                    return ArabicFemininePlural ?? ArabicPlural ?? ArabicFeminine ?? Arabic;
                default:
                    return Arabic;
            }
        }

        /// <summary>True when the entry has the requested form of its own (no fallback).</summary>
        internal bool HasForm(TermForm form)
        {
            switch (form)
            {
                case TermForm.Plural:
                    return ArabicPlural != null;
                case TermForm.Feminine:
                    return ArabicFeminine != null;
                case TermForm.FemininePlural:
                    return ArabicFemininePlural != null;
                default:
                    return true;
            }
        }
    }

    /// <summary>A glossary term found in a source text.</summary>
    internal sealed class GlossaryMatch
    {
        /// <summary>Start of the matched text in the source (including a leading "the" / "a" / "an").</summary>
        internal int Start;
        /// <summary>Exclusive end of the matched text.</summary>
        internal int End;
        internal GlossaryEntry Entry;
        internal TermForm Form;
        /// <summary>The English text carries a definite article ("the colonist").</summary>
        internal bool Definite;
        /// <summary>The English text carries an indefinite article ("a colonist").</summary>
        internal bool Indefinite;
        /// <summary>The English surface text that was matched (without the article).</summary>
        internal string Surface;
        /// <summary>The match is the only content of the text (a label).</summary>
        internal bool WholeText;
        /// <summary>Token positions inside the scanner (used to detect adjacent terms).</summary>
        internal int FirstToken;
        internal int LastToken;
    }

    /// <summary>All terms found in one source text.</summary>
    internal sealed class GlossaryScan
    {
        internal string Source;
        internal GlossaryContext Context;
        internal List<GlossaryMatch> Matches = new List<GlossaryMatch>();
        /// <summary>The text already contains a {GLn} marker, so it cannot be protected with markers.</summary>
        internal bool MarkerCollision;

        internal bool HasMatches
        {
            get { return Matches.Count > 0; }
        }
    }

    /// <summary>One piece of text that is replaced by a single marker (one term, or two adjacent terms of one compound).</summary>
    internal sealed class GlossaryUnit
    {
        /// <summary>The approved Arabic text that replaces the marker.</summary>
        internal string Arabic;
        /// <summary>The matches the unit covers, in source order.</summary>
        internal List<GlossaryMatch> Parts = new List<GlossaryMatch>();
        internal int Start;
        internal int End;
    }

    /// <summary>Everything needed to send a text to the translator and to put the approved terms back afterwards.</summary>
    internal sealed class GlossaryPlan
    {
        internal string Source;
        internal GlossaryScan Scan;
        /// <summary>The units in source order; the marker of unit n is {GLn}.</summary>
        internal List<GlossaryUnit> Units = new List<GlossaryUnit>();
        /// <summary>The source with every unit replaced by its marker (null when there is nothing to protect).</summary>
        internal string Marked;
        /// <summary>The text is nothing but a glossary term: the translation is known without asking a translator.</summary>
        internal bool Offline;
        /// <summary>The Arabic text of an offline plan.</summary>
        internal string OfflineArabic;
    }

    /// <summary>Problems found while loading the glossary file (shown in the log, checked by the tests).</summary>
    internal sealed class GlossaryIssue
    {
        internal int Line;
        internal string Message;

        public override string ToString()
        {
            return "line " + Line + ": " + Message;
        }
    }

    /// <summary>The parsed glossary file.</summary>
    internal sealed class GlossaryData
    {
        internal string DeclaredVersion = "0";
        /// <summary>Hash of the content of all entries (changes whenever an entry changes).</summary>
        internal string ContentHash = "0000000000000000";
        internal List<GlossaryEntry> Entries = new List<GlossaryEntry>();
        internal List<GlossaryIssue> Issues = new List<GlossaryIssue>();

        /// <summary>Identifier stored in the cache: declared version plus content hash.</summary>
        internal string Version
        {
            get { return "gl-" + DeclaredVersion + "." + ContentHash; }
        }
    }
}
