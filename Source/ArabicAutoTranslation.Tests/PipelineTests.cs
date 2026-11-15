using System;
using System.Collections.Generic;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// The processing between the translator and the cache: glossary terms in, orthography, pronoun variables, placeholder
    /// validation, the display stage and the final validation. The provider is replaced by a fake that records what it was
    /// asked, so the tests see exactly what would be sent (and what would not be sent) to Yandex.
    /// </summary>
    internal static class PipelineTests
    {
        private static readonly GlossaryContext Core = new GlossaryContext("ludeon.rimworld", "Keyed.Test", "");
        private static readonly GlossaryContext IdeologyMeme = new GlossaryContext("ludeon.rimworld.ideology", "MemeDef.label", "");
        private const string Tatweel = "\u0640";

        /// <summary>A provider that records every request and answers with <c>answer</c>.</summary>
        private sealed class Fake
        {
            internal readonly List<string> Calls = new List<string>();
            private readonly Func<string, string> _answer;

            internal Fake(Func<string, string> answer)
            {
                _answer = answer;
            }

            internal string Translate(string chunk)
            {
                Calls.Add(chunk);
                return _answer(chunk);
            }
        }

        private static string Cps(params int[] codePoints)
        {
            var sb = new StringBuilder();
            foreach (int c in codePoints) sb.Append((char)c);
            return sb.ToString();
        }

        private static ArabicPipeline MiniPipeline()
        {
            return Fixtures.PipelineFor(Fixtures.MiniGlossary, null);
        }

        internal static void Run()
        {
            T.Group("pipeline: preparing a source text");
            Preparing();

            T.Group("pipeline: translating with a fake provider");
            OfflineTerms();
            MarkerRoundTrip();
            ProviderFailures();
            LostMarkers();
            Compounds();
            PlaceholderRules();
            LineBreaks();
            PronounVariables();
            Orthography();
            Rejections();
            Segments();
            PieceFallback();
            PieceFallbackOnAnswer();

            T.Group("pipeline: the other entry points");
            UserText();
            LegacyText();
            Regeneration();
            Previews();

            T.Group("pipeline: display stage and options");
            MandatorySamples();
            Options();
            UnshapedLetters();

            T.Group("pipeline: shipped glossary end to end");
            ShippedGlossary();
        }

        // ------------------------------------------------------------------------------------------ preparing

        private static void Preparing()
        {
            var none = new ArabicPipeline(null, null);
            var prep = none.Prepare("Hello", null, true);
            T.True(prep.Plan == null && prep.Terms == 0 && !prep.Offline && !prep.UsesMarkers, "without a glossary nothing is planned");
            T.Equal("Hello", prep.TextToTranslate, "the provider gets the text as it is");
            T.Equal("0", prep.GlossarySig, "no glossary signature");
            T.True(object.ReferenceEquals(GlossaryContext.None, prep.Context), "a missing context is the empty context");
            T.Equal("gl-none", none.GlossaryVersion, "the version without a glossary");
            T.Equal("S1R1I1B1", none.Options.Signature(), "default options when none are given");
            T.Equal(ArabicVersions.DisplayVersion + ";S1R1I1B1", none.DisplayVersion, "the display version carries the options");
            T.Equal("0", none.GlossarySignatureOf("Colonist", Core), "no glossary, no signature");

            var p = MiniPipeline();
            var offline = p.Prepare("Colonist", Core, true);
            T.True(offline.Offline && offline.TextToTranslate == null && !offline.UsesMarkers, "a bare term is known without the provider");
            T.Equal(1, offline.Terms, "one term");
            T.True(offline.GlossarySig.Length == 16 && offline.GlossarySig != "0", "a term gives a 16-digit glossary signature");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", offline.Plan.OfflineArabic, "the approved text");

            var marked = p.Prepare("The colonist is hungry.", Core, true);
            T.True(marked.UsesMarkers && !marked.Offline, "a term inside a sentence is protected by a marker");
            T.Equal("{GL0} is hungry.", marked.TextToTranslate, "the text the provider gets");
            T.Equal(1, marked.Terms, "one term");

            var unmarked = p.Prepare("The colonist is hungry.", Core, false);
            T.True(!unmarked.UsesMarkers && !unmarked.Offline, "markers can be switched off");
            T.Equal("The colonist is hungry.", unmarked.TextToTranslate, "the provider gets the plain text");
            T.Equal(1, unmarked.Terms, "the term is still counted");
            T.Equal(marked.GlossarySig, unmarked.GlossarySig, "the signature does not depend on how the text is protected");

            var plain = p.Prepare("Hello world", Core, true);
            T.True(plain.Plan != null && plain.Terms == 0 && plain.GlossarySig == "0" && !plain.UsesMarkers && !plain.Offline, "a text without terms");
            T.Equal("Hello world", plain.TextToTranslate, "is sent as it is");

            var empty = p.Prepare("", Core, true);
            T.True(empty.Plan == null && empty.TextToTranslate == "" && empty.Terms == 0, "the empty text");
            var nothing = p.Prepare(null, Core, true);
            T.True(nothing.Plan == null && nothing.TextToTranslate == null && nothing.Terms == 0, "null");

            T.Equal(offline.GlossarySig, p.GlossarySignatureOf("Colonist", Core), "the signature of a text");
            T.Equal("0", p.GlossarySignatureOf("", Core), "of the empty text");
            T.Equal("0", p.GlossarySignatureOf(null, Core), "of null");
            T.Equal("0", p.GlossarySignatureOf("Hello world", Core), "of a text without terms");
        }

        // ------------------------------------------------------------------------------------------ translating

        private static void OfflineTerms()
        {
            var p = MiniPipeline();
            var fake = new Fake(c => null);

            var r = p.TranslateText("Colonist", Core, fake.Translate);
            T.True(r.Ok, "an approved term is a complete translation: " + r.Problem);
            T.Equal(0, fake.Calls.Count, "no request was sent");
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631", r.Logical, "the logical text");
            T.True(r.Origin == PipelineOrigin.Offline, "the origin");
            T.Equal(1, r.Terms, "terms");
            T.Equal(p.GlossarySignatureOf("Colonist", Core), r.GlossarySig, "glossary signature");
            T.Equal(Cps(0xFEAE, 0xFEE4, 0xFECC, 0xFE98, 0xFEB4, 0xFEE3), r.Display, "the display-ready text, character by character");
            T.Null(r.Problem, "no problem");
            T.Equal(0, r.Notes.Count, "no adjustment");

            r = p.TranslateText("The colonists", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", "a definite plural term");
            r = p.TranslateText("A colonist.", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0645\u0633\u062A\u0639\u0645\u0631.", "an indefinite term with a full stop");
            T.Equal(0, fake.Calls.Count, "still no request");

            var direct = p.FromOffline(p.Prepare("Colonist", Core, true));
            T.True(direct.Ok && direct.Logical == "\u0645\u0633\u062A\u0639\u0645\u0631" && direct.Origin == PipelineOrigin.Offline, "FromOffline gives the same result");

            // the same English text, another meaning: the context decides
            r = p.TranslateText("Raider", IdeologyMeme, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0627\u0644\u063A\u0632\u0648", "the Ideology meme");
            T.Equal(Cps(0xFEED, 0xFEB0, 0xFED0, 0xFEDF, 0xFE8D), r.Display, "meme, display");
            r = p.TranslateText("Raider", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u063A\u0627\u0632\u064A", "every other Raider");
            T.Equal(Cps(0xFEF1, 0xFEAF, 0xFE8E, 0xFECF), r.Display, "raider, display");
            r = p.TranslateText("Raiders", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u063A\u0632\u0627\u0629", "the plural");
            T.Equal(0, fake.Calls.Count, "no request for any of them");
        }

        private static void MarkerRoundTrip()
        {
            var p = MiniPipeline();
            var fake = new Fake(c => "{GL0} \u062C\u0627\u0626\u0639.");
            const string source = "The colonist is hungry.";

            var r = p.TranslateText(source, Core, fake.Translate);
            T.True(r.Ok, "the translation passes every check: " + r.Problem);
            T.Equal(1, fake.Calls.Count, "one request");
            if (fake.Calls.Count == 1) T.Equal("{GL0} is hungry.", fake.Calls[0], "the provider sees a marker instead of the term");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", r.Logical, "the approved term is put in");
            T.True(r.Origin == PipelineOrigin.Translator, "the origin");
            T.Equal(1, r.Terms, "terms");
            T.Equal(p.GlossarySignatureOf(source, Core), r.GlossarySig, "glossary signature");
            T.Equal(Cps(0x002E, 0xFECA, 0xFE8B, 0xFE8E, 0xFE9F, 0x0020, 0xFEAE, 0xFEE4, 0xFECC, 0xFE98, 0xFEB4, 0xFEE4, 0xFEDF, 0xFE8D), r.Display,
                "the display-ready text, character by character");
            T.True(ArabicShaper.HasPresentationForms(r.Display), "the display text is shaped");
            T.False(ArabicPipeline.HasUnshapedLetters(r.Display), "and complete");

            // spaces inside the marker are tolerated; the provider may move the marker
            r = p.TranslateText(source, Core, new Fake(c => "\u062C\u0627\u0626\u0639 \u0647\u0648 { GL0 }.").Translate);
            T.True(r.Ok && r.Logical == "\u062C\u0627\u0626\u0639 \u0647\u0648 \u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631.", "a moved, spaced marker is restored");

            // two terms
            fake = new Fake(c => "{GL0} \u0648{GL1}");
            r = p.TranslateText("A colonist and a turret", Core, fake.Translate);
            T.True(r.Ok, "two terms: " + r.Problem);
            T.Equal("\u0645\u0633\u062A\u0639\u0645\u0631 \u0648\u0628\u0631\u062C", r.Logical, "both approved terms are in");
            T.Equal(2, r.Terms, "two terms");
            if (fake.Calls.Count == 1) T.Equal("{GL0} and {GL1}", fake.Calls[0], "two markers");

            // the provider's own idea of the term cannot win: the term was never sent
            fake = new Fake(c => "{GL0} \u0627\u0644\u062C\u0646\u062F\u064A \u062C\u0627\u0626\u0639.");
            r = p.TranslateText(source, Core, fake.Translate);
            T.True(r.Ok && r.Logical.StartsWith("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 ", StringComparison.Ordinal), "the approved word stands where the marker was");

            // direct use of Prepare / Finish
            var prep = p.Prepare(source, Core, true);
            T.True(p.Finish(prep, "{GL0} \u062C\u0627\u0626\u0639.").Ok, "Finish with a kept marker");
            var lost = p.Finish(prep, "\u062C\u0627\u0626\u0639.");
            T.True(!lost.Ok && lost.RetryPlain, "Finish with a lost marker asks for a retry without markers");
            T.Contains(lost.Problem, "marker", "and says why");
            var none = p.Finish(prep, "");
            T.True(!none.Ok && !none.RetryPlain, "Finish with an empty answer");
            T.Contains(none.Problem, "no text", "and says why");
            T.False(p.Finish(prep, null).Ok, "Finish with null");
            T.False(p.Finish(prep, "   ").Ok, "Finish with spaces only");
        }

        private static void ProviderFailures()
        {
            var p = MiniPipeline();

            var fake = new Fake(c => null);
            var r = p.TranslateText("The colonist is hungry.", Core, fake.Translate);
            T.False(r.Ok, "a failed request is a failure");
            T.Contains(r.Problem, "did not return", "with its reason");
            T.Equal(2, fake.Calls.Count, "with markers: once more without markers");
            if (fake.Calls.Count == 2)
            {
                T.Equal("{GL0} is hungry.", fake.Calls[0], "first the protected text");
                T.Equal("The colonist is hungry.", fake.Calls[1], "then the plain text");
            }
            T.Null(r.Logical, "nothing to store");
            T.Null(r.Display, "nothing to show");

            fake = new Fake(c => null);
            r = p.TranslateText("Hello world", Core, fake.Translate);
            T.False(r.Ok, "a text without terms fails the same way");
            T.Equal(1, fake.Calls.Count, "but is asked only once");
            T.Equal("0", r.GlossarySig, "signature of a text without terms");

            fake = new Fake(c => "");
            r = p.TranslateText("Hello world", Core, fake.Translate);
            T.False(r.Ok, "an empty answer");
            T.Contains(r.Problem, "no text", "is reported");
            T.Equal(1, fake.Calls.Count, "once");

            fake = new Fake(c => "   ");
            r = p.TranslateText("The colonist is hungry.", Core, fake.Translate);
            T.False(r.Ok, "an answer of spaces");
            T.Equal(1, fake.Calls.Count, "is not asked again (only a lost marker or a failed request is)");

            // the empty source
            fake = new Fake(c => "x");
            T.False(p.TranslateText("", Core, fake.Translate).Ok, "an empty source");
            T.False(p.TranslateText(null, Core, fake.Translate).Ok, "a null source");
            T.Equal(0, fake.Calls.Count, "is never sent");
        }

        private static void LostMarkers()
        {
            var p = MiniPipeline();
            int n = 0;
            var fake = new Fake(c => ++n == 1 ? "\u062C\u0627\u0626\u0639." : "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.");
            var r = p.TranslateText("The colonist is hungry.", Core, fake.Translate);
            T.True(r.Ok, "a lost marker is repaired by asking for the plain text: " + r.Problem);
            T.Equal(2, fake.Calls.Count, "two requests");
            if (fake.Calls.Count == 2)
            {
                T.Equal("{GL0} is hungry.", fake.Calls[0], "the protected text first");
                T.Equal("The colonist is hungry.", fake.Calls[1], "the plain text second");
            }
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", r.Logical, "the plain answer carries the approved term");

            // ... but a plain answer that does not carry the approved term is not accepted
            n = 0;
            fake = new Fake(c => ++n == 1 ? "\u062C\u0627\u0626\u0639." : "\u0627\u0644\u0631\u062C\u0644 \u062C\u0627\u0626\u0639.");
            r = p.TranslateText("The colonist is hungry.", Core, fake.Translate);
            T.False(r.Ok, "the provider's own wording does not replace the glossary");
            T.Contains(r.Problem, "missing", "the problem says the term is missing");
            T.Contains(r.Problem, "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631", "and names it");

            // a duplicated marker is a lost marker too
            n = 0;
            fake = new Fake(c => ++n == 1 ? "{GL0} {GL0} \u062C\u0627\u0626\u0639." : "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.");
            r = p.TranslateText("The colonist is hungry.", Core, fake.Translate);
            T.True(r.Ok && fake.Calls.Count == 2, "a duplicated marker leads to the plain retry");

            // other problems are not retried
            fake = new Fake(c => "{GL0} \u0628\u0646\u0649.");
            r = p.TranslateText("The colonist built {0}.", Core, fake.Translate);
            T.False(r.Ok, "a placeholder that was lost is not repaired by asking again");
            T.Equal(1, fake.Calls.Count, "one request");
        }

        private static void Compounds()
        {
            var p = MiniPipeline();
            var fake = new Fake(c => null);

            var r = p.TranslateText("Slag chunk", Core, fake.Translate);
            T.True(r.Ok, "a compound of two terms: " + r.Problem);
            T.Equal(0, fake.Calls.Count, "a text that is nothing but terms needs no request");
            T.Equal("\u0643\u062A\u0644\u0629 \u062E\u0631\u062F\u0629", r.Logical, "head first, modifier after it");
            T.Equal(Cps(0xFE93, 0xFEA9, 0xFEAE, 0xFEA7, 0x0020, 0xFE94, 0xFEE0, 0xFE98, 0xFEDB), r.Display, "display, character by character");

            r = p.TranslateText("Slag chunks", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0643\u062A\u0644 \u062E\u0631\u062F\u0629", "the plural head");
            r = p.TranslateText("The slag chunk", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0643\u062A\u0644\u0629 \u0627\u0644\u062E\u0631\u062F\u0629", "a definite modifier");
            T.Equal(0, fake.Calls.Count, "still no request");

            fake = new Fake(c => "{GL0} \u062B\u0642\u064A\u0644\u0629.");
            r = p.TranslateText("Slag chunk is heavy.", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0643\u062A\u0644\u0629 \u062E\u0631\u062F\u0629 \u062B\u0642\u064A\u0644\u0629.", "a compound inside a sentence: " + r.Problem);
            if (fake.Calls.Count == 1) T.Equal("{GL0} is heavy.", fake.Calls[0], "is one marker");
        }

        private static void PlaceholderRules()
        {
            var p = MiniPipeline();

            var fake = new Fake(c => "\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!");
            var r = p.TranslateText("Built {0} successfully!", Core, fake.Translate);
            T.True(r.Ok, "a kept placeholder: " + r.Problem);
            T.Equal(1, fake.Calls.Count, "one request");
            if (fake.Calls.Count == 1) T.Equal("Built {0} successfully!", fake.Calls[0], "the placeholder goes to the provider as it is");
            T.Equal("\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!", r.Logical, "logical text");
            T.Equal(Cps(0x21, 0xFEA1, 0xFE8E, 0xFEA0, 0xFEE8, 0xFE91, 0x20, 0x7B, 0x30, 0x7D, 0x20, 0x0621, 0xFE8E, 0xFEE8, 0xFE91, 0x20, 0xFEE2, 0xFE97), r.Display,
                "display text: {0} stays intact");

            string[] bad =
            {
                "\u062A\u0645 \u0627\u0644\u0628\u0646\u0627\u0621 \u0628\u0646\u062C\u0627\u062D!",
                "\u062A\u0645 \u0628\u0646\u0627\u0621 {0} {1} \u0628\u0646\u062C\u0627\u062D!",
                "\u062A\u0645 \u0628\u0646\u0627\u0621 {1} \u0628\u0646\u062C\u0627\u062D!",
                "{0} \u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!",
                "\u062A\u0645 \u0628\u0646\u0627\u0621 {0 \u0628\u0646\u062C\u0627\u062D!"
            };
            string[] why = { "lost placeholder", "invented placeholder", "renamed placeholder", "duplicated placeholder", "broken placeholder" };
            for (int i = 0; i < bad.Length; i++)
            {
                string answer = bad[i];
                r = p.TranslateText("Built {0} successfully!", Core, c => answer);
                T.False(r.Ok, "rejected: " + why[i]);
                if (i < 4) T.Contains(r.Problem, "placeholders", "the problem names the placeholders: " + why[i]);
            }

            // the order may change; the numbers of each stay
            r = p.TranslateText("{0} gave {1} to {2}.", Core, c => "\u0623\u0639\u0637\u0649 {2} {1} \u0625\u0644\u0649 {0}.");
            T.True(r.Ok && r.Logical == "\u0623\u0639\u0637\u0649 {2} {1} \u0625\u0644\u0649 {0}.", "placeholders may change places");

            // rich-text tags
            r = p.TranslateText("Hello <b>world</b>!", Core, c => "\u0645\u0631\u062D\u0628\u0627 <b>\u0639\u0627\u0644\u0645</b>!");
            T.True(r.Ok, "tags survive: " + r.Problem);
            T.Equal("\u0645\u0631\u062D\u0628\u0627 <b>\u0639\u0627\u0644\u0645</b>!", r.Logical, "logical text keeps the tags in place");
            T.Equal(Cps(0x21, 0x3C, 0x62, 0x3E, 0xFEE2, 0xFEDF, 0xFE8E, 0xFECB, 0x3C, 0x2F, 0x62, 0x3E, 0x20, 0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3), r.Display,
                "the display text wraps the visual extent of the span");
            T.False(p.TranslateText("Hello <b>world</b>!", Core, c => "\u0645\u0631\u062D\u0628\u0627 \u0639\u0627\u0644\u0645!").Ok, "lost tags");
            T.False(p.TranslateText("Hello <b>world</b>!", Core, c => "\u0645\u0631\u062D\u0628\u0627 <b>\u0639\u0627\u0644\u0645!").Ok, "a lost closing tag");
            T.False(p.TranslateText("Hello <b>world</b>!", Core, c => "\u0645\u0631\u062D\u0628\u0627 <i>\u0639\u0627\u0644\u0645</i>!").Ok, "changed tags");
        }

        private static void LineBreaks()
        {
            var p = MiniPipeline();

            var fake = new Fake(c => c == "Hello" ? "\u0645\u0631\u062D\u0628\u0627" : "\u0639\u0627\u0644\u0645");
            var r = p.TranslateText("Hello\nWorld", Core, fake.Translate);
            T.True(r.Ok, "two lines: " + r.Problem);
            T.Equal("Hello|World", string.Join("|", fake.Calls.ToArray()), "every line is its own request");
            T.Equal("\u0645\u0631\u062D\u0628\u0627\n\u0639\u0627\u0644\u0645", r.Logical, "the line break is kept");
            T.Equal(Cps(0xFE8E, 0xFE92, 0xFEA3, 0xFEAE, 0xFEE3, 0x0A, 0xFEE2, 0xFEDF, 0xFE8E, 0xFECB), r.Display, "every line is displayed on its own");

            fake = new Fake(c => c == "Hello" ? "\u0645\u0631\u062D\u0628\u0627" : "\u0639\u0627\u0644\u0645");
            r = p.TranslateText("Hello\\nWorld", Core, fake.Translate);
            T.True(r.Ok, "the escape of a line break: " + r.Problem);
            T.Equal("Hello|World", string.Join("|", fake.Calls.ToArray()), "is a separator as well");
            T.Equal("\u0645\u0631\u062D\u0628\u0627\\n\u0639\u0627\u0644\u0645", r.Logical, "and is kept");

            r = p.TranslateText("Hello\r\nWorld", Core, c => c == "Hello" ? "\u0645\u0631\u062D\u0628\u0627" : "\u0639\u0627\u0644\u0645");
            T.True(r.Ok && r.Logical == "\u0645\u0631\u062D\u0628\u0627\r\n\u0639\u0627\u0644\u0645", "a Windows line break is kept");
        }

        private static void PronounVariables()
        {
            var p = MiniPipeline();

            var r = p.TranslateText("{PAWN_nameDef} took {PAWN_possessive} weapon.", Core,
                c => "\u0623\u062E\u0630 {PAWN_nameDef} \u0633\u0644\u0627\u062D {PAWN_possessive}.");
            T.True(r.Ok, "pronoun variable: " + r.Problem);
            T.Equal("\u0623\u062E\u0630 {PAWN_nameDef} \u0633\u0644\u0627\u062D" + Tatweel + "{PAWN_possessive}.", r.Logical, "the variable is attached with a kashida (\u062D connects)");
            T.True(r.Notes.Count == 1 && r.Notes[0].Contains("{PAWN_possessive}"), "the change is reported");
            T.True(r.Display.Contains("{PAWN_possessive}" + Tatweel), "the kashida stays beside the variable in the display text");
            T.True(r.Display.Contains("{PAWN_nameDef}"), "the other variable is untouched");

            // alef maksura: yaa + kashida
            r = p.TranslateText("[PAWN_nameDef] gave it to [PAWN_objective].", Core,
                c => "\u0623\u0639\u0637\u0627\u0647 [PAWN_nameDef] \u0625\u0644\u0649 [PAWN_objective].");
            T.True(r.Ok, "objective variable: " + r.Problem);
            T.Equal("\u0623\u0639\u0637\u0627\u0647 [PAWN_nameDef] \u0625\u0644\u064A" + Tatweel + "[PAWN_objective].", r.Logical, "alef maksura becomes yaa + kashida");
            T.True(r.Display.Contains("[PAWN_objective]" + Tatweel), "and stays in the display text");

            // the angle-bracket grammar tokens
            r = p.TranslateText("He took a weapon <Prohis>.", Core, c => "\u0623\u062E\u0630 \u0633\u0644\u0627\u062D <Prohis>.");
            T.True(r.Ok, "angle bracket variable: " + r.Problem);
            T.Equal("\u0623\u062E\u0630 \u0633\u0644\u0627\u062D" + Tatweel + "<Prohis>.", r.Logical, "attached as well");
            T.True(r.Display.Contains("<Prohis>" + Tatweel), "the token is an atom of the display stage");

            // a letter that does not connect: no kashida, the space goes
            r = p.TranslateText("{PAWN_nameDef} took {PAWN_possessive} hair.", Core, c => "\u0623\u062E\u0630 {PAWN_nameDef} \u0634\u0639\u0631 {PAWN_possessive}.");
            T.True(r.Ok && r.Logical == "\u0623\u062E\u0630 {PAWN_nameDef} \u0634\u0639\u0631{PAWN_possessive}.", "reh does not connect: only the space is removed");

            // taa marbuta: taa + kashida
            r = p.TranslateText("He lost {PAWN_possessive} strength.", Core, c => "\u0641\u0642\u062F \u0642\u0648\u0629 {PAWN_possessive}.");
            T.True(r.Ok && r.Logical == "\u0641\u0642\u062F \u0642\u0648\u062A" + Tatweel + "{PAWN_possessive}.", "taa marbuta becomes taa + kashida");

            // no variable is ever removed or renamed
            T.False(p.TranslateText("{PAWN_nameDef} took {PAWN_possessive} weapon.", Core, c => "\u0623\u062E\u0630 {PAWN_nameDef} \u0633\u0644\u0627\u062D\u0647.").Ok, "a removed variable is rejected");
            T.False(p.TranslateText("{PAWN_nameDef} took {PAWN_possessive} weapon.", Core, c => "\u0623\u062E\u0630 {PAWN_nameDef} \u0633\u0644\u0627\u062D {PAWN_objective}.").Ok, "a renamed variable is rejected");
        }

        private static void Orthography()
        {
            var p = MiniPipeline();
            var r = p.TranslateText("He is also here.", Core, c => "\u0647\u0648 \u0623\u064A\u0636\u064B\u0627 \u0647\u0646\u0627.");
            T.True(r.Ok, "orthography: " + r.Problem);
            T.Equal("\u0647\u0648 \u0623\u064A\u0636\u0627\u064B \u0647\u0646\u0627.", r.Logical, "the fathatan goes behind the alef");
            T.True(r.Notes.Contains("orthography"), "the change is reported");

            r = p.TranslateText("He is also here.", Core, c => "\u0647\u0648 \u0623\u064A\u0636\u0627 \u0647\u0646\u0627.");
            T.True(r.Ok && r.Logical == "\u0647\u0648 \u0623\u064A\u0636\u0627\u064B \u0647\u0646\u0627.", "a bare spelling gets the required one");

            r = p.TranslateText("He is also here.", Core, c => "\u0647\u0648 \u0623\u064A\u0636\u0627\u064B \u0647\u0646\u0627.");
            T.True(r.Ok && r.Notes.Count == 0, "correct spelling is not reported");
        }

        private static void Rejections()
        {
            var p = MiniPipeline();

            // text that is already display text must never be processed a second time
            string display = p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627");
            var r = p.TranslateText("Hello", Core, c => display);
            T.False(r.Ok, "an answer in presentation forms is refused");
            T.Contains(r.Problem, "already shaped", "with the reason");

            // leftovers of the protection
            r = p.TranslateText("Hello", Core, c => "\u0645\u0631\u062D\u0628\u0627 __PH1__");
            T.False(r.Ok, "a leftover protection token");
            T.Contains(r.Problem, "protection marker", "is named");
            r = p.TranslateText("Hello", Core, c => "\u0645\u0631\u062D\u0628\u0627 {GL3}");
            T.False(r.Ok, "a glossary marker in a text that never had one");
        }

        private static void Segments()
        {
            string s = new string('a', 90) + ". " + new string('b', 90) + ". " + new string('c', 90) + ".";
            var calls = new List<string>();
            string joined = ArabicPipeline.TranslateSegments(s, c => { calls.Add(c); return "<" + calls.Count + ">"; });
            T.Equal("<1> <2>", joined, "a long text is sent in pieces and joined with the original separators");
            T.Equal(2, calls.Count, "two requests");
            T.Equal(s, string.Join(" ", calls.ToArray()), "the pieces together are the original text");

            var fake = new Fake(c => "x");
            T.Equal("123", ArabicPipeline.TranslateSegments("123", fake.Translate), "a text without letters is copied");
            T.Equal(0, fake.Calls.Count, "and never sent");
            T.Equal(" {0} ", ArabicPipeline.TranslateSegments(" {0} ", fake.Translate), "a text with only a placeholder is copied");
            T.Equal(0, fake.Calls.Count, "never sent");

            T.True(ArabicPipeline.TranslateSegments(s, c => null) == null, "a failed piece fails the text");
            T.True(ArabicPipeline.TranslateSegments("Hello\nWorld", c => c == "World" ? null : "x") == null, "a failed second line fails the text");
            T.Equal("x\ny", ArabicPipeline.TranslateSegments("Hello\nWorld", c => c == "World" ? "y" : "x"), "lines are joined with their break");
        }

        private static void PieceFallback()
        {
            var p = MiniPipeline();

            // a provider that cannot carry a placeholder or a marker through a sentence
            Func<string, string> garbles = c => c.IndexOf('{') >= 0 ? null : "T(" + c + ")";

            var fake = new Fake(garbles);
            var r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, () => true);
            T.True(r.Ok, "the text around the placeholder is translated piece by piece: " + r.Problem);
            T.Equal("T(Cost:) {0} T(per day)", r.Logical, "the placeholder stays where it was");
            T.Equal(3, fake.Calls.Count, "the whole text first, then one request for each piece");
            if (fake.Calls.Count == 3)
            {
                T.Equal("Cost: {0} per day", fake.Calls[0], "the whole text");
                T.Equal("Cost:", fake.Calls[1], "the text before the placeholder");
                T.Equal("per day", fake.Calls[2], "the text after it");
            }

            // only when the provider says that it lost the placeholders (a network error is not a reason)
            fake = new Fake(garbles);
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate);
            T.False(r.Ok, "without the evidence the text fails as before");
            T.Equal(1, fake.Calls.Count, "after one request");
            fake = new Fake(garbles);
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, () => false);
            T.False(r.Ok, "a provider that did not lose them fails as before");
            T.Equal(1, fake.Calls.Count, "after one request, too");

            // a glossary marker: the plain text came back without the approved term, the pieces are the last resort
            fake = new Fake(c => c.IndexOf("{GL", StringComparison.Ordinal) >= 0 ? null : "T(" + c + ")");
            r = p.TranslateText("The colonist is hungry.", Core, fake.Translate, () => true);
            T.True(r.Ok, "a text with a marker: " + r.Problem);
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 T(is hungry.)", r.Logical, "the approved term is put in where the marker was");
            T.Equal(3, fake.Calls.Count, "the protected text, the plain text, the piece after the marker");
            if (fake.Calls.Count == 3)
            {
                T.Equal("{GL0} is hungry.", fake.Calls[0], "first the protected text");
                T.Equal("The colonist is hungry.", fake.Calls[1], "then the plain text");
                T.Equal("is hungry.", fake.Calls[2], "then the text after the marker");
            }

            // a failed piece fails the text
            fake = new Fake(c => c.IndexOf('{') >= 0 || c == "per day" ? null : "T(" + c + ")");
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, () => true);
            T.False(r.Ok, "a failed piece");
            T.Contains(r.Problem, "did not return", "is reported like any failed request");

            // the pieces themselves: tags and placeholders are copied, one letter is not worth a request
            fake = new Fake(c => "T(" + c + ")");
            T.Equal("<b>T(Hi)</b> {0}!", ArabicPipeline.TranslateAroundPlaceholders("<b>Hi</b> {0}!", fake.Translate), "tags and placeholders are copied");
            T.Equal(1, fake.Calls.Count, "only the words are sent");
            fake = new Fake(c => "T(" + c + ")");
            T.Equal("{0}s", ArabicPipeline.TranslateAroundPlaceholders("{0}s", fake.Translate), "the s after a placeholder is left alone");
            T.Equal(0, fake.Calls.Count, "and never sent");
            T.True(ArabicPipeline.TranslateAroundPlaceholders("Hi {0}", c => null) == null, "a failed piece gives no text");
        }

        /// <summary>
        /// The same fallback for an answer that did come back but does not agree with the placeholders of the source: the
        /// provider did not say that it lost anything, it dropped, changed or invented a placeholder in what it answered.
        /// Only used when the caller asks for it (the game does, the other entry points do not).
        /// </summary>
        private static void PieceFallbackOnAnswer()
        {
            var p = MiniPipeline();

            // a provider that drops the placeholder and leaves a double space behind
            Func<string, string> forgets = c => "T(" + c.Replace("{0}", "") + ")";

            var fake = new Fake(forgets);
            var r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, null, true);
            T.True(r.Ok, "an answer without the placeholder is not used, the pieces are: " + r.Problem);
            T.Equal("T(Cost:) {0} T(per day)", r.Logical, "the placeholder stays where it was");
            T.Equal(3, fake.Calls.Count, "the whole text first, then one request for each piece");
            T.True(r.FallbackFrom != null, "the rejected answer is kept for the log");
            if (r.FallbackFrom != null)
            {
                T.True(r.FallbackFrom.PlaceholderProblem, "it was rejected because of its placeholders");
                T.Equal("placeholders: missing {0}", r.FallbackFrom.Problem, "the log can say which one was lost");
                T.Equal("T(Cost:  per day)", r.FallbackFrom.Answer, "and what the provider answered");
            }

            // not asked for: the text fails as before, but it says why
            fake = new Fake(forgets);
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate);
            T.False(r.Ok, "without the option the text fails as before");
            T.True(r.PlaceholderProblem, "because of its placeholders");
            T.True(r.FallbackFrom == null, "without a second try");
            T.Equal(1, fake.Calls.Count, "after one request");

            // an invented placeholder is no better than a lost one
            fake = new Fake(c => c.IndexOf('{') >= 0 ? "T(" + c + ") {5}" : "T(" + c + ")");
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, null, true);
            T.True(r.Ok, "an invented placeholder: " + r.Problem);
            T.Equal("T(Cost:) {0} T(per day)", r.Logical, "only the real one is in the result");
            T.Equal("placeholders: unexpected {5}", r.FallbackFrom == null ? null : r.FallbackFrom.Problem, "the invented one is named");

            // tags are placeholders, too
            fake = new Fake(c => "T(" + c.Replace("<b>", "").Replace("</b>", "") + ")");
            r = p.TranslateText("Hello <b>world</b>!", Core, fake.Translate, null, true);
            T.True(r.Ok, "lost tags: " + r.Problem);
            T.Equal("T(Hello) <b>T(world)</b>!", r.Logical, "the tags stay around their word");
            T.Equal(3, fake.Calls.Count, "the whole text, then the two words");

            // a glossary marker next to a placeholder: the term is put in after the pieces were translated
            fake = new Fake(forgets);
            r = p.TranslateText("The colonist built {0}.", Core, fake.Translate, null, true);
            T.True(r.Ok, "a marker and a placeholder: " + r.Problem);
            T.Contains(r.Logical, "T(built) {0}.", "the word between the term and the placeholder was translated");
            T.False(r.Logical != null && r.Logical.IndexOf("{GL", StringComparison.Ordinal) >= 0, "no marker is left in the text");
            T.Equal(2, fake.Calls.Count, "the protected text, then the word between the marker and the placeholder");
            if (fake.Calls.Count == 2)
            {
                T.Equal("{GL0} built {0}.", fake.Calls[0], "the protected text first");
                T.Equal("built", fake.Calls[1], "then the words between them");
            }

            // the pieces fail, too: the text fails with the problem of the whole answer
            fake = new Fake(c => c.IndexOf('{') >= 0 ? "T(" + c.Replace("{0}", "") + ")" : null);
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, null, true);
            T.False(r.Ok, "a failed piece fails the text");
            T.Contains(r.Problem, "placeholders", "with the problem of the whole text");
            T.True(r.FallbackFrom == null, "nothing replaced it");
            T.Equal(2, fake.Calls.Count, "the whole text, then the first piece");

            // a provider that keeps its placeholders is asked once
            fake = new Fake(c => "T(" + c + ")");
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, null, true);
            T.True(r.Ok, "a good answer: " + r.Problem);
            T.Equal("T(Cost: {0} per day)", r.Logical, "is used as it is");
            T.Equal(1, fake.Calls.Count, "after one request");
            T.True(r.FallbackFrom == null, "without a second try");

            // a failure that has nothing to do with placeholders is no reason to send the pieces
            fake = new Fake(c => "   ");
            r = p.TranslateText("Cost: {0} per day", Core, fake.Translate, null, true);
            T.False(r.Ok, "an empty answer");
            T.False(r.PlaceholderProblem, "is not a placeholder problem");
            T.Equal(1, fake.Calls.Count, "and is not asked again");

            // a multiplier sign stays with its number: the translator only gets the words (it reads a lone x as a word)
            var seen = new List<string>();
            var around = ArabicPipeline.TranslateAroundPlaceholders("Spirit root: x{0}", delegate (string c)
            {
                seen.Add(c);
                return "T(" + c + ")";
            });
            T.Equal("T(Spirit root:) x{0}", around, "the sign is copied with its placeholder");
            T.Equal("Spirit root:", string.Join("|", seen), "and the translator only gets the words");
        }

        // ------------------------------------------------------------------------------------------ other entry points

        private static void UserText()
        {
            var p = MiniPipeline();
            const string source = "The colonist is hungry.";

            var r = p.FromUserText(source, "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", Core, false);
            T.True(r.Ok, "typed text: " + r.Problem);
            T.True(r.Origin == PipelineOrigin.UserEdit, "origin");
            T.Equal(p.GlossarySignatureOf(source, Core), r.GlossarySig, "the signature of the source's terms");
            T.True(r.GlossarySig != "0", "which exist");
            T.Equal("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", r.Logical, "logical text");
            T.Equal(p.RenderDisplay("\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639."), r.Display, "display text");

            r = p.FromUserText(source, "\u0627\u0644\u0631\u062C\u0644 \u062C\u0627\u0626\u0639.", Core, false);
            T.True(r.Ok, "the user's own wording is accepted even where it differs from the glossary");

            r = p.FromUserText("He is also here.", "\u0647\u0648 \u0623\u064A\u0636\u064B\u0627 \u0647\u0646\u0627.", Core, false);
            T.True(r.Ok && r.Logical == "\u0647\u0648 \u0623\u064A\u0636\u0627\u064B \u0647\u0646\u0627.", "the orthography rules still apply");
            r = p.FromUserText("{PAWN_nameDef} took {PAWN_possessive} hair.", "\u0623\u062E\u0630 {PAWN_nameDef} \u0643\u062A\u0627\u0628 {PAWN_possessive}.", Core, false);
            T.True(r.Ok && r.Logical == "\u0623\u062E\u0630 {PAWN_nameDef} \u0643\u062A\u0627\u0628" + Tatweel + "{PAWN_possessive}.", "and so do the pronoun rules");

            T.False(p.FromUserText("Hello {0}", "\u0645\u0631\u062D\u0628\u0627", Core, false).Ok, "typed text may not lose a placeholder");
            T.True(p.FromUserText("Hello {0}", "\u0645\u0631\u062D\u0628\u0627 {0}", Core, false).Ok, "but may keep it");
            T.True(p.FromUserText("Hello {0}", "\u0645\u0631\u062D\u0628\u0627", Core, true).Ok, "unless the source is only approximate");
            T.False(p.FromUserText("Hello {0}", "\u0645\u0631\u062D\u0628\u0627 {1}", Core, true).Ok, "but it may never invent one");

            T.False(p.FromUserText(source, "", Core, false).Ok, "empty text");
            T.False(p.FromUserText(source, "   ", Core, false).Ok, "spaces");
            T.False(p.FromUserText(source, null, Core, false).Ok, "null");

            string shaped = p.FromUserText("Colonist", "\u0645\u0633\u062A\u0639\u0645\u0631", Core, false).Display;
            r = p.FromUserText("Colonist", shaped, Core, false);
            T.False(r.Ok, "display text offered as logical text is refused");
            T.Contains(r.Problem, "already shaped", "with the reason");
        }

        private static void LegacyText()
        {
            var p = MiniPipeline();

            var r = p.FromLegacy("Hello world", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", Core);
            T.True(r.Ok, "an old entry without terms is adopted: " + r.Problem);
            T.True(r.Origin == PipelineOrigin.Migrated, "origin");
            T.Equal("0", r.GlossarySig, "no glossary signature");
            T.Equal("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", r.Logical, "the old text is the logical text");
            T.Equal(p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"), r.Display, "the display text is built from it");

            r = p.FromLegacy("The colonist is hungry.", "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", Core);
            T.False(r.Ok, "an old entry with a glossary term has to be translated again");
            T.Contains(r.Problem, "glossary terms", "with the reason");
            T.False(p.FromLegacy("Slag chunk", "\u0642\u0637\u0639\u0629 \u062E\u0631\u062F\u0629", Core).Ok, "a compound as well");

            T.True(p.FromLegacy("Hello {0}extra", "\u0645\u0631\u062D\u0628\u0627", Core).Ok, "the old key may carry additional text after the placeholders");
            T.False(p.FromLegacy("Hello", "\u0645\u0631\u062D\u0628\u0627 {0}", Core).Ok, "but the old text may not invent a placeholder");
            T.False(p.FromLegacy("Hello", "", Core).Ok, "an empty old text");
            T.False(p.FromLegacy("Hello", p.RenderDisplay("\u0645\u0631\u062D\u0628\u0627"), Core).Ok, "an old text that is already shaped");
            T.True(p.FromLegacy("Hello world", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", null).Ok, "a missing context");

            // a term that only applies in a certain def type still counts: the old entry does not say where it came from
            var q = Fixtures.PipelineFor("@glossary-version 1\n[Core | ludeon.rimworld]\nBloom | \u0627\u0632\u062F\u0647\u0627\u0631 | mode=whole | key=MemeDef\n", null);
            T.Equal(0, q.Prepare("Bloom", Core, true).Terms, "the term does not apply to a Keyed string");
            T.False(q.FromLegacy("Bloom", "\u0632\u0647\u0631\u0629", Core).Ok, "but an old entry of that text is not adopted blindly");

            var none = new ArabicPipeline(null, null);
            T.True(none.FromLegacy("The colonist is hungry.", "\u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631 \u062C\u0627\u0626\u0639.", null).Ok, "without a glossary every old entry qualifies");
        }

        private static void Regeneration()
        {
            var p = MiniPipeline();
            const string source = "The colonist is hungry.";
            var first = p.TranslateText(source, Core, c => "{GL0} \u062C\u0627\u0626\u0639.");

            var regen = p.Regenerate(source, first.Logical, first.GlossarySig, false);
            T.True(regen.Ok, "regenerated: " + regen.Problem);
            T.True(regen.Origin == PipelineOrigin.Regenerated, "origin");
            T.Equal(first.Logical, regen.Logical, "the logical text is the one stored");
            T.Equal(first.Display, regen.Display, "the same options give the same display text");
            T.Equal(first.GlossarySig, regen.GlossarySig, "the signature is carried over");
            T.Equal("0", p.Regenerate("x", "\u0645\u0631\u062D\u0628\u0627", null, false).GlossarySig, "a missing signature becomes 0");

            // other options: same logical text, other display text
            var reorderOnly = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Shape = false });
            var other = reorderOnly.Regenerate(source, first.Logical, first.GlossarySig, false);
            T.True(other.Ok && other.Logical == first.Logical, "another mode keeps the logical text");
            T.True(other.Display != first.Display, "and changes the display text");
            T.False(ArabicShaper.HasPresentationForms(other.Display), "reorder only does not shape");

            T.True(p.Regenerate("Hello {0}extra", "\u0645\u0631\u062D\u0628\u0627", "0", true).Ok, "an approximate source");
            T.False(p.Regenerate("Hello {0}", "\u0645\u0631\u062D\u0628\u0627", "0", false).Ok, "an exact source needs the placeholder");
            T.False(p.Regenerate(source, first.Display, first.GlossarySig, false).Ok, "display text is never regenerated");
            T.False(p.Regenerate(source, "", first.GlossarySig, false).Ok, "empty text");
        }

        private static void Previews()
        {
            var p = MiniPipeline();
            T.True(p.Preview(null) == null, "null");
            T.Equal("", p.Preview(""), "the empty text");
            T.Equal("   ", p.Preview("   "), "spaces");

            string shaped = p.RenderDisplay("\u0645\u0633\u062A\u0639\u0645\u0631");
            T.Equal(shaped, p.Preview(shaped), "display text is returned as it is");
            T.Equal(Cps(0xFEAE, 0xFEE4, 0xFECC, 0xFE98, 0xFEB4, 0xFEE3), p.Preview("\u0645\u0633\u062A\u0639\u0645\u0631"), "a word");
            T.Equal(p.RenderDisplay("\u0647\u0648 \u0623\u064A\u0636\u0627\u064B \u0647\u0646\u0627."), p.Preview("\u0647\u0648 \u0623\u064A\u0636\u064B\u0627 \u0647\u0646\u0627."), "the orthography rules apply");
            T.Equal(p.RenderDisplay("\u0643\u062A\u0627\u0628" + Tatweel + "{PAWN_possessive}"), p.Preview("\u0643\u062A\u0627\u0628 {PAWN_possessive}"), "the pronoun rules apply");

            // an unfinished text can still be previewed (no validation)
            T.Equal(p.RenderDisplay("\u062A\u0645 \u0628\u0646\u0627\u0621 {0"), p.Preview("\u062A\u0645 \u0628\u0646\u0627\u0621 {0"), "a broken placeholder");
        }

        // ------------------------------------------------------------------------------------------ display stage

        private static void MandatorySamples()
        {
            var p = MiniPipeline();
            if (GoldenVectors.Display.Length < GoldenVectors.MandatoryCount)
            {
                T.Fail("the mandatory samples are missing from the vectors");
                return;
            }

            for (int i = 0; i < GoldenVectors.MandatoryCount; i++)
            {
                string logical = GoldenVectors.Display[i].Logical;
                string source = string.Join(" ", Placeholders.Of(logical).ToArray());
                string d = "sample " + (i + 1) + " [" + T.Show(logical) + "]";

                var r = p.Regenerate(source, logical, "0", false);
                if (!T.True(r.Ok, "the sample passes every check: " + d + " " + r.Problem)) continue;

                T.Equal(logical, r.Logical, "the logical text is untouched: " + d);
                T.Equal(0, r.Notes.Count, "no adjustment was needed: " + d);
                T.Equal(DisplayProcessor.Process(logical, p.Options), r.Display, "the display text is the display stage's output: " + d);
                T.True(ArabicShaper.HasPresentationForms(r.Display), "shaped: " + d);
                T.False(ArabicPipeline.HasUnshapedLetters(r.Display), "completely shaped: " + d);

                // the same text offered again as logical text, and the editor preview, never process it twice
                T.False(p.FromUserText(source, r.Display, GlossaryContext.None, false).Ok, "display text is refused as input: " + d);
                T.Equal(r.Display, p.Preview(r.Display), "display text is not previewed again: " + d);
            }
        }

        private static void Options()
        {
            var full = Fixtures.PipelineFor(Fixtures.MiniGlossary, null);
            var shapeOnly = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Reorder = false });
            var reorderOnly = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Shape = false });
            var logicalMode = Fixtures.PipelineFor(Fixtures.MiniGlossary, new DisplayOptions { Shape = false, Reorder = false });
            const string word = "\u0645\u0633\u062A\u0639\u0645\u0631";

            T.Equal(Cps(0xFEAE, 0xFEE4, 0xFECC, 0xFE98, 0xFEB4, 0xFEE3), full.RenderDisplay(word), "mode 0: shaped and reordered");
            T.Equal(Cps(0xFEE3, 0xFEB4, 0xFE98, 0xFECC, 0xFEE4, 0xFEAE), shapeOnly.RenderDisplay(word), "mode 1: shaped, typing order");
            T.Equal(Cps(0x0631, 0x0645, 0x0639, 0x062A, 0x0633, 0x0645), reorderOnly.RenderDisplay(word), "reordered only");
            T.Equal(word, logicalMode.RenderDisplay(word), "mode 2: the logical text as it is");

            T.True(full.RenderDisplay(null) == null && full.RenderDisplay("") == "", "null and the empty text");
            T.Equal(DisplayProcessor.Process("\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!", full.Options), full.RenderDisplay("\u062A\u0645 \u0628\u0646\u0627\u0621 {0} \u0628\u0646\u062C\u0627\u062D!"), "RenderDisplay is the display stage");

            // every mode runs the whole pipeline and stores the text of its own mode
            var pipelines = new[] { full, shapeOnly, reorderOnly, logicalMode };
            foreach (var pl in pipelines)
            {
                string mode = pl.Options.Signature();
                var r = pl.TranslateText("Colonist", Core, c => null);
                T.True(r.Ok, "mode " + mode + ": " + r.Problem);
                T.Equal(word, r.Logical, "the logical text does not depend on the mode: " + mode);
                T.Equal(pl.RenderDisplay(word), r.Display, "the display text follows the mode: " + mode);
            }

            // the identity of the display stage tells the cache when entries have to be rebuilt
            T.Equal(ArabicVersions.DisplayVersion + ";S1R1I1B1", full.DisplayVersion, "mode 0");
            T.Equal(ArabicVersions.DisplayVersion + ";S1R0I1B1", shapeOnly.DisplayVersion, "mode 1");
            T.Equal(ArabicVersions.DisplayVersion + ";S0R1I1B1", reorderOnly.DisplayVersion, "reorder only");
            T.Equal(ArabicVersions.DisplayVersion + ";S0R0I1B1", logicalMode.DisplayVersion, "mode 2");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pl in pipelines) seen.Add(pl.DisplayVersion);
            T.Equal(4, seen.Count, "four different display versions");

            // text without Arabic letters is not touched in any mode
            foreach (var pl in pipelines)
            {
                T.Equal("Hello {0}", pl.RenderDisplay("Hello {0}"), "Latin text is returned as it is: " + pl.Options.Signature());
            }
        }

        private static void UnshapedLetters()
        {
            var p = MiniPipeline();
            T.True(ArabicPipeline.HasUnshapedLetters("\u0645\u0633\u062A\u0639\u0645\u0631"), "plain Arabic has unshaped letters");
            T.False(ArabicPipeline.HasUnshapedLetters(p.RenderDisplay("\u0645\u0633\u062A\u0639\u0645\u0631")), "shaped text has none");
            T.False(ArabicPipeline.HasUnshapedLetters("Hello 123 {0}"), "Latin text");
            T.False(ArabicPipeline.HasUnshapedLetters(""), "the empty text");
            T.False(ArabicPipeline.HasUnshapedLetters("\u0621"), "hamza stands alone");
            T.True(ArabicPipeline.HasUnshapedLetters("\u0627"), "a bare alef");
            T.False(ArabicPipeline.HasUnshapedLetters(Tatweel), "tatweel");
            T.False(ArabicPipeline.HasUnshapedLetters("\u0660\u0661\u0662"), "Arabic-Indic digits");
        }

        // ------------------------------------------------------------------------------------------ shipped glossary

        private static void ShippedGlossary()
        {
            var p = Fixtures.ShippedPipeline(null);
            var fake = new Fake(c => null);

            var r = p.TranslateText("Raider", IdeologyMeme, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0627\u0644\u063A\u0632\u0648", "the Ideology meme Raider: " + r.Problem);
            r = p.TranslateText("Raider", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u063A\u0627\u0632\u064A", "Raider elsewhere");
            r = p.TranslateText("Raiders", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u063A\u0632\u0627\u0629", "Raiders");
            r = p.TranslateText("Colonists", Core, fake.Translate);
            T.True(r.Ok && r.Logical == "\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646", "Colonists");
            T.Equal(0, fake.Calls.Count, "none of them needed a request");

            fake = new Fake(c => "{GL0} \u062A\u0647\u0627\u062C\u0645 {GL1}.");
            r = p.TranslateText("The raiders attack the colonists.", Core, fake.Translate);
            T.True(r.Ok, "a sentence with two terms: " + r.Problem);
            if (fake.Calls.Count == 1) T.Equal("{GL0} attack {GL1}.", fake.Calls[0], "what the provider sees");
            T.Equal("\u0627\u0644\u063A\u0632\u0627\u0629 \u062A\u0647\u0627\u062C\u0645 \u0627\u0644\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646.", r.Logical, "approved terms with their articles");
            T.Equal(2, r.Terms, "two terms");
            T.Equal(Cps(0x2E, 0xFEE6, 0xFEF3, 0xFEAE, 0xFEE4, 0xFECC, 0xFE98, 0xFEB4, 0xFEE4, 0xFEDF, 0xFE8D, 0x20, 0xFEE2, 0xFE9F, 0xFE8E, 0xFEEC, 0xFE97, 0x20,
                0xFE93, 0xFE8D, 0xFEB0, 0xFED0, 0xFEDF, 0xFE8D), r.Display, "display, character by character");

            // a noun that is also a verb is not enforced as the noun
            fake = new Fake(c => "\u062D\u062F\u062F \u0627\u0644\u0639\u062F\u0648");
            r = p.TranslateText("Spot the enemy", Core, fake.Translate);
            T.True(r.Ok && r.Terms == 0, "the verb Spot is left to the translator: " + r.Problem);
            if (fake.Calls.Count == 1) T.Equal("Spot the enemy", fake.Calls[0], "and goes to the provider unchanged");

            // the glossary signature follows the glossary: another glossary, another signature
            var other = Fixtures.PipelineFor(Fixtures.ShippedGlossaryText.Replace("Colonist | \u0645\u0633\u062A\u0639\u0645\u0631 |", "Colonist | \u0631\u0627\u0626\u062F |"), null);
            T.True(p.GlossarySignatureOf("The colonist is hungry.", Core) != other.GlossarySignatureOf("The colonist is hungry.", Core),
                "an edited term changes the signature of the texts that contain it");
            T.Equal(p.GlossarySignatureOf("Hello world", Core), other.GlossarySignatureOf("Hello world", Core), "and not of the others");
            T.True(p.GlossaryVersion != other.GlossaryVersion, "the glossary version changes");
        }
    }
}
