using System;
using System.IO;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>Shared test data: the shipped glossary (parsed once), the reference glossary text, temporary directories.</summary>
    internal static class Fixtures
    {
        private static string _shippedText;
        private static GlossaryData _shippedData;
        private static GlossaryIndex _shippedIndex;
        private static string _referenceText;

        /// <summary>
        /// A data file: next to the test program (Data/ is copied there by the project), or - when the program runs from
        /// somewhere else - found by walking up from the program's folder to the repository.
        /// </summary>
        internal static string Locate(string dataFile, string repositoryRelative)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(Path.Combine(baseDir, "Data"), dataFile);
            if (File.Exists(local)) return local;

            string dir = baseDir;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, repositoryRelative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("cannot find " + dataFile + " (looked next to " + baseDir + " and in the parent folders)");
        }

        /// <summary>The text of Arabic/ArabicGlossary.txt, the glossary the game ships.</summary>
        internal static string ShippedGlossaryText
        {
            get
            {
                if (_shippedText == null)
                {
                    _shippedText = File.ReadAllText(Locate("ArabicGlossary.txt", "Arabic/ArabicGlossary.txt"), Encoding.UTF8);
                }
                return _shippedText;
            }
        }

        internal static GlossaryData ShippedData
        {
            get
            {
                if (_shippedData == null) _shippedData = GlossaryParser.Parse(ShippedGlossaryText);
                return _shippedData;
            }
        }

        internal static GlossaryIndex ShippedIndex
        {
            get
            {
                if (_shippedIndex == null) _shippedIndex = new GlossaryIndex(ShippedData);
                return _shippedIndex;
            }
        }

        /// <summary>The terminology glossary the project was given (the source of the shipped glossary's terms).</summary>
        internal static string ReferenceGlossaryText
        {
            get
            {
                if (_referenceText == null)
                {
                    _referenceText = File.ReadAllText(
                        Locate("RimWorld_Glossary.reference.txt", "Source/ArabicAutoTranslation.Tests/Data/RimWorld_Glossary.reference.txt"), Encoding.UTF8);
                }
                return _referenceText;
            }
        }

        internal static ArabicPipeline ShippedPipeline(DisplayOptions options)
        {
            return new ArabicPipeline(ShippedIndex, options ?? DisplayOptions.Default());
        }

        internal static ArabicPipeline PipelineFor(string glossaryText, DisplayOptions options)
        {
            return new ArabicPipeline(new GlossaryIndex(GlossaryParser.Parse(glossaryText)), options ?? DisplayOptions.Default());
        }

        /// <summary>A new, empty temporary directory.</summary>
        internal static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ArabicAutoTranslation.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static void DeleteTempDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // a leftover temporary folder is not a test failure
            }
        }

        /// <summary>A small glossary that exercises every matching feature, independent of the shipped data.</summary>
        internal const string MiniGlossary =
            "@glossary-version 5\n" +
            "[Core | ludeon.rimworld]\n" +
            "Colonist | \u0645\u0633\u062A\u0639\u0645\u0631 | pl=\u0645\u0633\u062A\u0639\u0645\u0631\u064A\u0646\n" +
            "Turret | \u0628\u0631\u062C | pl=\u0623\u0628\u0631\u0627\u062C\n" +
            "Slag | \u062E\u0631\u062F\u0629\n" +
            "Chunk | \u0643\u062A\u0644\u0629 | pl=\u0643\u062A\u0644\n" +
            "Will | \u0625\u0631\u0627\u062F\u0629 | cs\n" +
            "Spot | \u0645\u0643\u0627\u0646 | guard | pl=\u0623\u0645\u0627\u0643\u0646\n" +
            "Investigate | \u062A\u062D\u0642\u064A\u0642 \u0641\u064A | mode=start\n" +
            "Psychic | \u0646\u0641\u0633\u064A | mode=whole | adj\n" +
            "Psychic Drone | \u0637\u0646\u064A\u0646 \u0646\u0641\u0633\u064A\n" +
            "Mechanoid / Mech | \u0645\u064A\u0643\u0627\u0646\u0648\u064A\u062F\n" +
            "The Empire / Empire | \u0627\u0644\u0625\u0645\u0628\u0631\u0627\u0637\u0648\u0631\u064A\u0629\n" +
            "Assassin | \u0645\u063A\u062A\u0627\u0644 | pl=\u0645\u063A\u062A\u0627\u0644\u064A\u0646 | fem=\u0645\u063A\u062A\u0627\u0644\u0629\n" +
            "[Royalty | ludeon.rimworld.royalty]\n" +
            "Foo | \u0641\u0648\n" +
            "[Ideology | ludeon.rimworld.ideology]\n" +
            "Foo | \u0641\u0648\u0648\n" +
            "Raider | \u0627\u0644\u063A\u0632\u0648 | mode=whole | key=MemeDef | id=ideology.raider\n" +
            "[Standardized]\n" +
            "Raider | \u063A\u0627\u0632\u064A | pl=\u063A\u0632\u0627\u0629\n";
    }
}
