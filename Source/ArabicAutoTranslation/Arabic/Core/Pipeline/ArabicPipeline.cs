using System;
using System.Collections.Generic;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>How a cached translation came into being.</summary>
    internal enum PipelineOrigin : byte
    {
        /// <summary>Translated by the provider; the glossary terms were put in by the pipeline.</summary>
        Translator = 0,
        /// <summary>The whole text is an approved glossary term: no request was needed.</summary>
        Offline = 1,
        /// <summary>Typed by the user (editor) or merged from a shared cache; never invalidated by a glossary change.</summary>
        UserEdit = 2,
        /// <summary>Adopted from the old unprocessed cache after it passed the pipeline.</summary>
        Migrated = 3,
        /// <summary>Display form rebuilt from the stored logical text after a shaping / BiDi / option change.</summary>
        Regenerated = 4
    }

    /// <summary>A source text that went through the glossary scan and is ready for the translator.</summary>
    internal sealed class PreparedSource
    {
        internal string Source;
        internal GlossaryContext Context;
        /// <summary>The glossary plan (null when there is no glossary).</summary>
        internal GlossaryPlan Plan;
        /// <summary>Signature of the glossary entries that matched this text ("0" when none did).</summary>
        internal string GlossarySig = "0";
        /// <summary>Number of glossary terms found in the text.</summary>
        internal int Terms;
        /// <summary>The whole text is an approved term: the translation is known without the provider.</summary>
        internal bool Offline;
        /// <summary>The terms are replaced by {GLn} markers in <see cref="TextToTranslate"/>.</summary>
        internal bool UsesMarkers;
        /// <summary>The text the provider has to translate (null for an offline text).</summary>
        internal string TextToTranslate;
    }

    /// <summary>The outcome of processing one text.</summary>
    internal sealed class PipelineResult
    {
        internal bool Ok;
        /// <summary>Why the text was rejected (null when <see cref="Ok"/>).</summary>
        internal string Problem;
        /// <summary>The finished text in typing order: glossary enforced, grammar and pronoun rules applied, validated.</summary>
        internal string Logical;
        /// <summary>The FINAL display-ready string: this is what is cached and shown.</summary>
        internal string Display;
        internal string GlossarySig = "0";
        internal PipelineOrigin Origin;
        internal int Terms;
        /// <summary>A glossary marker did not survive the translator: asking again without markers may help.</summary>
        internal bool RetryPlain;
        /// <summary>The placeholders (or a protection marker) of the translation did not agree with those of the source.</summary>
        internal bool PlaceholderProblem;
        /// <summary>What the translator answered for the text (glossary markers still in it); null when it gave no answer.</summary>
        internal string Answer;
        /// <summary>
        /// Set when the text as a whole did not come back with its placeholders and was translated piece by piece
        /// instead: the rejected result of the whole text (its Problem and Answer say what went wrong).
        /// </summary>
        internal PipelineResult FallbackFrom;
        /// <summary>Adjustments made by the pipeline (orthography, pronoun attachments ...), for the log and the tests.</summary>
        internal List<string> Notes = new List<string>();

        internal static PipelineResult Failure(string problem)
        {
            return new PipelineResult { Ok = false, Problem = problem };
        }
    }

    /// <summary>
    /// The Arabic processing pipeline, independent of the game: glossary enforcement, grammar, pronoun variables,
    /// placeholder validation, BiDi + shaping, final validation. The caller supplies the translator as a function.
    ///
    /// Order (the deviation from "BiDi then shaping" is deliberate: the renderer is a left-to-right-only text engine, so
    /// the letters are shaped in typing order, where their neighbours are known, and the shaped line is then reordered):
    ///   1  protect placeholders / glossary terms           Prepare
    ///   2  translate (the existing provider)                the caller
    ///   3  glossary terms back in                           Finish
    ///   4  orthography, pronoun-variable attachment         FinishLogical
    ///   5  placeholder + pronoun validation                 FinishLogical
    ///   6  shaping + UAX #9 reordering                      RenderDisplay
    ///   7  validation of the display string                 CheckDisplay
    /// </summary>
    internal sealed class ArabicPipeline
    {
        internal GlossaryIndex Glossary { get; private set; }
        internal DisplayOptions Options { get; private set; }

        internal ArabicPipeline(GlossaryIndex glossary, DisplayOptions options)
        {
            Glossary = glossary;
            Options = options ?? DisplayOptions.Default();
        }

        /// <summary>Version of the glossary data ("gl-none" without a glossary).</summary>
        internal string GlossaryVersion
        {
            get { return Glossary == null ? "gl-none" : Glossary.Data.Version; }
        }

        /// <summary>Identity of the display stage: its revision plus the option combination.</summary>
        internal string DisplayVersion
        {
            get { return ArabicVersions.DisplayVersion + ";" + Options.Signature(); }
        }

        /// <summary>Signature of the glossary entries that apply to a text (changes when one of them changes).</summary>
        internal string GlossarySignatureOf(string source, GlossaryContext ctx)
        {
            if (Glossary == null || string.IsNullOrEmpty(source)) return "0";
            return GlossaryIndex.SignatureOf(Glossary.Scan(source, ctx));
        }

        // ---------------------------------------------------------------------------------------- translation

        /// <summary>Scans the text for glossary terms and decides what the provider has to translate.</summary>
        internal PreparedSource Prepare(string source, GlossaryContext ctx, bool allowMarkers)
        {
            var prep = new PreparedSource { Source = source, Context = ctx ?? GlossaryContext.None };
            prep.TextToTranslate = source;
            if (Glossary == null || string.IsNullOrEmpty(source)) return prep;

            var plan = Glossary.Plan(source, prep.Context, allowMarkers);
            prep.Plan = plan;
            prep.GlossarySig = GlossaryIndex.SignatureOf(plan.Scan);
            prep.Terms = plan.Scan.Matches.Count;
            if (plan.Offline)
            {
                prep.Offline = true;
                prep.TextToTranslate = null;
            }
            else if (plan.Marked != null)
            {
                prep.UsesMarkers = true;
                prep.TextToTranslate = plan.Marked;
            }
            return prep;
        }

        /// <summary>The result for a text that is nothing but an approved term.</summary>
        internal PipelineResult FromOffline(PreparedSource prep)
        {
            var res = FinishLogical(prep.Source, prep.Plan.OfflineArabic, Placeholders.Mode.Exact);
            res.Origin = PipelineOrigin.Offline;
            res.GlossarySig = prep.GlossarySig;
            res.Terms = prep.Terms;
            return res;
        }

        /// <summary>Processes the provider's answer for <see cref="PreparedSource.TextToTranslate"/>.</summary>
        internal PipelineResult Finish(PreparedSource prep, string translation)
        {
            if (string.IsNullOrWhiteSpace(translation))
            {
                return Tag(PipelineResult.Failure("the translator returned no text"), prep);
            }

            string text = translation;
            if (prep.UsesMarkers)
            {
                string restored;
                if (!GlossaryIndex.Restore(prep.Plan, text, out restored))
                {
                    var lost = Tag(PipelineResult.Failure("a glossary marker was lost by the translator"), prep);
                    lost.RetryPlain = true;
                    return lost;
                }
                text = restored;
            }
            else if (prep.Terms > 0 && prep.Plan != null)
            {
                // the terms could not be put in by marker: the approved wording has to be there already
                foreach (var m in prep.Plan.Scan.Matches)
                {
                    var term = GlossaryIndex.TermArabic(m);
                    if (!ArabicText.ContainsTerm(text, term))
                    {
                        return Tag(PipelineResult.Failure("the approved term \"" + term + "\" for \"" + m.Surface + "\" is missing in the translation"), prep);
                    }
                }
            }

            var res = FinishLogical(prep.Source, text, Placeholders.Mode.Exact);
            res.Origin = PipelineOrigin.Translator;
            res.GlossarySig = prep.GlossarySig;
            res.Terms = prep.Terms;
            res.Answer = translation;
            return res;
        }

        private static PipelineResult Tag(PipelineResult res, PreparedSource prep)
        {
            res.GlossarySig = prep.GlossarySig;
            res.Terms = prep.Terms;
            return res;
        }

        /// <summary>
        /// Translates and processes a text. <paramref name="translateChunk"/> sends one piece of text to the provider and
        /// returns its translation, or null when the provider failed. <paramref name="placeholdersLost"/> (optional) tells
        /// whether the provider's last answer failed because it garbled the placeholders it was given.
        /// With <paramref name="retryAroundPlaceholders"/> an answer that came back but does not agree with the placeholders
        /// of the source (one lost, changed or invented) is not used either: the text is translated piece by piece.
        /// </summary>
        internal PipelineResult TranslateText(string source, GlossaryContext ctx, Func<string, string> translateChunk,
                                              Func<bool> placeholdersLost = null, bool retryAroundPlaceholders = false)
        {
            if (string.IsNullOrEmpty(source)) return PipelineResult.Failure("empty text");

            var prep = Prepare(source, ctx, true);
            if (prep.Offline) return FromOffline(prep);

            string raw = TranslateSegments(prep.TextToTranslate, translateChunk);
            // asked now, before the next request is sent
            bool lost = raw == null && placeholdersLost != null && placeholdersLost();
            var res = raw == null
                ? Tag(PipelineResult.Failure("the translator did not return a translation"), prep)
                : Finish(prep, raw);

            if (!res.Ok && prep.UsesMarkers && (raw == null || res.RetryPlain))
            {
                // a marker did not survive: ask once more for the plain text; the terms are then checked afterwards
                var plain = Prepare(source, ctx, false);
                raw = TranslateSegments(plain.TextToTranslate, translateChunk);
                res = raw == null
                    ? Tag(PipelineResult.Failure("the translator did not return a translation"), plain)
                    : Finish(plain, raw);
            }

            if (!res.Ok && (lost || (retryAroundPlaceholders && res.PlaceholderProblem)))
            {
                // The provider garbles the placeholders and glossary markers of a sentence (or answers without them) and
                // the plain text did not carry the approved terms. The placeholders and markers stay where they are and
                // only the text between them is translated, piece by piece, instead of leaving the whole sentence in English.
                var failed = res;
                var pieces = TranslateAroundPlaceholders(prep.TextToTranslate, translateChunk);
                if (pieces != null)
                {
                    res = Finish(prep, pieces);
                    res.FallbackFrom = failed;
                }
            }
            return res;
        }

        /// <summary>
        /// Sends the text to the provider piece by piece (see <see cref="TextSegmenter"/>) and joins the answers with the
        /// original separators. Null when one piece failed.
        /// </summary>
        internal static string TranslateSegments(string text, Func<string, string> translateChunk)
        {
            var segments = TextSegmenter.Split(text, TextSegmenter.DefaultLimit);
            var sb = new StringBuilder(text.Length + 16);
            foreach (var seg in segments)
            {
                if (!seg.Translate)
                {
                    sb.Append(seg.Text);
                    continue;
                }
                var t = translateChunk(seg.Text);
                if (t == null) return null;
                sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>
        /// For a text whose placeholders a translator cannot carry through a sentence: the placeholders and tags stay
        /// where they are and only the text between them is sent, piece by piece. Null when one piece failed.
        /// </summary>
        internal static string TranslateAroundPlaceholders(string text, Func<string, string> translateChunk)
        {
            var sb = new StringBuilder(text.Length + 16);
            foreach (var part in TextSegmenter.SplitAtPlaceholders(text))
            {
                if (!part.Translate || IsLoneLatinLetter(part.Text))
                {
                    sb.Append(part.Text);
                    continue;
                }
                // translators trim the white space around a text: it is put back
                string lead = part.Text.Substring(0, part.Text.Length - part.Text.TrimStart().Length);
                string trail = part.Text.Substring(part.Text.TrimEnd().Length);
                var t = TranslateSegments(part.Text.Trim(), translateChunk);
                if (t == null) return null;
                sb.Append(lead).Append(t).Append(trail);
            }
            return sb.ToString();
        }

        /// <summary>One Latin letter and nothing else to read (the "s" of "{0}s"): a translator could only guess at it.</summary>
        private static bool IsLoneLatinLetter(string s)
        {
            int letters = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetter(s[i])) continue;
                if (s[i] >= 0x250) return false;
                letters++;
            }
            return letters <= 1;
        }

        // ---------------------------------------------------------------------------------------- other entries

        /// <summary>
        /// Processes a translation typed by the user (logical text, never display text). When
        /// <paramref name="sourceIsApproximate"/> is set the source is only known from the cache key (it may carry the
        /// additional key), so the text may omit placeholders but must not invent any.
        /// </summary>
        internal PipelineResult FromUserText(string source, string userText, GlossaryContext ctx, bool sourceIsApproximate)
        {
            var res = FinishLogical(source, userText,
                sourceIsApproximate ? Placeholders.Mode.ActualWithinExpected : Placeholders.Mode.Exact);
            res.Origin = PipelineOrigin.UserEdit;
            res.GlossarySig = GlossarySignatureOf(source, ctx);
            return res;
        }

        /// <summary>
        /// Adopts an entry of the old cache (raw provider text without glossary enforcement). Only texts without
        /// glossary terms qualify: for those there is nothing to enforce; everything else must be translated again.
        /// <paramref name="sourceGuess"/> is the cache key without its mod prefix (the original text, possibly followed by
        /// the additional key), so the placeholder check is one-sided.
        /// </summary>
        internal PipelineResult FromLegacy(string sourceGuess, string rawArabic, GlossaryContext ctx)
        {
            if (Glossary != null)
            {
                // the real context (def type, field) is unknown: a term that applies in any context counts
                var anyContext = new GlossaryContext(ctx == null ? "" : ctx.ModPackageId, "", "") { AnyContext = true };
                var scan = Glossary.Scan(sourceGuess, anyContext);
                if (scan.HasMatches)
                {
                    return PipelineResult.Failure("the text contains glossary terms and has to be translated again");
                }
            }
            var res = FinishLogical(sourceGuess, rawArabic, Placeholders.Mode.ActualWithinExpected);
            res.Origin = PipelineOrigin.Migrated;
            res.GlossarySig = "0";
            return res;
        }

        /// <summary>Rebuilds the display form from a stored logical text (after a shaping / BiDi / option change).</summary>
        internal PipelineResult Regenerate(string source, string logical, string glossarySig, bool sourceIsApproximate)
        {
            var res = FinishLogical(source, logical,
                sourceIsApproximate ? Placeholders.Mode.ActualWithinExpected : Placeholders.Mode.Exact);
            res.Origin = PipelineOrigin.Regenerated;
            res.GlossarySig = glossarySig ?? "0";
            return res;
        }

        // ---------------------------------------------------------------------------------------- stages 4 - 7

        /// <summary>Orthography, pronoun variables, placeholder validation, display, final validation.</summary>
        private PipelineResult FinishLogical(string source, string logical, Placeholders.Mode placeholderMode)
        {
            var res = new PipelineResult();
            if (string.IsNullOrWhiteSpace(logical)) return Reject(res, "empty text");
            if (ArabicShaper.HasPresentationForms(logical))
            {
                return Reject(res, "the text is already shaped (presentation forms); it must be plain Arabic text");
            }

            string text = ArabicText.NormalizeOrthography(logical);
            if (!string.Equals(text, logical, StringComparison.Ordinal)) res.Notes.Add("orthography");
            text = PronounRules.Apply(text, res.Notes);

            string problem = PronounRules.Validate(text);
            if (problem != null) return Reject(res, "pronoun variables: " + problem);

            problem = Placeholders.Check(source, text, placeholderMode);
            if (problem != null)
            {
                res.PlaceholderProblem = true;
                return Reject(res, "placeholders: " + problem);
            }

            if (GlossaryIndex.HasMarker(text) || text.IndexOf("__PH", StringComparison.Ordinal) >= 0)
            {
                res.PlaceholderProblem = true;
                return Reject(res, "a protection marker was left in the text");
            }

            string display = RenderDisplay(text);
            problem = CheckDisplay(text, display);
            if (problem != null) return Reject(res, "display string: " + problem);

            res.Ok = true;
            res.Logical = text;
            res.Display = display;
            return res;
        }

        private static PipelineResult Reject(PipelineResult res, string problem)
        {
            res.Ok = false;
            res.Problem = problem;
            return res;
        }

        /// <summary>
        /// How a text typed in the editor will look in the game: the display stage applied to the text after the same
        /// orthography and pronoun adjustments the real pipeline makes. No validation (the text may still be unfinished).
        /// </summary>
        internal string Preview(string logical)
        {
            if (string.IsNullOrWhiteSpace(logical) || ArabicShaper.HasPresentationForms(logical)) return logical;
            string text = ArabicText.NormalizeOrthography(logical);
            text = PronounRules.Apply(text, new List<string>());
            return RenderDisplay(text);
        }

        /// <summary>Logical text -> display-ready text (shaping and UAX #9 reordering according to the options).</summary>
        internal string RenderDisplay(string logical)
        {
            if (string.IsNullOrEmpty(logical)) return logical;
            if (!Options.Shape && !Options.Reorder) return logical;
            return DisplayProcessor.Process(logical, Options);
        }

        /// <summary>Null when the display string is sound, otherwise the problem.</summary>
        private string CheckDisplay(string logical, string display)
        {
            string problem = Placeholders.Check(logical, display, Placeholders.Mode.TagsAsSet);
            if (problem != null) return problem;

            for (int i = 0; i < display.Length; i++)
            {
                char c = display[i];
                if ((c >= '\u2066' && c <= '\u2069') || (c >= '\u202A' && c <= '\u202E'))
                {
                    return "a bidirectional control character was left in the text";
                }
            }

            if (Options.Shape && DisplayProcessor.HasStrongRtl(display) && HasUnshapedLetters(display))
            {
                return "unshaped Arabic letters remain";
            }
            return null;
        }

        /// <summary>True when a letter that has contextual forms still stands in its base form.</summary>
        internal static bool HasUnshapedLetters(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '\u0621' || c > '\u06D3') continue;
                var jt = UnicodeData.JoinTypeOf(c);
                if ((jt == JoinType.D || jt == JoinType.R) && UnicodeData.FormsOf(c) != null) return true;
            }
            return false;
        }
    }
}
