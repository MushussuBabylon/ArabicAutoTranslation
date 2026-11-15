using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ArabicAutoTranslation.Translators;
using ArabicAutoTranslation.Utilities;
using Verse;

namespace ArabicAutoTranslation.Services
{
    public class TranslationMetadata
    {
        public string TargetLanguage { get; set; }
        public string TranslatorName { get; set; }
        public string TranslatorModel { get; set; }
        public string Date { get; set; }
        public int TranslationCount { get; set; }
    }

    public class GistService
    {
        private const string GistApiUrl = "https://api.github.com/gists";
        private const string GistRawUrl = "https://gist.githubusercontent.com";
        private const string RequestUserAgent = "ArabicAutoTranslation_Gist_Uploader";
        
        // As HugsLib does: stored reversed and flipped at runtime, so that crawlers that scan for the "ghp_" prefix
        // cannot harvest the token. This token only has Gist permission.
        private readonly string GitHubAuthToken = new string("79zbe0K6z7iFkWOpeRWkn0KwiscnCuGYEkyd_phg".ToCharArray().Reverse().ToArray());
        
        private static readonly Regex GistUrlMatch = new Regex(@"gist\.github\.com/(?:[\w-]+/)?([a-f0-9]{32}|[a-f0-9]{20})", RegexOptions.IgnoreCase);
        private static readonly Regex GistIdMatch = new Regex(@"^([a-f0-9]{32}|[a-f0-9]{20})$", RegexOptions.IgnoreCase);

        public class UploadResult
        {
            public bool Success { get; set; }
            public string GistId { get; set; }
            public string GistUrl { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class DownloadResult
        {
            public bool Success { get; set; }
            public Dictionary<string, string> Translations { get; set; }
            public TranslationMetadata Metadata { get; set; }
            public string ErrorMessage { get; set; }
        }

        public UploadResult UploadTranslation(string translationData, TranslationMetadata metadata)
        {
            try
            {
                var description = $"RimWorld Arabic Auto Translation Cache - {metadata.TargetLanguage} ({metadata.TranslatorName})";
                
                // build the metadata as JSON
                var metadataJson = $"{{\"targetLanguage\":\"{CleanForJSON(metadata.TargetLanguage)}\",\"translatorName\":\"{CleanForJSON(metadata.TranslatorName)}\",\"translatorModel\":\"{CleanForJSON(metadata.TranslatorModel)}\",\"date\":\"{CleanForJSON(metadata.Date)}\",\"translationCount\":{metadata.TranslationCount}}}";
                
                // build the Gist payload: translations.xml (same format as the cache file) and metadata.json
                var payload = $"{{\"description\":\"{CleanForJSON(description)}\",\"public\":true,\"files\":{{\"translations.xml\":{{\"content\":\"{CleanForJSON(translationData)}\"}},\"metadata.json\":{{\"content\":\"{CleanForJSON(metadataJson)}\"}}}}}}";
                
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", $"token {GitHubAuthToken}" },
                    { "User-Agent", RequestUserAgent },
                    { "Accept", "application/vnd.github.v3+json" }
                };

                var response = NetworkHelper.Post(GistApiUrl, payload, headers, "application/json", 3);
                
                if (string.IsNullOrEmpty(response))
                {
                    return new UploadResult
                    {
                        Success = false,
                        ErrorMessage = "Empty response from GitHub API"
                    };
                }

                // extract the Gist ID and URL from the response
                var gistIdMatch = Regex.Match(response, @"""id"":\s*""([^""]+)""");
                var htmlUrlMatch = Regex.Match(response, @"""html_url"":\s*""([^""]+)""");
                
                if (gistIdMatch.Success)
                {
                    var gistId = gistIdMatch.Groups[1].Value;
                    var gistUrl = htmlUrlMatch.Success ? htmlUrlMatch.Groups[1].Value : $"https://gist.github.com/{gistId}";
                    
                    return new UploadResult
                    {
                        Success = true,
                        GistId = gistId,
                        GistUrl = gistUrl
                    };
                }
                else
                {
                    return new UploadResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to parse response: {response.Substring(0, Math.Min(200, response.Length))}"
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{ArabicAutoTranslation.LogPrefix}Error uploading to Gist: {ex.Message}");
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public DownloadResult DownloadTranslation(string input)
        {
            try
            {
                // extract the Gist ID (URL or plain ID)
                string gistId = ExtractGistId(input);
                
                if (string.IsNullOrEmpty(gistId))
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid Gist ID or URL format"
                    };
                }

                // fetch the metadata and the file list through the Gist API
                var apiUrl = $"{GistApiUrl}/{gistId}";
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", RequestUserAgent },
                    { "Accept", "application/vnd.github.v3+json" }
                };

                var gistInfo = NetworkHelper.Get(apiUrl, headers, 3);
                
                if (string.IsNullOrEmpty(gistInfo))
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to fetch Gist information"
                    };
                }

                // extract the raw_url of the translations.xml file
                var translationsUrlMatch = Regex.Match(gistInfo, @"""translations\.xml"":\s*\{[^}]*""raw_url"":\s*""([^""]+)""");
                var metadataUrlMatch = Regex.Match(gistInfo, @"""metadata\.json"":\s*\{[^}]*""raw_url"":\s*""([^""]+)""");

                if (!translationsUrlMatch.Success)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "translations.xml file not found in Gist"
                    };
                }

                var translationsUrl = translationsUrlMatch.Groups[1].Value;
                var translationsData = NetworkHelper.Get(translationsUrl, headers, 3);
                
                if (string.IsNullOrEmpty(translationsData))
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to download translations data"
                    };
                }

                // download the metadata (optional)
                TranslationMetadata metadata = null;
                if (metadataUrlMatch.Success)
                {
                    try
                    {
                        var metadataUrl = metadataUrlMatch.Groups[1].Value;
                        var metadataJson = NetworkHelper.Get(metadataUrl, headers, 3);
                        
                        if (!string.IsNullOrEmpty(metadataJson))
                        {
                            metadata = ParseMetadata(metadataJson);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"{ArabicAutoTranslation.LogPrefix}Failed to parse metadata: {ex.Message}");
                    }
                }

                // parse translations.xml (same format as the on-disk cache)
                var translations = TranslationCacheManager.ParseCacheXml(translationsData);
                
                if (translations == null || translations.Count == 0)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = "No translations found in Gist"
                    };
                }

                // use defaults when there is no metadata
                if (metadata == null)
                {
                    metadata = new TranslationMetadata
                    {
                        TargetLanguage = "Unknown",
                        TranslatorName = "Unknown",
                        TranslatorModel = "Unknown",
                        Date = "Unknown",
                        TranslationCount = translations.Count
                    };
                }
                else
                {
                    metadata.TranslationCount = translations.Count;
                }

                return new DownloadResult
                {
                    Success = true,
                    Translations = translations,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                Log.Error($"{ArabicAutoTranslation.LogPrefix}Error downloading from Gist: {ex.Message}");
                return new DownloadResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string ExtractGistId(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            input = input.Trim();

            // check whether it is in URL form
            var urlMatch = GistUrlMatch.Match(input);
            if (urlMatch.Success)
            {
                return urlMatch.Groups[1].Value;
            }

            // check whether it is in plain ID form
            var idMatch = GistIdMatch.Match(input);
            if (idMatch.Success)
            {
                return idMatch.Groups[1].Value;
            }

            return null;
        }


        private TranslationMetadata ParseMetadata(string jsonData)
        {
            try
            {
                var metadata = new TranslationMetadata();
                
                var targetLangMatch = Regex.Match(jsonData, @"""targetLanguage""\s*:\s*""([^""]*)""");
                var translatorNameMatch = Regex.Match(jsonData, @"""translatorName""\s*:\s*""([^""]*)""");
                var translatorModelMatch = Regex.Match(jsonData, @"""translatorModel""\s*:\s*""([^""]*)""");
                var dateMatch = Regex.Match(jsonData, @"""date""\s*:\s*""([^""]*)""");
                var countMatch = Regex.Match(jsonData, @"""translationCount""\s*:\s*(\d+)");
                
                if (targetLangMatch.Success)
                    metadata.TargetLanguage = UnescapeJson(targetLangMatch.Groups[1].Value);
                if (translatorNameMatch.Success)
                    metadata.TranslatorName = UnescapeJson(translatorNameMatch.Groups[1].Value);
                if (translatorModelMatch.Success)
                    metadata.TranslatorModel = UnescapeJson(translatorModelMatch.Groups[1].Value);
                if (dateMatch.Success)
                    metadata.Date = UnescapeJson(dateMatch.Groups[1].Value);
                if (countMatch.Success)
                    metadata.TranslationCount = int.Parse(countMatch.Groups[1].Value);
                
                return metadata;
            }
            catch (Exception ex)
            {
                Log.Error($"{ArabicAutoTranslation.LogPrefix}Error parsing metadata JSON: {ex.Message}");
                return null;
            }
        }

        private string UnescapeJson(string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;
            
            return str
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static string CleanForJSON(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            
            int length = s.Length;
            StringBuilder stringBuilder = new StringBuilder(length + 4);
            for (int index = 0; index < length; index++)
            {
                char ch = s[index];
                switch (ch)
                {
                    case '\b':
                        stringBuilder.Append("\\b");
                        break;
                    case '\t':
                        stringBuilder.Append("\\t");
                        break;
                    case '\n':
                        stringBuilder.Append("\\n");
                        break;
                    case '\f':
                        stringBuilder.Append("\\f");
                        break;
                    case '\r':
                        stringBuilder.Append("\\r");
                        break;
                    case '"':
                    case '\\':
                        stringBuilder.Append('\\');
                        stringBuilder.Append(ch);
                        break;
                    case '/':
                        stringBuilder.Append('\\');
                        stringBuilder.Append(ch);
                        break;
                    default:
                        if (ch < ' ')
                        {
                            string str = "000" + ((int)ch).ToString("X4");
                            stringBuilder.Append("\\u" + str.Substring(str.Length - 4));
                            break;
                        }
                        stringBuilder.Append(ch);
                        break;
                }
            }
            return stringBuilder.ToString();
        }
    }
}

