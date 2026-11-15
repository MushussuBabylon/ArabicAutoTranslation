using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArabicAutoTranslation.Services;
using ArabicAutoTranslation.Utilities;
using RimWorld;
using UnityEngine;
using Verse;

namespace ArabicAutoTranslation.Translators
{
    public abstract class Translator_BaseOnlineAIModel : ITranslator
    {
        public abstract string Name { get; }
        public bool Ready { get; set; }
        public virtual bool RequiresKey => true;

        public TranslatorSettings Settings { get; set; }
        public TranslatorSettings_AIModel Config
        {
            get
            {
                if (Settings == null) Settings = new TranslatorSettings_AIModel();
                return Settings as TranslatorSettings_AIModel;
            }
        }

        public virtual string Model => Config?.UserSelectedModel;

        /// <summary>Last error from loading the model list, for display in settings UI. Null when OK.</summary>
        public string LastModelsError { get; private set; }

        public List<string> Models
        {
            get
            {
                if (_models != null && _models.Count > 0) return _models;

                try
                {
                    var result = GetModels();

                    if (result == null || result.Count == 0)
                    {
                        LastModelsError = "AT_Setting_NoModelFound".Translate();
                        _models = null; // do NOT cache failure - allow retry on next click
                        return new List<string>();
                    }

                    LastModelsError = null;
                    _models = result;
                    return _models;
                }
                catch (Exception e)
                {
                    LastModelsError = NetworkHelper.ExtractErrorMessage(e);
                    var msg = ArabicAutoTranslation.LogPrefix + $"{Name}: Failed to load models: {LastModelsError}";
                    Log.WarningOnce(msg, msg.GetHashCode());
                    _models = null;
                    return new List<string>();
                }
            }
        }

        public abstract string BaseURL { get; }

        public virtual void Prepare()
        {
            if (Settings == null) Settings = new TranslatorSettings_AIModel();
            
            if (string.IsNullOrEmpty(Config.UserAPIKey) && RequiresKey) return;
            Ready = true;
        }

        public bool TryTranslate(string text, out string translated)
        {
            return TryTranslate(text, out translated, false);
        }

        public bool TryTranslate(string text, out string translated, bool skipRetry)
        {
            if (string.IsNullOrEmpty(text))
            {
                translated = string.Empty;
                return true;
            }

            if (string.IsNullOrEmpty(Model))
            {
                var msg = ArabicAutoTranslation.LogPrefix + $"{Name}: Model is not set!";
                Log.ErrorOnce(msg, msg.GetHashCode());
                translated = text;
                return false;
            }

            // when skipRetry is true, do not retry (maxRetries = 0)
            var maxRetries = skipRetry ? 0 : 2;
            var retryCount = 0;

            while (retryCount <= maxRetries)
            {
                try
                {
                    // Phase 1: Protect placeholders
                    var (protectedText, placeholders) = text.ProtectPlaceholders();

                    // Phase 2: AI Translation - Pass prompt explicitly
                    var prompt = GetNormalPrompt();
                    var rawResponse = GetResponseUnsafe(protectedText, prompt);
                    RecordUsage(rawResponse);
                    var translatedProtected = ParseResponse(rawResponse);

                    // Phase 3: Restore placeholders
                    var (restoredText, allRestored) = translatedProtected.RestorePlaceholders(placeholders);

                    // Validation
                    if (allRestored && text.ValidatePlaceholderCount(restoredText))
                    {
                        translated = restoredText;
                        
                        if (retryCount > 0)
                        {
                            Log.Message(ArabicAutoTranslation.LogPrefix + $"{Name}: Successfully translated after {retryCount} retries.");
                        }
                        
                        return true;
                    }
                    else
                    {
                        // Validation failed
                        retryCount++;
                        
                        if (retryCount <= maxRetries)
                        {
                            Log.Warning(ArabicAutoTranslation.LogPrefix + 
                                $"{Name}: Placeholder restoration/validation failed (attempt {retryCount}/{maxRetries}). Retrying...");
                            Thread.Sleep(500); // Brief delay before retry
                            continue;
                        }
                        else
                        {
                            // Max retries reached, return original
                            Log.Warning(ArabicAutoTranslation.LogPrefix + 
                                $"{Name}: Failed to translate after {maxRetries} retries. Placeholder mismatch detected. Returning original text.");
                            translated = text;
                            return false;
                        }
                    }
                }
                catch (Exception e)
                {
                    retryCount++;
                    
                    if (retryCount <= maxRetries)
                    {
                        Log.Warning(ArabicAutoTranslation.LogPrefix + 
                            $"{Name}: Translation exception (attempt {retryCount}/{maxRetries}): {e.Message}. Retrying...");
                        Thread.Sleep(500);
                        continue;
                    }
                    else
                    {
                        var msg = ArabicAutoTranslation.LogPrefix + $"{Name}, translate failed after {maxRetries} retries. reason: {e.GetType()}|{e.Message}";
                        Log.WarningOnce(msg + $", target: {text}", msg.GetHashCode());
                        translated = text;
                        return false;
                    }
                }
            }

            // Should never reach here, but just in case
            translated = text;
            return false;
        }

        // Batch translation support
        public virtual bool SupportsBatchTranslation => Config.EnableBatchTranslation;

        public virtual int BatchSizeTokens => Config.BatchSizeTokens;

        // No translator-side cap by default
        public virtual int MaxConcurrentRequests => 0;

        public bool TryTranslateBatch(List<string> texts, out List<string> translated)
        {
            translated = new List<string>();
            
            if (texts == null || texts.Count == 0)
            {
                return true;
            }

            if (string.IsNullOrEmpty(Model))
            {
                var msg = ArabicAutoTranslation.LogPrefix + $"{Name}: Model is not set!";
                Log.ErrorOnce(msg, msg.GetHashCode());
                translated = new List<string>(texts);
                return false;
            }

            try
            {
                // Phase 1: Protect placeholders for each text
                var protectedTexts = new List<string>();
                var allPlaceholders = new List<Dictionary<string, string>>();
                
                foreach (var text in texts)
                {
                    var (protectedText, placeholders) = text.ProtectPlaceholders();
                    protectedTexts.Add(protectedText);
                    allPlaceholders.Add(placeholders);
                }

                // Phase 2: Build XML batch request
                var batchXml = BuildBatchXml(protectedTexts);

                // Phase 3: Send batch request to AI with BatchPrompt
                // Pass batch prompt as local parameter (thread-safe)
                var batchPrompt = GetBatchPrompt();
                var rawResponse = GetResponseUnsafe(batchXml, batchPrompt);
                RecordUsage(rawResponse);
                var response = ParseResponse(rawResponse);

                // Phase 4: Parse XML response
                var translatedTexts = ParseBatchXml(response, protectedTexts.Count);
                
                if (translatedTexts == null || translatedTexts.Count != protectedTexts.Count)
                {
                    Log.Warning(ArabicAutoTranslation.LogPrefix + 
                        $"{Name}: Batch translation failed - response count mismatch. Expected {protectedTexts.Count}, got {translatedTexts?.Count ?? 0}");
                    translated = new List<string>(texts);
                    return false;
                }

                // Phase 5: Restore placeholders for each translated text
                var results = new List<string>();
                var allSuccess = true;
                
                for (int i = 0; i < translatedTexts.Count; i++)
                {
                    var (restoredText, allRestored) = translatedTexts[i].RestorePlaceholders(allPlaceholders[i]);
                    
                    if (allRestored && texts[i].ValidatePlaceholderCount(restoredText))
                    {
                        results.Add(restoredText);
                    }
                    else
                    {
                        Log.Warning(ArabicAutoTranslation.LogPrefix + 
                            $"{Name}: Placeholder restoration failed for text #{i} in batch");
                        results.Add(texts[i]); // Return original on failure
                        allSuccess = false;
                    }
                }

                translated = results;
                return allSuccess;
            }
            catch (Exception e)
            {
                var msg = ArabicAutoTranslation.LogPrefix + $"{Name}: Batch translation failed: {e.GetType()}|{e.Message}";
                Log.Warning(msg);
                translated = new List<string>(texts);
                return false;
            }
        }

        private string BuildBatchXml(List<string> texts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<translations>");
            
            for (int i = 0; i < texts.Count; i++)
            {
                // Escape XML special characters
                var escapedText = texts[i]
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
                
                sb.AppendLine($"  <text id=\"{i}\">{escapedText}</text>");
            }
            
            sb.AppendLine("</translations>");
            return sb.ToString();
        }

        private List<string> ParseBatchXml(string response, int expectedCount)
        {
            var results = new List<string>();
            
            try
            {
                if (string.IsNullOrEmpty(response))
                {
                    Log.Warning(ArabicAutoTranslation.LogPrefix + "Failed to parse batch XML response - response is null or empty");
                    return null;
                }
                
                // Decode Unicode escape sequences (e.g., \u003c becomes <)
                // JSON-layer parsing already decoded escape sequences; unescaping again would
                // throw on literal backslashes in translated text
                var cleanedResponse = response;
                
                // Clean response - remove markdown code blocks if present
                if (cleanedResponse.Contains("```xml") || cleanedResponse.Contains("```"))
                {
                    cleanedResponse = Regex.Replace(cleanedResponse, @"```xml\s*", "", RegexOptions.IgnoreCase);
                    cleanedResponse = Regex.Replace(cleanedResponse, @"```\s*", "");
                }
                
                // Extract text elements using regex
                var pattern = @"<text\s+id\s*=\s*[""'](\d+)[""']\s*>(.*?)</text>";
                var matches = Regex.Matches(cleanedResponse, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                
                if (matches.Count == 0)
                {
                    Log.Warning(ArabicAutoTranslation.LogPrefix + "Failed to parse batch XML response - no text elements found");
                    return null;
                }

                // Sort by id to maintain order
                var sortedMatches = matches.Cast<Match>()
                    .OrderBy(m => int.Parse(m.Groups[1].Value))
                    .ToList();

                foreach (var match in sortedMatches)
                {
                    var content = match.Groups[2].Value;
                    
                    // Unescape XML entities
                    content = content
                        .Replace("&lt;", "<")
                        .Replace("&gt;", ">")
                        .Replace("&quot;", "\"")
                        .Replace("&apos;", "'")
                        .Replace("&amp;", "&");
                    
                    results.Add(content.Trim());
                }

                return results;
            }
            catch (Exception e)
            {
                Log.Warning(ArabicAutoTranslation.LogPrefix + $"Error parsing batch XML: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the batch translation prompt (thread-safe, computed on-demand)
        /// </summary>
        protected string GetBatchPrompt()
        {
            var targetLanguage = GetTargetLanguageName();

            return $"You are translating RimWorld game content into natural {targetLanguage}.\n\n" +
                "TASK: Translate the XML document below. Each <text> element contains content to translate.\n\n" +
                "CRITICAL RULES:\n" +
                "1. PRESERVE all __PH[number]__ tokens exactly as they appear (these are placeholders)\n" +
                "2. PRESERVE all XML tags and id attributes exactly\n" +
                "3. ONLY translate the content inside <text> tags\n" +
                "4. Keep the same XML structure and formatting\n" +
                "5. Output ONLY the translated XML, nothing else\n\n" +
                "EXAMPLE INPUT:\n" +
                "<translations>\n" +
                "  <text id=\"0\">Welcome to __PH0__</text>\n" +
                "  <text id=\"1\">Hello World</text>\n" +
                "</translations>\n\n" +
                $"EXAMPLE OUTPUT ({targetLanguage}):\n" +
                "<translations>\n" +
                "  <text id=\"0\">Welcome to __PH0__</text>\n" +
                "  <text id=\"1\">Hello World</text>\n" +
                "</translations>\n\n" +
                "Now translate the following:";
        }

        public abstract List<string> GetModels();

        public void ResetSettings()
        {
            _models = null;
            _rotater = null;
            _rotaterSource = null;
            LastModelsError = null;
            Prepare();
        }

        /// <summary>
        /// Force reload models list (used when user clicks the dropdown button)
        /// </summary>
        private void RefreshModels()
        {
            _models = null;
        }

        /// <summary>
        /// Core API call method. MUST be overridden to accept prompt parameter.
        /// </summary>
        protected abstract string GetResponseUnsafe(string text, string prompt);

        /// <summary>
        /// Extracts token usage from a raw API response and accumulates it for cost tracking.
        /// Tries the usage key names of each supported API (OpenAI / Anthropic / Gemini).
        /// </summary>
        protected void RecordUsage(string rawResponse)
        {
            if (string.IsNullOrEmpty(rawResponse) || Config == null) return;

            var input = rawResponse.GetLongValueFromJson("prompt_tokens")
                        ?? rawResponse.GetLongValueFromJson("input_tokens")
                        ?? rawResponse.GetLongValueFromJson("promptTokenCount");
            var output = rawResponse.GetLongValueFromJson("completion_tokens")
                         ?? rawResponse.GetLongValueFromJson("output_tokens")
                         ?? rawResponse.GetLongValueFromJson("candidatesTokenCount");

            if (input == null && output == null) return;

            Config.UsageInputTokens += input ?? 0;
            Config.UsageOutputTokens += output ?? 0;
            global::ArabicAutoTranslation.Settings.UsageDirty = true;
        }

        protected virtual string ParseResponse(string response)
        {
            return response.GetStringValueFromJson("text");
        }

        /// <summary>
        /// Gets the base translation prompt template (thread-safe, computed on-demand)
        /// </summary>
        /// <summary>
        /// Language name used in AI prompts: manual override verbatim, otherwise the
        /// active language folder with custom suffixes stripped ("Russian-SK" -> "Russian").
        /// </summary>
        protected static string GetTargetLanguageName()
        {
            if (Helpers.HasLanguageOverride()) return Helpers.EffectiveLanguageFolder();
            return LanguageDatabase.activeLanguage?.LegacyFolderName.NormalizeLanguageFolder() ?? "English";
        }

        protected string GetBasePrompt()
        {
            return $"Translate the following text into natural {GetTargetLanguageName()} suitable for RimWorld game context.\n\n" +
                "CRITICAL RULES:\n" +
                "1. PRESERVE all tokens in the format __PH[number]__ exactly as they appear.\n" +
                "2. Do NOT translate, remove, or modify __PH[number]__ tokens.\n" +
                "3. Output ONLY the translated text, no explanations or additional text.\n" +
                "4. Maintain the same tone and formality as the original.";
        }

        /// <summary>
        /// Gets the normal (non-batch) prompt, respecting user customization (thread-safe)
        /// </summary>
        protected string GetNormalPrompt()
        {
            var customPrompt = Config?.UserCustomPrompt?.Trim();
            return string.IsNullOrEmpty(customPrompt) ? GetBasePrompt() : customPrompt;
        }

        protected int TimeoutMs => Math.Max(10, Config?.RequestTimeoutSeconds ?? 30) * 1000;

        protected string APIKey
        {
            get
            {
                var source = Config?.UserAPIKey ?? string.Empty;
                if (_rotater == null || _rotaterSource != source)
                {
                    _rotaterSource = source;
                    var keys = source.Split(',').Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
                    _rotater = keys.Length > 0 ? new APIKeyRotater(keys) : null;
                }
                return _rotater?.Key ?? string.Empty;
            }
        }

        protected string RequestURL
        {
            get
            {
                var url = Config?.UserCustomBaseURL;
                if (string.IsNullOrEmpty(url))
                {
                    url = BaseURL;
                }

                if (!url.EndsWith("/"))
                {
                    url += "/";
                }

                return url;
            }
        }


        protected APIKeyRotater _rotater = null;
        private string _rotaterSource;

        private List<string> _models;

        /// <summary>
        /// Draws the connection-related fields (API key by default). Overridden by translators
        /// that connect differently (e.g. Claude Code runs a local CLI instead).
        /// </summary>
        protected virtual void DrawConnectionSettings(Listing_Standard ls)
        {
            var apiKeyLabelRect = ls.GetRect(Text.LineHeight);
            Widgets.Label(apiKeyLabelRect, "AT_Setting_APIKey".Translate());
            TooltipHandler.TipRegion(apiKeyLabelRect, "AT_Setting_RequiresAPIKey_Tooltip".Translate());

            Config.UserAPIKey = ls.TextEntry(Config.UserAPIKey);
        }

        /// <summary>
        /// Draws the custom base URL field. Overridden by translators without an HTTP endpoint.
        /// </summary>
        protected virtual void DrawBaseUrlSettings(Listing_Standard ls)
        {
            ls.Label("AT_Setting_CustomBaseURL".Translate());
            Config.UserCustomBaseURL = ls.TextEntry(Config.UserCustomBaseURL);
        }

        protected void DrawCostSection(Listing_Standard ls)
        {
            ls.GapLine();
            ls.Label("AT_Setting_CostTracking".Translate());

            ls.Label("AT_Setting_UsageTokens".Translate(Config.UsageInputTokens.ToString("N0"), Config.UsageOutputTokens.ToString("N0")));

            var inKey = $"{Name}_PriceIn";
            var outKey = $"{Name}_PriceOut";

            var inRect = ls.GetRect(Text.LineHeight + 4f);
            Widgets.Label(inRect.LeftPart(0.6f), "AT_Setting_PriceInputPerMTokens".Translate());
            Config.PriceInputPerMTokens = Helpers.DecimalTextField(inRect.RightPart(0.38f), inKey, Config.PriceInputPerMTokens);

            var outRect = ls.GetRect(Text.LineHeight + 4f);
            Widgets.Label(outRect.LeftPart(0.6f), "AT_Setting_PriceOutputPerMTokens".Translate());
            Config.PriceOutputPerMTokens = Helpers.DecimalTextField(outRect.RightPart(0.38f), outKey, Config.PriceOutputPerMTokens);

            var cost = Config.UsageInputTokens / 1_000_000.0 * Config.PriceInputPerMTokens
                       + Config.UsageOutputTokens / 1_000_000.0 * Config.PriceOutputPerMTokens;
            ls.Label("AT_Setting_EstimatedCost".Translate(cost.ToString("F4")));

            if (ls.ButtonText("AT_Setting_ResetUsage".Translate()))
            {
                Config.UsageInputTokens = 0;
                Config.UsageOutputTokens = 0;
                global::ArabicAutoTranslation.Settings.UsageDirty = true;
            }
        }

        // Height of a slider row: the slider on top, the captions of both ends under it
        private const float SliderHeight = 36f;

        /// <summary>
        /// A horizontal slider with a small caption under each end, returning the (rounded) value.
        /// Built on Unity's GUI.HorizontalSlider: the RimWorld widget Widgets.HorizontalSlider(Rect, float, float, float, bool,
        /// string, string, string, float) is marked obsolete in newer game versions and its replacement is named differently
        /// from version to version, while this call exists in all of them.
        /// </summary>
        private static float DrawSlider(Rect rect, float value, float min, float max, string leftCaption, string rightCaption, float roundTo)
        {
            var sliderRect = new Rect(rect.x, rect.y + 2f, rect.width, 16f);
            var result = GUI.HorizontalSlider(sliderRect, value, min, max);

            var anchor = Text.Anchor;
            var font = Text.Font;
            Text.Font = GameFont.Tiny;

            var captionRect = new Rect(rect.x, rect.y + 18f, rect.width, rect.height - 18f);
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(captionRect, leftCaption);
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(captionRect, rightCaption);

            Text.Anchor = anchor;
            Text.Font = font;

            return roundTo > 0f ? Mathf.RoundToInt(result / roundTo) * roundTo : result;
        }

        public virtual void DrawSettings(Listing_Standard ls)
        {
            if (Settings == null) Settings = new TranslatorSettings_AIModel();

            DrawConnectionSettings(ls);

            ls.Gap();

            ls.Label("AT_Setting_Model".Translate());
            
            // Button click refreshes model list
            if (Widgets.ButtonText(ls.GetRect(30f), string.IsNullOrEmpty(Config.UserSelectedModel) ? "AT_ChooseModel".Translate().ToString() : Config.UserSelectedModel))
            {
                // Force refresh models when button is clicked
                RefreshModels();
                
                // Get fresh model list
                var modelList = Models;
                
                if (modelList != null && modelList.Count > 0)
                {
                    var options = new List<FloatMenuOption>();
                    foreach (var m in modelList)
                    {
                        options.Add(new FloatMenuOption(m, () => Config.UserSelectedModel = m));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                else
                {
                    var failMsg = "AT_Message_NoModelsFound".Translate().ToString();
                    if (!string.IsNullOrEmpty(LastModelsError)) failMsg += "\n" + LastModelsError;
                    Messages.Message(failMsg, MessageTypeDefOf.NegativeEvent);
                }
            }

            if (!string.IsNullOrEmpty(LastModelsError))
            {
                var prevColor = GUI.color;
                GUI.color = Color.red;
                ls.Label(LastModelsError);
                GUI.color = prevColor;
            }

            ls.Label("AT_Setting_ManualModelEntry".Translate());
            Config.UserSelectedModel = ls.TextEntry(Config.UserSelectedModel);

            ls.Gap();

            DrawBaseUrlSettings(ls);

            ls.Gap();

            ls.Label("AT_Setting_CustomPrompt".Translate());
            Config.UserCustomPrompt = ls.TextEntry(Config.UserCustomPrompt, 3);
            
            ls.Gap();

            // Batch translation settings
            var batchCheckRect = ls.GetRect(Text.LineHeight);
            Widgets.CheckboxLabeled(batchCheckRect, "AT_Setting_EnableBatchTranslation".Translate(), ref Config.EnableBatchTranslation);
            TooltipHandler.TipRegion(batchCheckRect, "AT_Setting_EnableBatchTranslation_Tooltip".Translate());
            
            if (Config.EnableBatchTranslation)
            {
                ls.Gap(6f);
                
                var batchSizeLabelRect = ls.GetRect(Text.LineHeight);
                Widgets.Label(batchSizeLabelRect, "AT_Setting_BatchSize".Translate() + $": {Config.BatchSizeTokens}");
                TooltipHandler.TipRegion(batchSizeLabelRect, "AT_Setting_BatchSize_Tooltip".Translate());
                
                // Slider with 100-unit snapping
                var newValue = DrawSlider(
                    ls.GetRect(SliderHeight),
                    Config.BatchSizeTokens,
                    500f,
                    5000f,
                    "500",
                    "5000",
                    100f
                );

                // Snap to nearest 100
                Config.BatchSizeTokens = Mathf.RoundToInt(newValue / 100f) * 100;
                
                ls.Gap(6f);
            }

            ls.Gap();

            var timeoutLabelRect = ls.GetRect(Text.LineHeight);
            Widgets.Label(timeoutLabelRect, "AT_Setting_RequestTimeout".Translate() + $": {Config.RequestTimeoutSeconds}s");
            TooltipHandler.TipRegion(timeoutLabelRect, "AT_Setting_RequestTimeout_Tooltip".Translate());

            var newTimeout = DrawSlider(
                ls.GetRect(SliderHeight),
                Config.RequestTimeoutSeconds,
                10f,
                300f,
                "10s",
                "300s",
                10f
            );
            Config.RequestTimeoutSeconds = Mathf.RoundToInt(newTimeout / 10f) * 10;

            DrawCostSection(ls);

            ls.Gap();

            if (ls.ButtonText("AT_Setting_Reset".Translate()))
            {
                // Reset to defaults
                Config.UserAPIKey = "";
                Config.UserSelectedModel = "";
                Config.UserCustomBaseURL = "";
                Config.UserCustomPrompt = "";
                Config.EnableBatchTranslation = true;
                Config.BatchSizeTokens = 2000;
                Config.RequestTimeoutSeconds = 30;
                Config.PriceInputPerMTokens = 0f;
                Config.PriceOutputPerMTokens = 0f;
                Helpers.ClearDecimalBuffer($"{Name}_PriceIn");
                Helpers.ClearDecimalBuffer($"{Name}_PriceOut");
                ResetSettings();
            }
        }
    }
}
