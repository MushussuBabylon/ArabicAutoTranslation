using System;
using System.Collections.Generic;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// RULE 6: pronoun variables ({PAWN_possessive}, [PAWN_objective], &lt;Prohis&gt; ...) are attached to the Arabic word before them;
    /// connecting letters get a kashida, non-connecting letters nothing, taa marbuta becomes taa + kashida, alef maksura yaa + kashida.
    /// </summary>
    internal static class PronounTests
    {
        // the 20 letters of the rule that connect to the next letter
        private const string RuleLetters = "\u0628\u062A\u062B\u062C\u062D\u062E\u0633\u0634\u0635\u0636\u0637\u0638\u0639\u063A\u0641\u0642\u0643\u0644\u0645\u0646";
        // letters that do not connect forward (and the hamza, which never connects)
        private const string NoKashidaLetters = "\u0627\u062F\u0630\u0631\u0632\u0648\u0623\u0625\u0622\u0624\u0621";

        internal static void Run()
        {
            T.Group("pronoun variables: which variables");
            Attachable();

            T.Group("pronoun variables: kashida rules");
            KashidaMatrix();
            Cases();
            Validation();
        }

        private static void Attachable()
        {
            string[] yes =
            {
                "{PAWN_possessive}", "{PAWN_objective}", "{PAWN_reflexive}", "[PAWN_possessive]", "[PAWN_objective]", "[PAWN_reflexive]",
                "{ANIMAL_possessive}", "<Prohis>", "<Proher>", "<Prohim>", "<Prohimself>", "<Proherself>", "<Prohers>", "<Protheir>",
                "<Protheirs>", "<Prothem>", "<Prothemself>", "<Prothemselves>", "<Proits>", "<Proitself>", "<prohis>"
            };
            foreach (var v in yes) T.True(PronounRules.IsAttachable(v), v + " is a suffix pronoun");

            string[] no =
            {
                "{PAWN_nameDef}", "{0}", "{1}", "[PAWN_pronoun]", "[PAWN_nameFull]", "<b>", "</b>", "<color=#ff0000>", "<Pro>", "<Prohe>", "<Proshe>",
                "{}", "", "\\n", "x"
            };
            foreach (var v in no) T.False(PronounRules.IsAttachable(v), v + " is not a suffix pronoun");
            T.False(PronounRules.IsAttachable(null), "null is not a suffix pronoun");
        }

        private static string Fix(string text)
        {
            return PronounRules.Apply(text, new List<string>());
        }

        private static void KashidaMatrix()
        {
            const string variable = "{PAWN_possessive}";
            string tatweel = "\u0640";

            // letters that connect: kashida between the word and the variable, and the space goes
            foreach (char c in RuleLetters)
            {
                string word = "\u0643" + c;
                string actual = Fix(word + " " + variable);
                T.Equal(word + tatweel + variable, actual, "kashida after the letter U+" + ((int)c).ToString("X4"));
                T.Equal(actual, Fix(actual), "idempotent after U+" + ((int)c).ToString("X4"));
            }

            // the other letters of the Arabic alphabet that connect: heh, yeh, yeh with hamza
            foreach (char c in "\u0647\u064A\u0626")
            {
                string word = "\u0643" + c;
                T.Equal(word + tatweel + variable, Fix(word + " " + variable), "kashida after the dual-joining letter U+" + ((int)c).ToString("X4"));
            }

            // letters that do not connect forward: nothing is inserted, the space still goes
            foreach (char c in NoKashidaLetters)
            {
                string word = "\u0643" + c;
                string actual = Fix(word + " " + variable);
                T.Equal(word + variable, actual, "no kashida after the letter U+" + ((int)c).ToString("X4"));
                T.Equal(actual, Fix(actual), "idempotent after U+" + ((int)c).ToString("X4"));
            }

            // taa marbuta becomes an open taa + kashida, alef maksura becomes yaa + kashida
            T.Equal("\u0642\u0648\u062A" + tatweel + variable, Fix("\u0642\u0648\u0629 " + variable), "taa marbuta becomes taa + kashida");
            T.Equal("\u0639\u0644\u064A" + tatweel + variable, Fix("\u0639\u0644\u0649 " + variable), "alef maksura becomes yaa + kashida");
            T.Equal("\u0642\u0648\u062A" + tatweel + variable, Fix("\u0642\u0648\u0629" + variable), "taa marbuta without a space");
            T.Equal("\u0639\u0644\u064A" + tatweel + variable, Fix("\u0639\u0644\u0649" + variable), "alef maksura without a space");

            // all three bracket styles
            T.Equal("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", Fix("\u0643\u062A\u0627\u0628 {PAWN_possessive}"), "curly braces");
            T.Equal("\u0639\u0644\u064A" + tatweel + "[PAWN_objective]", Fix("\u0639\u0644\u0649 [PAWN_objective]"), "square brackets");
            T.Equal("\u0633\u0644\u0627\u062D" + tatweel + "<Prohis>", Fix("\u0633\u0644\u0627\u062D <Prohis>"), "angle brackets");
            T.Equal("\u0646\u0641\u0633" + tatweel + "{PAWN_reflexive}", Fix("\u0646\u0641\u0633 {PAWN_reflexive}"), "a reflexive variable");
        }

        private static void Cases()
        {
            string tatweel = "\u0640";

            T.Equal("\u0634\u0639\u0631{PAWN_possessive}", Fix("\u0634\u0639\u0631 {PAWN_possessive}"), "reh does not connect: only the space goes");
            T.Equal("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", Fix("\u0643\u062A\u0627\u0628  {PAWN_possessive}"), "several spaces go");
            T.Equal("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", Fix("\u0643\u062A\u0627\u0628\u00A0{PAWN_possessive}"), "a no-break space goes");
            T.Equal("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", Fix("\u0643\u062A\u0627\u0628" + tatweel + " {PAWN_possessive}"), "an existing kashida is kept, only the space goes");

            // marks stay behind their letter: the kashida goes after them
            T.Equal("\u0643\u0650\u062A\u064E\u0627\u0628\u064C" + tatweel + "{PAWN_possessive}", Fix("\u0643\u0650\u062A\u064E\u0627\u0628\u064C {PAWN_possessive}"), "marks on the last letter");
            T.Equal("\u0642\u0648\u062A\u064E" + tatweel + "{PAWN_possessive}", Fix("\u0642\u0648\u0629\u064E {PAWN_possessive}"), "taa marbuta with a mark keeps the mark behind the taa");

            // nothing to attach to: the text is not changed
            T.Equal("Hello {PAWN_possessive}", Fix("Hello {PAWN_possessive}"), "after Latin text");
            T.Equal("{PAWN_possessive} \u0643\u062A\u0627\u0628", Fix("{PAWN_possessive} \u0643\u062A\u0627\u0628"), "at the start of the text");
            T.Equal("\u0643\u062A\u0627\u0628. {PAWN_possessive}", Fix("\u0643\u062A\u0627\u0628. {PAWN_possessive}"), "after punctuation");
            T.Equal("\u0643\u062A\u0627\u0628 {PAWN_nameDef}", Fix("\u0643\u062A\u0627\u0628 {PAWN_nameDef}"), "a variable that is not a pronoun");
            T.Equal("\u0643\u062A\u0627\u0628 {0}", Fix("\u0643\u062A\u0627\u0628 {0}"), "a numbered placeholder");
            T.Equal("\u0643\u062A\u0627\u0628 <b>x</b>", Fix("\u0643\u062A\u0627\u0628 <b>x</b>"), "a tag");

            // several variables in one text, each handled on its own; none is lost, moved or renamed
            string input = "\u0623\u062E\u0630 [PAWN_nameDef] \u0643\u062A\u0627\u0628 {PAWN_possessive} \u0648\u0633\u0644\u0627\u062D <Prohis> \u0639\u0644\u0649 [PAWN_objective].";
            string expected = "\u0623\u062E\u0630 [PAWN_nameDef] \u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive} \u0648\u0633\u0644\u0627\u062D" + tatweel + "<Prohis> \u0639\u0644\u064A" + tatweel + "[PAWN_objective].";
            string actual = Fix(input);
            T.Equal(expected, actual, "several variables in one text");
            T.Equal(string.Join("|", PronounRules.VariablesOf(input).ToArray()), string.Join("|", PronounRules.VariablesOf(actual).ToArray()), "the variables are the same before and after");
            T.Equal(actual, Fix(actual), "applying the rule twice changes nothing");

            // the log of the changes
            var changes = new List<string>();
            PronounRules.Apply("\u0643\u062A\u0627\u0628 {PAWN_possessive}", changes);
            T.Equal(1, changes.Count, "one change is reported");
            if (changes.Count == 1) T.Contains(changes[0], "{PAWN_possessive}", "the report names the variable");
            changes = new List<string>();
            PronounRules.Apply("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", changes);
            T.Equal(0, changes.Count, "nothing is reported when nothing changes");

            T.True(object.ReferenceEquals("plain text", PronounRules.Apply("plain text", null)), "text without variables is returned as it is");
            T.True(PronounRules.Apply(null, null) == null, "null stays null");
            T.Equal("", PronounRules.Apply("", null), "the empty text stays empty");
            T.Equal("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}", PronounRules.Apply("\u0643\u062A\u0627\u0628 {PAWN_possessive}", null), "the change list is optional");

            T.Equal("{0}|[b]|<c>", string.Join("|", PronounRules.VariablesOf("a {0} [b] <c> {d").ToArray()), "VariablesOf finds the complete variables in order");
            T.Equal(0, PronounRules.VariablesOf(null).Count, "no variables in null");
        }

        private static void Validation()
        {
            string tatweel = "\u0640";
            T.Null(PronounRules.Validate("\u0643\u062A\u0627\u0628" + tatweel + "{PAWN_possessive}"), "a correctly attached variable");
            T.Null(PronounRules.Validate("\u0634\u0639\u0631{PAWN_possessive}"), "a variable after a non-connecting letter");
            T.Null(PronounRules.Validate("\u0642\u0648\u062A" + tatweel + "{PAWN_possessive}"), "taa + kashida");
            T.Null(PronounRules.Validate("Hello {PAWN_possessive}"), "after Latin text");
            T.Null(PronounRules.Validate("{PAWN_possessive}"), "alone");
            T.Null(PronounRules.Validate("\u0643\u062A\u0627\u0628 {PAWN_nameDef}"), "a variable that is not a pronoun is not checked");
            T.Null(PronounRules.Validate(""), "the empty text");
            T.Null(PronounRules.Validate(null), "null");

            T.Contains(PronounRules.Validate("\u0643\u062A\u0627\u0628 {PAWN_possessive}"), "separated", "a space before the variable");
            T.Contains(PronounRules.Validate("\u0643\u062A\u0627\u0628{PAWN_possessive}"), "kashida", "a connecting letter without kashida");
            T.Contains(PronounRules.Validate("\u0642\u0648\u0629{PAWN_possessive}"), "must be", "taa marbuta directly before the variable");
            T.Contains(PronounRules.Validate("\u0639\u0644\u0649{PAWN_possessive}"), "must be", "alef maksura directly before the variable");
            T.Contains(PronounRules.Validate("\u0643\u062A\u0627\u0628\u064C{PAWN_possessive}"), "kashida", "a connecting letter with a mark and no kashida");

            // everything the rule produces validates
            foreach (char c in RuleLetters + NoKashidaLetters + "\u0647\u064A\u0626\u0629\u0649")
            {
                string fixedText = Fix("\u0643" + c + " {PAWN_possessive} \u0648\u0643" + c + " <Prohis>");
                T.Null(PronounRules.Validate(fixedText), "the result validates after U+" + ((int)c).ToString("X4"));
            }
        }
    }
}
