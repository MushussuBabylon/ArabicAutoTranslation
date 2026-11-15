using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Verse;

namespace ArabicAutoTranslation.Services
{
    /// <summary>
    /// Utility class that detects the language of a text
    /// </summary>
    public static class LanguageDetector
    {
        // language detection based on Unicode ranges
        private static readonly Regex KoreanRegex = new Regex(@"[\uAC00-\uD7A3\u1100-\u11FF\u3130-\u318F]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u309F\u30A0-\u30FF\u31F0-\u31FF]", RegexOptions.Compiled);
        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF\u3400-\u4DBF]", RegexOptions.Compiled);
        private static readonly Regex CyrillicRegex = new Regex(@"[\u0400-\u04FF]", RegexOptions.Compiled); // Russian, Ukrainian
        private static readonly Regex ArabicRegex = new Regex(@"[\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFC]", RegexOptions.Compiled);
        private static readonly Regex ThaiRegex = new Regex(@"[\u0E00-\u0E7F]", RegexOptions.Compiled);
        private static readonly Regex HebrewRegex = new Regex(@"[\u0590-\u05FF]", RegexOptions.Compiled);
        private static readonly Regex GreekRegex = new Regex(@"[\u0370-\u03FF\u1F00-\u1FFF]", RegexOptions.Compiled);
        private static readonly Regex VietnameseRegex = new Regex(@"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềấếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // English and Latin-script languages (default)
        private static readonly Regex LatinRegex = new Regex(@"[a-zA-ZÀ-ÿ]", RegexOptions.Compiled);

        public enum DetectedLanguage
        {
            Unknown,
            English,
            Korean,
            Japanese,
            ChineseSimplified,
            ChineseTraditional,
            Russian,
            Ukrainian,
            Arabic,
            Thai,
            Hebrew,
            Greek,
            Vietnamese,
            Mixed,
            Latin  // Generic Latin-based language
        }

        /// <summary>
        /// Detects the main language of a text.
        /// </summary>
        /// <param name="text">Text to analyze</param>
        /// <returns>The detected language</returns>
        // Vietnamese diacritic characters (both cases) for the allocation-free scanner below
        private static readonly HashSet<char> VietnameseChars = BuildVietnameseChars();

        private static HashSet<char> BuildVietnameseChars()
        {
            const string lower = "àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềấếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ";
            var set = new HashSet<char>();
            foreach (var c in lower)
            {
                set.Add(c);
                set.Add(char.ToUpperInvariant(c));
            }
            return set;
        }

        public static DetectedLanguage Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DetectedLanguage.Unknown;

            // Single allocation-free pass. The previous implementation ran 10 regexes over
            // the full text and allocated a Match object per character hit - measured as the
            // dominant per-string cost of the whole def scan.
            int totalChars = 0;
            int koreanCount = 0, japaneseCount = 0, chineseCount = 0, cyrillicCount = 0, arabicCount = 0,
                thaiCount = 0, hebrewCount = 0, greekCount = 0, vietnameseCount = 0, latinCount = 0;

            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) continue;
                totalChars++;

                int ci = c;
                // Same Unicode ranges as the old regexes
                if ((ci >= 0xAC00 && ci <= 0xD7A3) || (ci >= 0x1100 && ci <= 0x11FF) || (ci >= 0x3130 && ci <= 0x318F)) koreanCount++;
                if ((ci >= 0x3040 && ci <= 0x309F) || (ci >= 0x30A0 && ci <= 0x30FF) || (ci >= 0x31F0 && ci <= 0x31FF)) japaneseCount++;
                if ((ci >= 0x4E00 && ci <= 0x9FFF) || (ci >= 0x3400 && ci <= 0x4DBF)) chineseCount++;
                if (ci >= 0x0400 && ci <= 0x04FF) cyrillicCount++;
                if ((ci >= 0x0600 && ci <= 0x06FF) || (ci >= 0x0750 && ci <= 0x077F) || (ci >= 0xFB50 && ci <= 0xFDFF) || (ci >= 0xFE70 && ci <= 0xFEFC)) arabicCount++;
                if (ci >= 0x0E00 && ci <= 0x0E7F) thaiCount++;
                if (ci >= 0x0590 && ci <= 0x05FF) hebrewCount++;
                if ((ci >= 0x0370 && ci <= 0x03FF) || (ci >= 0x1F00 && ci <= 0x1FFF)) greekCount++;
                if (VietnameseChars.Contains(c)) vietnameseCount++;
                if ((ci >= 'a' && ci <= 'z') || (ci >= 'A' && ci <= 'Z') || (ci >= 0x00C0 && ci <= 0x00FF)) latinCount++;
            }

            if (totalChars < 2)
                return DetectedLanguage.Unknown;

            // calculate the ratios (a language is chosen when it reaches at least 20%)
            const double threshold = 0.2;
            
            // calculate the ratio of each language
            double koreanRatio = (double)koreanCount / totalChars;
            double japaneseRatio = (double)japaneseCount / totalChars;
            double chineseRatio = (double)chineseCount / totalChars;
            double cyrillicRatio = (double)cyrillicCount / totalChars;
            double arabicRatio = (double)arabicCount / totalChars;
            double thaiRatio = (double)thaiCount / totalChars;
            double hebrewRatio = (double)hebrewCount / totalChars;
            double greekRatio = (double)greekCount / totalChars;
            double vietnameseRatio = (double)vietnameseCount / totalChars;
            double latinRatio = (double)latinCount / totalChars;

            // check the languages that have their own script first
            var priorityLanguages = new[]
            {
                (DetectedLanguage.Korean, koreanRatio),
                (DetectedLanguage.Japanese, japaneseRatio),
                (DetectedLanguage.ChineseSimplified, chineseRatio), // Simplified/Traditional distinction later
                (DetectedLanguage.Arabic, arabicRatio),
                (DetectedLanguage.Thai, thaiRatio),
                (DetectedLanguage.Hebrew, hebrewRatio),
                (DetectedLanguage.Greek, greekRatio),
                (DetectedLanguage.Vietnamese, vietnameseRatio),
            };

            var maxPriority = priorityLanguages.OrderByDescending(x => x.Item2).First();
            if (maxPriority.Item2 >= threshold)
                return maxPriority.Item1;

            // Cyrillic (Russian/Ukrainian)
            if (cyrillicRatio >= threshold)
                return DetectedLanguage.Russian; // Russian by default; telling Russian and Ukrainian apart is hard

            // Latin-script languages (including English)
            if (latinRatio >= threshold)
            {
                // no further distinction: unify as English or return Latin
                return DetectedLanguage.English;
            }

            // mixed languages or undecidable
            return latinRatio > 0.1 ? DetectedLanguage.English : DetectedLanguage.Unknown;
        }

        /// <summary>
        /// Checks whether a text is in a given language.
        /// </summary>
        public static bool IsLanguage(string text, string targetLanguageCode)
        {
            if (string.IsNullOrWhiteSpace(targetLanguageCode))
                return false;

            var detected = Detect(text);
            
            // match against the RimWorld language code
            var lowerCode = targetLanguageCode.ToLower();
            
            // exact matches first
            if (lowerCode.Contains("korean"))
                return detected == DetectedLanguage.Korean;
            if (lowerCode.Contains("japanese") || lowerCode.Contains("日本語"))
                return detected == DetectedLanguage.Japanese;
            if (lowerCode.Contains("chinese") || lowerCode.Contains("简体") || lowerCode.Contains("繁體"))
                return detected == DetectedLanguage.ChineseSimplified || detected == DetectedLanguage.ChineseTraditional;
            if (lowerCode.Contains("russian") || lowerCode.Contains("русский"))
                return detected == DetectedLanguage.Russian;
            if (lowerCode.Contains("ukrainian") || lowerCode.Contains("українська"))
                return detected == DetectedLanguage.Ukrainian || detected == DetectedLanguage.Russian; // Cyrillic
            if (lowerCode.Contains("arabic"))
                return detected == DetectedLanguage.Arabic;
            if (lowerCode.Contains("thai"))
                return detected == DetectedLanguage.Thai;
            if (lowerCode.Contains("hebrew"))
                return detected == DetectedLanguage.Hebrew;
            if (lowerCode.Contains("greek") || lowerCode.Contains("ελληνικά"))
                return detected == DetectedLanguage.Greek;
            if (lowerCode.Contains("vietnamese") || lowerCode.Contains("tiếng việt"))
                return detected == DetectedLanguage.Vietnamese;
            
            // check English
            if (lowerCode.Contains("english") || lowerCode == "en")
                return detected == DetectedLanguage.English || detected == DetectedLanguage.Latin;
            
            return false;
        }

        /// <summary>
        /// Checks whether a text is already written in the target language.
        /// If it is, there is no need to translate it.
        /// </summary>
        public static bool IsAlreadyInTargetLanguage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string targetLanguageFolderName;

            if (Utilities.Helpers.HasLanguageOverride())
            {
                // The user picked an explicit target - judge against it, not the game language
                targetLanguageFolderName = Utilities.Helpers.EffectiveLanguageFolder();
            }
            else
            {
                // get the currently active language
                var activeLanguage = LanguageDatabase.activeLanguage;
                if (activeLanguage == null)
                    return false;

                // default language (English): hand over to the English target handling below
                targetLanguageFolderName = activeLanguage == LanguageDatabase.defaultLanguage
                    ? "English"
                    : activeLanguage.folderName;
            }

            // English target: Latin-only text is treated as already English. Telling English apart
            // from other Latin-script languages is unreliable, and sending every English def string
            // to the translator floods it (Google detects 'en' == target and returns it unchanged).
            // Only text containing a non-Latin script (CJK, Hangul, Cyrillic, ...) gets translated.
            if (IsEnglishLanguage(targetLanguageFolderName))
            {
                return !ContainsNonLatinScript(text);
            }

            // Latin-script languages (English, German, French, ...) share one writing system,
            // so text analysis alone cannot tell them apart reliably (many false positives).
            // The duplicate-translation guard is therefore only enabled when the writing systems are completely different
            // (for example English <-> Chinese or Cyrillic).
            if (IsNonLatinScriptLanguage(targetLanguageFolderName))
            {
                return IsLanguage(text, targetLanguageFolderName);
            }

            // for Latin-script languages the detection is switched off to avoid false positives, and a translation is always attempted.
            return false;
        }

        private static bool IsEnglishLanguage(string languageFolderName)
        {
            var lower = languageFolderName.Trim().ToLower();
            return lower.StartsWith("english") || lower == "en" || lower.StartsWith("en-");
        }

        /// <summary>
        /// Checks whether the text contains at least one non-Latin character (Hangul, kana, Han, Cyrillic, Arabic, Thai, Hebrew, Greek).
        /// </summary>
        private static bool ContainsNonLatinScript(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int ci = text[i];
                if ((ci >= 0xAC00 && ci <= 0xD7A3) || (ci >= 0x1100 && ci <= 0x11FF) || (ci >= 0x3130 && ci <= 0x318F) || // Hangul
                    (ci >= 0x3040 && ci <= 0x30FF) || (ci >= 0x31F0 && ci <= 0x31FF) || // Kana
                    (ci >= 0x4E00 && ci <= 0x9FFF) || (ci >= 0x3400 && ci <= 0x4DBF) || // CJK
                    (ci >= 0x0370 && ci <= 0x03FF) || (ci >= 0x1F00 && ci <= 0x1FFF) || // Greek
                    (ci >= 0x0400 && ci <= 0x04FF) || // Cyrillic
                    (ci >= 0x0590 && ci <= 0x05FF) || // Hebrew
                    (ci >= 0x0600 && ci <= 0x06FF) || (ci >= 0x0750 && ci <= 0x077F) || (ci >= 0xFB50 && ci <= 0xFDFF) || (ci >= 0xFE70 && ci <= 0xFEFC) || // Arabic (letters and presentation forms)
                    (ci >= 0x0E00 && ci <= 0x0E7F)) // Thai
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the language mainly uses a non-Latin script (Hangul, Han, Cyrillic, ...).
        /// </summary>
        private static bool IsNonLatinScriptLanguage(string languageFolderName)
        {
             var lower = languageFolderName.ToLower();
             return lower.Contains("korean") ||
                    lower.Contains("japanese") || lower.Contains("日本語") ||
                    lower.Contains("chinese") || lower.Contains("简体") || lower.Contains("繁體") ||
                    lower.Contains("russian") || lower.Contains("русский") ||
                    lower.Contains("ukrainian") || lower.Contains("українська") || // Cyrillic
                    lower.Contains("arabic") || 
                    lower.Contains("thai") || 
                    lower.Contains("hebrew") || 
                    lower.Contains("greek");
        }

        /// <summary>
        /// Gets the language code and the display name.
        /// </summary>
        public static (string code, string name, UnityEngine.Color color) GetLanguageDisplayInfo(DetectedLanguage language)
        {
            switch (language)
            {
                case DetectedLanguage.Korean:
                    return ("KO", "Korean", new UnityEngine.Color(0.2f, 0.6f, 1f, 0.3f));
                case DetectedLanguage.Japanese:
                    return ("JP", "日本語 (Japanese)", new UnityEngine.Color(1f, 0.4f, 0.4f, 0.3f));
                case DetectedLanguage.ChineseSimplified:
                    return ("CN", "简体中文 (Chinese Simplified)", new UnityEngine.Color(1f, 0.8f, 0.2f, 0.3f));
                case DetectedLanguage.ChineseTraditional:
                    return ("TW", "繁體中文 (Chinese Traditional)", new UnityEngine.Color(1f, 0.7f, 0.1f, 0.3f));
                case DetectedLanguage.Russian:
                    return ("RU", "Русский (Russian)", new UnityEngine.Color(0.6f, 0.2f, 1f, 0.3f));
                case DetectedLanguage.Ukrainian:
                    return ("UK", "Українська (Ukrainian)", new UnityEngine.Color(0.4f, 0.6f, 1f, 0.3f));
                case DetectedLanguage.Arabic:
                    return ("AR", "العربية (Arabic)", new UnityEngine.Color(0.8f, 0.6f, 0.2f, 0.3f));
                case DetectedLanguage.Thai:
                    return ("TH", "ไทย (Thai)", new UnityEngine.Color(0.9f, 0.3f, 0.7f, 0.3f));
                case DetectedLanguage.Hebrew:
                    return ("HE", "עברית (Hebrew)", new UnityEngine.Color(0.5f, 0.5f, 1f, 0.3f));
                case DetectedLanguage.Greek:
                    return ("GR", "Ελληνικά (Greek)", new UnityEngine.Color(0.3f, 0.5f, 0.9f, 0.3f));
                case DetectedLanguage.Vietnamese:
                    return ("VI", "Tiếng Việt (Vietnamese)", new UnityEngine.Color(0.9f, 0.5f, 0.3f, 0.3f));
                case DetectedLanguage.English:
                    return ("EN", "English", new UnityEngine.Color(0.3f, 0.8f, 0.3f, 0.3f));
                case DetectedLanguage.Latin:
                    return ("LA", "Latin-based", new UnityEngine.Color(0.6f, 0.6f, 0.6f, 0.3f));
                case DetectedLanguage.Mixed:
                    return ("MX", "Mixed", new UnityEngine.Color(0.7f, 0.7f, 0.7f, 0.3f));
                default: // Unknown
                    return ("?", "Unknown", new UnityEngine.Color(0.5f, 0.5f, 0.5f, 0.2f));
            }
        }

        /// <summary>
        /// For debugging: prints detailed language information about a text.
        /// </summary>
        public static string GetLanguageInfo(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Empty text";

            var detected = Detect(text);
            var trimmedText = Regex.Replace(text, @"\s+", "");
            int totalChars = trimmedText.Length;
            
            if (totalChars == 0)
                return "Empty text";
            
            // Count characters for all supported language scripts
            var languageCounts = new System.Collections.Generic.List<(string code, string name, int count)>
            {
                ("KR", "Korean", KoreanRegex.Matches(text).Count),
                ("JP", "Japanese", JapaneseRegex.Matches(text).Count),
                ("CN", "Chinese", ChineseRegex.Matches(text).Count),
                ("CY", "Cyrillic", CyrillicRegex.Matches(text).Count),
                ("AR", "Arabic", ArabicRegex.Matches(text).Count),
                ("TH", "Thai", ThaiRegex.Matches(text).Count),
                ("HE", "Hebrew", HebrewRegex.Matches(text).Count),
                ("GR", "Greek", GreekRegex.Matches(text).Count),
                ("VI", "Vietnamese", VietnameseRegex.Matches(text).Count),
                ("LA", "Latin", LatinRegex.Matches(text).Count)
            };
            
            // Filter to only show languages with count >= 1
            var activeLanguages = languageCounts
                .Where(l => l.count >= 1)
                .OrderByDescending(l => l.count)
                .Select(l => $"{l.code}: {l.count} ({(double)l.count/totalChars:P0})")
                .ToList();
            
            var languageInfo = activeLanguages.Count > 0 
                ? string.Join(", ", activeLanguages)
                : "No recognizable characters";
            
            return $"Detected: {detected} | Total: {totalChars} chars | {languageInfo}";
        }
    }
}

