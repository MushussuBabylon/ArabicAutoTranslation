using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ArabicAutoTranslation.Services;
using ArabicAutoTranslation.Translators;
using ArabicAutoTranslation.Utilities;
using Verse;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>One queued translation in Arabic mode: what the glossary needs to know and where the result goes.</summary>
    internal sealed class ArabicJob
    {
        /// <summary>The cache key the rest of the mod uses.</summary>
        internal string CacheKey;
        /// <summary>The original English text.</summary>
        internal string Source;
        /// <summary>Mod, def type / field and grammatical hint of the text (decides context dependent glossary terms).</summary>
        internal GlossaryContext Context;
        /// <summary>The pipeline the job was processed with (the cache entry is stamped with its versions).</summary>
        internal ArabicPipeline Pipeline;
        /// <summary>The outcome, set by the worker before the completion callback runs.</summary>
        internal PipelineResult Result;
    }

    /// <summary>
    /// The game side of the Arabic support: decides whether Arabic is the target language, loads the glossary and the
    /// Arabic cache, runs the pipeline for the translation worker and answers the editor / merge / share dialogs. Every
    /// method is safe to call when Arabic is not the target language (<see cref="Active"/> is false and nothing happens).
    /// </summary>
    internal static class ArabicIntegration
    {
        /// <summary>The single cache file of earlier versions; its entries are moved into the files of the mods.</summary>
        internal const string CacheFileName = "ArabicCache.xml";
        /// <summary>Folder (next to the old file) with one cache file per mod, like SimpleCameraSetting-ar.xml.</summary>
        internal const string CacheFolderName = "ArabicCache";
        internal const string GlossaryFileName = "ArabicGlossary.txt";
        private const string OwnPackageId = "mushussubabylon.arabicautotranslation";

        /// <summary>Root folder of this mod (set by the mod constructor); the bundled glossary lives in its Arabic folder.</summary>
        internal static string ModRoot;

        private static volatile ArabicPipeline _pipeline;
        private static volatile ArabicModCache _store;
        private static volatile bool _active;

        /// <summary>True when Arabic is the target language of this session and the Arabic support started correctly.</summary>
        internal static bool Active
        {
            get { return _active; }
        }

        internal static ArabicPipeline Pipeline
        {
            get { return _pipeline; }
        }

        internal static ArabicModCache Store
        {
            get { return _store; }
        }

        /// <summary>Where the glossary was read from (empty when none was found).</summary>
        internal static string GlossarySource { get; private set; }

        /// <summary>What happened to the cache when it was loaded (shown in the settings window).</summary>
        internal static CacheReport LastReport { get; private set; }

        /// <summary>Hints for the user (another Arabic mod is active, glossary missing ...).</summary>
        internal static readonly List<string> Warnings = new List<string>();

        // ---------------------------------------------------------------------------------------- start up

        /// <summary>True when the effective target language (game language or the override setting) is Arabic.</summary>
        internal static bool IsArabicTarget()
        {
            var folder = Helpers.EffectiveLanguageFolder();
            if (string.IsNullOrWhiteSpace(folder)) return false;
            folder = folder.Trim();
            if (string.Equals(folder, "ar", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(folder.NormalizeLanguageFolder(), "Arabic", StringComparison.OrdinalIgnoreCase);
        }

        internal static DisplayOptions BuildOptions()
        {
            var o = new DisplayOptions();
            switch (Settings.ArabicRenderMode)
            {
                case 1:   // shaped letters, typing order: for a renderer that reorders but does not shape
                    o.Shape = true;
                    o.Reorder = false;
                    break;
                case 2:   // plain text: for a renderer (or another mod) that shapes and reorders by itself
                    o.Shape = false;
                    o.Reorder = false;
                    break;
                default:  // shaped and reordered: for the renderer of the game (Unity IMGUI), which does neither
                    o.Shape = true;
                    o.Reorder = true;
                    break;
            }
            o.LtrIsolates = Settings.ArabicLtrIsolates;
            return o;
        }

        /// <summary>Starts the Arabic support when Arabic is the target language. Never throws.</summary>
        internal static void Initialize()
        {
            _active = false;
            Warnings.Clear();
            LastReport = null;
            if (!IsArabicTarget())
            {
                Log.Message(ArabicAutoTranslation.LogPrefix + "Arabic support is not used: the target language is '" +
                            (Helpers.EffectiveLanguageFolder() ?? "unknown") + "'.");
                return;
            }

            try
            {
                var glossary = LoadGlossary();
                var pipeline = new ArabicPipeline(glossary, BuildOptions());
                var cacheDirectory = TranslationCacheManager.Instance.CacheDirectory;
                var store = new ArabicModCache(Path.Combine(cacheDirectory, CacheFolderName), Path.Combine(cacheDirectory, CacheFileName), ModNameOf);

                var report = store.Load(pipeline);
                if (store.LastError != null) Log.Warning(ArabicAutoTranslation.LogPrefix + "Arabic cache: " + store.LastError);
                MigrateLegacyCache(store, pipeline, report);
                if (store.IsDirty && !store.Save(pipeline))
                {
                    Log.Warning(ArabicAutoTranslation.LogPrefix + "Arabic cache could not be saved: " + store.LastError);
                }
                if (store.Note != null) Log.Message(ArabicAutoTranslation.LogPrefix + store.Note);

                _pipeline = pipeline;
                _store = store;
                LastReport = report;
                _active = true;

                Log.Message(ArabicAutoTranslation.LogPrefix + "Arabic support is active (" + DescribeMode() + "). Cache: " + report);
                DetectOtherArabicMods();
            }
            catch (Exception e)
            {
                _active = false;
                Log.Error(ArabicAutoTranslation.LogPrefix + "Arabic support could not be started; text is handled like any other language. " + e);
            }
        }

        /// <summary>The name of a loaded mod for its package id (it names the mod's cache file), or null.</summary>
        private static string ModNameOf(string packageId)
        {
            try
            {
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                {
                    if (string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) return mod.Name;
                }
            }
            catch (Exception)
            {
                // the file is then named after the package id
            }
            return null;
        }

        private static GlossaryIndex LoadGlossary()
        {
            GlossarySource = "";
            string path = null;

            // a glossary the user keeps next to the cache wins over the bundled one (it survives mod updates)
            var userPath = Path.Combine(TranslationCacheManager.Instance.CacheDirectory, GlossaryFileName);
            if (File.Exists(userPath))
            {
                path = userPath;
            }
            else if (!string.IsNullOrEmpty(ModRoot))
            {
                var bundled = Path.Combine(Path.Combine(ModRoot, "Arabic"), GlossaryFileName);
                if (File.Exists(bundled)) path = bundled;
            }

            if (path == null)
            {
                const string msg = "The Arabic glossary (Arabic/ArabicGlossary.txt) was not found: approved terms are NOT enforced.";
                Warnings.Add(msg);
                Log.Warning(ArabicAutoTranslation.LogPrefix + msg);
                return null;
            }

            var data = GlossaryParser.Parse(File.ReadAllText(path, Encoding.UTF8));
            GlossarySource = path;
            int shown = 0;
            foreach (var issue in data.Issues)
            {
                if (shown++ >= 10) break;
                Log.Warning(ArabicAutoTranslation.LogPrefix + "Arabic glossary " + issue);
            }
            Log.Message(ArabicAutoTranslation.LogPrefix + "Arabic glossary " + data.Version + ": " + data.Entries.Count + " terms from " + path);
            return new GlossaryIndex(data);
        }

        /// <summary>Adopts the entries of the old (unprocessed) cache once, if it belongs to Arabic.</summary>
        private static void MigrateLegacyCache(ArabicModCache store, ArabicPipeline pipeline, CacheReport total)
        {
            if (store.LegacyHandled) return;
            try
            {
                var path = Path.Combine(TranslationCacheManager.Instance.CacheDirectory, nameof(TranslatorManager.CachedTranslationsV2) + ".xml");
                if (File.Exists(path))
                {
                    var xml = File.ReadAllText(path, Encoding.UTF8);
                    var m = Regex.Match(xml, "<Cache\\b[^>]*\\bLanguage=\"([^\"]*)\"");
                    var language = m.Success ? m.Groups[1].Value : "";
                    var legacy = TranslationCacheManager.ParseCacheXml(xml);
                    // the file says which game language was active when it was written; the translation target may have
                    // been another one (language override), so a file of another language is judged by its content
                    bool arabic = language.IndexOf("Arabic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  language.IndexOf("\u0627\u0644\u0639\u0631\u0628\u064A\u0629", StringComparison.Ordinal) >= 0 ||
                                  ArabicCacheMaintenance.MostlyArabic(legacy.Values);
                    if (arabic)
                    {
                        var r = ArabicCacheMaintenance.MigrateLegacy(store, pipeline, legacy);
                        total.LegacyAdopted += r.LegacyAdopted;
                        total.LegacyDropped += r.LegacyDropped;
                        Log.Message(ArabicAutoTranslation.LogPrefix + "Old translation cache: " + r.LegacyAdopted + " entries processed and adopted, " +
                                    r.LegacyDropped + " entries will be translated again (glossary terms or invalid). The old file was not changed.");
                    }
                    else
                    {
                        Log.Message(ArabicAutoTranslation.LogPrefix + "Old translation cache belongs to '" + language + "': not used for Arabic.");
                    }
                }
                store.LegacyHandled = true;
                store.MarkDirty();
            }
            catch (Exception e)
            {
                Log.Warning(ArabicAutoTranslation.LogPrefix + "The old translation cache could not be migrated: " + e.Message);
            }
        }

        private static readonly Regex OtherArabicMod = new Regex(@"rtl|bidi|reshap|shaping|arabic\s*(support|fix|text|font|render)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A mod that shapes Arabic at render time would shape the already processed text a second time.</summary>
        private static void DetectOtherArabicMods()
        {
            try
            {
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                {
                    var id = mod.PackageId ?? "";
                    if (id.StartsWith(OwnPackageId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (OtherArabicMod.IsMatch(id + " " + (mod.Name ?? "")))
                    {
                        var msg = "Another mod that may shape Arabic text is active: " + mod.Name + " (" + id + "). " +
                                  "If it shapes text while it is drawn, set the Arabic render mode to \"Logical text\" to avoid shaping twice.";
                        Warnings.Add(msg);
                        Log.Warning(ArabicAutoTranslation.LogPrefix + msg);
                    }
                }
            }
            catch (Exception)
            {
                // a hint only
            }
        }

        internal static string DescribeMode()
        {
            switch (Settings.ArabicRenderMode)
            {
                case 1: return "shaped text in typing order";
                case 2: return "plain logical text";
                default: return "shaped and reordered for the game renderer";
            }
        }

        /// <summary>Copies the display string of every valid entry into the live lookup table.</summary>
        internal static void FillDictionary(ConcurrentDictionary<string, string> target)
        {
            var store = _store;
            if (store == null) return;
            foreach (var e in store.Snapshot()) target[e.Key] = e.Display;
        }

        // ---------------------------------------------------------------------------------------- translating

        internal static ArabicJob CreateJob(string cacheKey, string source, string modPackageId, string glossaryKey, string glossaryHint)
        {
            return new ArabicJob
            {
                CacheKey = cacheKey,
                Source = source,
                Context = new GlossaryContext(modPackageId, glossaryKey, glossaryHint)
            };
        }

        /// <summary>
        /// Runs the pipeline for a job on the calling (worker) thread. On success <paramref name="display"/> is the final
        /// display string; on failure it is the original text and nothing is cached.
        /// </summary>
        internal static bool Translate(ArabicJob job, ITranslator translator, out string display)
        {
            var pipeline = _pipeline;
            job.Pipeline = pipeline;

            Func<string, string> chunk = delegate (string text)
            {
                // the translators set this when they got an answer but could not give the placeholders back
                Helpers.PlaceholderRestoreFailed = false;
                string t;
                if (!translator.TryTranslate(text, out t) || t == null) return null;
                return TranslatorManager.PolishText(t);
            };
            Func<bool> placeholdersLost = delegate { return Helpers.PlaceholderRestoreFailed; };

            // an answer that comes back without its placeholders is not used either: the text is translated piece by piece
            var res = pipeline.TranslateText(job.Source, job.Context, chunk, placeholdersLost, true);
            job.Result = res;
            LogPlaceholderFallback(job.Source, res);
            if (!res.Ok)
            {
                Log.WarningOnce(ArabicAutoTranslation.LogPrefix + "Arabic: \"" + Shorten(job.Source) + "\" was not translated: " + res.Problem,
                    ("arabic:" + job.CacheKey).GetHashCode());
                display = job.Source;
                return false;
            }
            display = res.Display;
            return true;
        }

        private static string Shorten(string s)
        {
            return Shorten(s, 60);
        }

        private static string Shorten(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        private static int _placeholderFallbacksLogged;

        /// <summary>
        /// For the first few texts whose translator answer came back without its placeholders (so the text was translated
        /// piece by piece): says what the answer was, which shows what the translator did to the placeholders.
        /// </summary>
        private static void LogPlaceholderFallback(string source, PipelineResult res)
        {
            var failed = res.FallbackFrom;
            if (failed == null || !failed.PlaceholderProblem) return;
            var n = System.Threading.Interlocked.Increment(ref _placeholderFallbacksLogged);
            if (n > 3) return;
            Log.Warning(ArabicAutoTranslation.LogPrefix + "Arabic: the translator's answer for \"" + Shorten(source, 160) +
                        "\" did not keep the placeholders (" + failed.Problem + "). It answered: \"" + Shorten(failed.Answer, 160) +
                        "\". " + (res.Ok ? "The text was translated piece by piece instead." : "Translating it piece by piece did not work either: " + res.Problem + ".") +
                        (n == 3 ? " (further cases are not logged)" : ""));
        }

        /// <summary>Stores the result of a successful job (called from the completion callback).</summary>
        internal static void Commit(ArabicJob job)
        {
            var store = _store;
            if (store == null || job == null || job.Result == null || !job.Result.Ok) return;
            store.Put(ArabicCacheEntry.From(job.CacheKey, job.Source, job.Context, job.Result, job.Pipeline ?? _pipeline));
        }

        /// <summary>Writes the cache file when something changed.</summary>
        internal static void SaveIfDirty()
        {
            var store = _store;
            var pipeline = _pipeline;
            if (!_active || store == null || pipeline == null || !store.IsDirty) return;
            if (!store.Save(pipeline)) Log.Warning(ArabicAutoTranslation.LogPrefix + "Arabic cache could not be saved: " + store.LastError);
        }

        // ---------------------------------------------------------------------------------------- editor / dialogs

        /// <summary>The plain Arabic text of a cache entry (what the user edits and what is shared), or null.</summary>
        internal static string LogicalOf(string key)
        {
            var store = _store;
            ArabicCacheEntry e;
            if (_active && store != null && store.TryGet(key, out e)) return e.Logical;
            return null;
        }

        /// <summary>
        /// Stores a translation the user typed or imported (plain Arabic text). It goes through the pipeline like any other
        /// translation; <paramref name="display"/> is the display string to inject. False with an explanation when the text
        /// is not acceptable (for example a placeholder was invented or the text is already shaped).
        /// </summary>
        internal static bool SetUserText(string key, string text, out string display, out string error)
        {
            display = null;
            error = null;
            var store = _store;
            var pipeline = _pipeline;
            if (!_active || store == null || pipeline == null)
            {
                error = "Arabic support is not active.";
                return false;
            }

            string source;
            GlossaryContext ctx;
            bool approximate;
            ArabicCacheEntry existing;
            if (store.TryGet(key, out existing))
            {
                source = existing.Source;
                ctx = existing.ToGlossaryContext();
                approximate = existing.Origin == PipelineOrigin.Migrated || existing.Origin == PipelineOrigin.UserEdit;
            }
            else
            {
                int colon = key.IndexOf(':');
                string modId = colon > 0 ? key.Substring(0, colon) : "";
                if (modId == ArabicCacheMaintenance.LegacyModId) modId = "";
                source = colon >= 0 ? key.Substring(colon + 1) : key;
                ctx = new GlossaryContext(modId, "", "");
                approximate = true;
            }

            var res = pipeline.FromUserText(source, text, ctx, approximate);
            if (!res.Ok)
            {
                error = res.Problem;
                return false;
            }
            store.Put(ArabicCacheEntry.From(key, source, ctx, res, pipeline));
            display = res.Display;
            return true;
        }

        internal static void Remove(string key)
        {
            var store = _store;
            if (_active && store != null) store.Remove(key);
        }

        internal static void ClearAll()
        {
            var store = _store;
            var pipeline = _pipeline;
            if (!_active || store == null || pipeline == null) return;
            store.Clear();
            store.Save(pipeline);
        }

        /// <summary>The cache as XML in the format the share (Gist) feature uses, with the plain text of every entry.</summary>
        internal static string SerializeForSharing(out int count)
        {
            count = 0;
            var doc = new XmlDocument();
            var root = doc.CreateElement("Cache");
            doc.AppendChild(root);
            root.SetAttribute("Language", LanguageDatabase.activeLanguage != null ? LanguageDatabase.activeLanguage.FriendlyNameEnglish : "Arabic");

            var store = _store;
            if (store == null) return doc.OuterXml;
            var entries = store.Snapshot();
            entries.Sort(delegate (ArabicCacheEntry a, ArabicCacheEntry b) { return string.CompareOrdinal(a.Key, b.Key); });
            foreach (var e in entries)
            {
                var el = doc.CreateElement("Entry");
                el.SetAttribute("Key", Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Key)));
                el.InnerText = e.Logical;
                root.AppendChild(el);
                count++;
            }
            return doc.OuterXml;
        }

        // ---------------------------------------------------------------------------------------- options

        /// <summary>
        /// Rebuilds the display form of every entry after the render mode or the isolate option changed. Returns the
        /// (key, display string) pairs whose display string changed so that the caller can re-inject them into the game.
        /// </summary>
        internal static List<KeyValuePair<string, string>> ApplyOptionChange()
        {
            var changed = new List<KeyValuePair<string, string>>();
            var store = _store;
            var old = _pipeline;
            if (!_active || store == null || old == null) return changed;

            var fresh = new ArabicPipeline(old.Glossary, BuildOptions());
            if (string.Equals(fresh.DisplayVersion, old.DisplayVersion, StringComparison.Ordinal)) return changed;
            _pipeline = fresh;

            var report = new CacheReport();
            foreach (var e in store.Snapshot())
            {
                var kept = ArabicCacheMaintenance.Check(e, fresh, report);
                if (kept == null)
                {
                    store.Remove(e.Key);
                    string ignored;
                    TranslatorManager.CachedTranslationsV2.TryRemove(e.Key, out ignored);
                    TranslationCacheManager.Remove(e.Key);
                }
                else if (!ReferenceEquals(kept, e))
                {
                    store.Put(kept);
                    TranslatorManager.CachedTranslationsV2[kept.Key] = kept.Display;
                    TranslationCacheManager.AddOrUpdate(kept.Key, kept.Display);
                    changed.Add(new KeyValuePair<string, string>(kept.Key, kept.Display));
                }
            }
            Log.Message(ArabicAutoTranslation.LogPrefix + "Arabic render mode changed (" + DescribeMode() + "): " + report);
            return changed;
        }

        private static string _previewInput;
        private static string _previewOutput = "";

        /// <summary>How a text typed in the editor will look in the game (main thread only; the last answer is remembered).</summary>
        internal static string Preview(string logical)
        {
            var pipeline = _pipeline;
            if (!_active || pipeline == null || string.IsNullOrEmpty(logical)) return "";
            if (string.Equals(logical, _previewInput, StringComparison.Ordinal)) return _previewOutput;
            string output;
            try
            {
                output = pipeline.Preview(logical) ?? "";
            }
            catch (Exception)
            {
                output = "";
            }
            _previewInput = logical;
            _previewOutput = output;
            return output;
        }

        /// <summary>One line for the settings window.</summary>
        internal static string StatusLine()
        {
            var store = _store;
            var pipeline = _pipeline;
            if (!_active || store == null || pipeline == null) return "";
            var terms = pipeline.Glossary == null ? 0 : pipeline.Glossary.Data.Entries.Count;
            return store.Count + " cached, glossary " + terms + " terms (" + pipeline.GlossaryVersion + "), " + DescribeMode();
        }
    }
}
