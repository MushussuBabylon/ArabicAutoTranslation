/*
Original code: https://github.com/HIllya51/LunaTranslator/blob/main/src/LunaTranslator/translator/yandex.py
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.Networking;
using Verse;
using ArabicAutoTranslation;
using ArabicAutoTranslation.Services;
using ArabicAutoTranslation.Utilities;

namespace ArabicAutoTranslation.Translators
{
    public class Translator_Yandex : Translator_BaseTraditional
    {
        private const string translateUrl = "https://browser.translate.yandex.net/api/v1/tr.json/translate";

        // Browser-like request headers. Without them the request carries no User-Agent at all, which public web
        // endpoints often refuse with HTTP 403 (the Google translator sends the same User-Agent)
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        private static readonly Dictionary<string, string> requestHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = UserAgent,
            ["Accept"] = "*/*",
            ["Accept-Language"] = "en-US,en;q=0.9"
        };

        public override string Name => "Yandex";

        // Not cached: the user can change the target language override at runtime
        public override string TranslateLanguage => GetTranslateLanguage();
        public override bool RequiresKey => false;

        public override void Prepare()
        {
            // No connectivity preflight (it blocked game loading when offline);
            // real requests + the circuit breaker handle reachability.
            Ready = true;
        }

        public override bool TryTranslate(string text, out string translated)
        {
            try
            {
                // Protect placeholders. They are sent as numbers in braces ("{0}"), not as "__PH0__": Yandex read the PH of
                // that token as a word (the pH of chemistry), spaced the underscores out and moved words into the token
                var protectedText = text.ProtectNumbered(out var placeholders);

                // Yandex auto-detects source language, only specify target language
                var translateParams = $"?lang={UnityWebRequest.EscapeURL(TranslateLanguage)}&text={UnityWebRequest.EscapeURL(protectedText)}&srv=browser_video_translation";
                
                // Send POST request with empty body and no Content-Type (Yandex doesn't require body parameters)
                var response = NetworkHelper.Post(translateUrl + translateParams, "", requestHeaders, contentType: null);

                // The server accepted the request: whatever happens to the text below, this is not a block
                TranslatorManager.ReportAccessOk(this);

                // Parse response - Yandex returns {"code": 200, "lang": "ja-en", "text": ["translated text"]}
                // The string body must allow escape pairs (\" etc.) or translations containing
                // quotes would fail to match at all
                var textArrayPattern = @"""text""\s*:\s*\[\s*""((?:\\.|[^""\\])*)""";
                var match = Regex.Match(response, textArrayPattern);

                if (!match.Success || match.Groups.Count < 2)
                {
                    translated = text;
                    return false;
                }

                var translatedProtected = match.Groups[1].Value.UnescapeJsonString();
                
                // Restore placeholders, wherever Yandex moved them. The text must then carry exactly the placeholders that were
                // sent; if it does not, the log shows what was sent and what came back, and the text is translated piece by piece
                string restoredText;
                if (!translatedProtected.RestoreNumbered(placeholders, out restoredText))
                {
                    Helpers.LogRestoreFailure(Name, protectedText, translatedProtected);
                    translated = text;
                    return false;
                }

                translated = restoredText;
                return true;
            }
            catch (Exception e)
            {
                var denied = NetworkHelper.IsAccessDenied(e);
                var msg = ArabicAutoTranslation.LogPrefix + $"{Name}, translate failed. reason: {e.GetType()}|{e.Message}";
                if (denied)
                {
                    msg += " (the server refused the request - blocked network or region; the mod can switch to Google, see the translator settings)";
                }
                Log.WarningOnce(msg + $", target: {text}", msg.GetHashCode());
                if (denied) TranslatorManager.ReportAccessDenied(this);
                translated = text;
                return false;
            }
        }

        public override bool SupportsCurrentLanguage()
        {
            if (Helpers.HasLanguageOverride()) return true;

            var lang = LanguageDatabase.activeLanguage;
            if (lang == null)
            {
                Log.Warning(ArabicAutoTranslation.LogPrefix + "activeLanguage was null");
                return false;
            }

            return TranslateLanguageGetter.ContainsKey(lang.LegacyFolderName.NormalizeLanguageFolder());
        }

        private static readonly Dictionary<string, string> TranslateLanguageGetter = new Dictionary<string, string>
        {
            // RimWorld supported languages with Yandex language codes
            ["Korean"] = "ko",
            ["ChineseSimplified"] = "zh",
            ["ChineseTraditional"] = "zh",
            ["English"] = "en",
            ["French"] = "fr",
            ["German"] = "de",
            ["Italian"] = "it",
            ["Japanese"] = "ja",
            ["Polish"] = "pl",
            ["Portuguese"] = "pt",
            ["PortugueseBrazilian"] = "pt",
            ["Russian"] = "ru",
            ["Spanish"] = "es",
            ["SpanishLatin"] = "es",
            ["Turkish"] = "tr",
            ["Czech"] = "cs",
            ["Danish"] = "da",
            ["Dutch"] = "nl",
            ["Finnish"] = "fi",
            ["Norwegian"] = "no",
            ["Swedish"] = "sv",
            ["Ukrainian"] = "uk",
            ["Hungarian"] = "hu",
            ["Arabic"] = "ar",
            ["Romanian"] = "ro",
            ["Slovak"] = "sk",
            ["Estonian"] = "et",
            ["Greek"] = "el",
            ["Thai"] = "th",
            ["Indonesian"] = "id",
            ["Catalan"] = "ca",
            ["Vietnamese"] = "vi",
            ["Serbian"] = "sr",
            
            // Additional Yandex supported languages
            ["Albanian"] = "sq",
            ["Armenian"] = "hy",
            ["Azerbaijani"] = "az",
            ["Basque"] = "eu",
            ["Belarusian"] = "be",
            ["Bulgarian"] = "bg",
            ["Croatian"] = "hr",
            ["Georgian"] = "ka",
            ["Hebrew"] = "he",
            ["Hindi"] = "hi",
            ["Kazakh"] = "kk",
            ["Latvian"] = "lv",
            ["Lithuanian"] = "lt",
            ["Macedonian"] = "mk",
            ["Mongolian"] = "mn",
            ["Persian"] = "fa",
            ["Slovenian"] = "sl",
            ["Tajik"] = "tg",
            ["Tatar"] = "tt",
            ["Uzbek"] = "uz"
        };
        
        private static string GetTranslateLanguage()
        {
            var res = Helpers.ResolveTargetLanguage(TranslateLanguageGetter);
            if (res == null)
            {
                Log.Error(ArabicAutoTranslation.LogPrefix + $"Unsupported language: {LanguageDatabase.activeLanguage?.LegacyFolderName ?? "(null)"}");
                res = "en";
            }
            return res;
        }
    }
}

