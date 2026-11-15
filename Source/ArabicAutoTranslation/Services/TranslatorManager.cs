using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArabicAutoTranslation.Translators;
using ArabicAutoTranslation.Utilities;
using RimWorld;
using Verse;
using ArabicIntegration = ArabicAutoTranslation.Arabic.ArabicIntegration;
using ArabicJob = ArabicAutoTranslation.Arabic.ArabicJob;

namespace ArabicAutoTranslation.Services
{
    /// <summary>
    /// One queued translation: the original text, the completion callback and - when the target language is Arabic -
    /// the job that carries the glossary context.
    /// </summary>
    internal sealed class TranslationWork
    {
        /// <summary>The original text (what the translator receives).</summary>
        internal readonly string Key;
        /// <summary>Completion callback: (translated text, success).</summary>
        internal readonly Action<string, bool> Value;
        /// <summary>Set in Arabic mode only.</summary>
        internal readonly ArabicJob Arabic;

        internal TranslationWork(string key, Action<string, bool> value, ArabicJob arabic)
        {
            Key = key;
            Value = value;
            Arabic = arabic;
        }
    }

    public static class TranslatorManager
    {
        public static readonly ConcurrentDictionary<string, string> CachedTranslationsV2 = new ConcurrentDictionary<string, string>();
        public static ITranslator CurrentTranslator;
        public static bool Ready;
        public static bool Working;

        // <(original, normalized), callback>
        internal static readonly ConcurrentQueue<TranslationWork> _queue = new ConcurrentQueue<TranslationWork>();
        internal static readonly List<ITranslator> translators = new List<ITranslator>();
        internal static int workCnt;
        internal static int _cacheCount;

        private static readonly ConcurrentDictionary<string, byte> _inQueue = new ConcurrentDictionary<string, byte>();
        private static readonly ConcurrentDictionary<string, int> _retryCount = new ConcurrentDictionary<string, int>();
        private const int MAX_RETRIES = 3; // Maximum number of times to retry a failed translation
        private static Task _translationThread;
        private static Timer _cacheSaver;
        private static SemaphoreSlim _concurrencyLimiter;
        private static int _currentMaxConcurrency;

        /// <summary>
        /// User's MaxConcurrency clamped by the current translator's own cap (if any).
        /// Claude Code caps this at 2: each request spawns a heavyweight CLI process.
        /// </summary>
        private static int EffectiveMaxConcurrency
        {
            get
            {
                var limit = CurrentTranslator?.MaxConcurrentRequests ?? 0;
                return limit > 0 ? Math.Min(Settings.MaxConcurrency, limit) : Settings.MaxConcurrency;
            }
        }

        public static void Prepare()
        {
            translators.Clear();
            var translatorTypes = GenTypes.AllTypes
                .Where(x => !x.IsAbstract && !x.IsInterface &&  x.GetInterface(nameof(ITranslator)) != null).ToList();
            
            foreach (var translatorType in translatorTypes)
            {
                var t = (ITranslator)Activator.CreateInstance(translatorType);
                
                // Inject settings if available
                if (Settings.TranslatorSettings.TryGetValue(t.Name, out var savedSettings))
                {
                    t.Settings = savedSettings;
                }
                
                t.Prepare();
                
                // If the translator created default settings, save them back
                if (t.Settings != null)
                {
                    Settings.TranslatorSettings[t.Name] = t.Settings;
                }

                translators.Add(t);
            }
            
            CurrentTranslator = GetTranslator(Settings.TranslatorName);
            if (CurrentTranslator?.Ready == false) CurrentTranslator = null;

            if (CurrentTranslator == null)
            {
                CurrentTranslator = translators.FirstOrDefault(x => x.Ready);
                if (CurrentTranslator != null)
                {
                     Log.Error(ArabicAutoTranslation.LogPrefix +
                            $"Selected translator named {Settings.TranslatorName} is not ready, changing to other translator.. {CurrentTranslator?.Name}");
                     Settings.TranslatorName = CurrentTranslator.Name;
                }
            }
            Log.Message(ArabicAutoTranslation.LogPrefix + $"List of translators: {translators.Select(x => x.Name).ToCommaList()}, Current translator: {CurrentTranslator?.Name}");
            Ready = CurrentTranslator != null;

            if (!Settings.EnableAutoTranslation)
            {
                // Cache-only mode: injected translations still come from the cache,
                // but nothing is queued and no network request is ever made
                Ready = false;
                Log.Message(ArabicAutoTranslation.LogPrefix + "Auto translation is disabled - using cached translations only.");
            }

            // Arabic keeps its own cache (final display strings + the plain text they were made from); every other
            // language keeps using the original cache file exactly as before
            ArabicIntegration.Initialize();
            if (ArabicIntegration.Active)
            {
                ArabicIntegration.FillDictionary(CachedTranslationsV2);
            }
            else
            {
                TranslationCacheManager.Load(nameof(CachedTranslationsV2));
                foreach (var pair in TranslationCacheManager.GetAll())
                {
                    CachedTranslationsV2[pair.Key] = pair.Value;
                }
            }

            _cacheCount = CachedTranslationsV2.Count;

        }

        /// <summary>
        /// Creates a cache key from the original text and optional additional key.
        /// Since we now use Base64 encoding for XML storage, we can keep the original text intact.
        /// </summary>
        internal static string NormalizeKey(string text)
        {
            if (text.NullOrEmpty())
            {
                return text;
            }

            // Simple normalization: trim whitespace and normalize line endings
            // Keep the original text as much as possible for better debugging
            text = text.Trim();
            text = text.Replace("\r\n", "\n");  // Normalize Windows line endings
            text = text.Replace('\r', '\n');    // Normalize old Mac line endings
            
            // No need to append length - the full text is already unique
            return text;
        }

        internal static string PolishText(string text)
        {
            // JSON-layer parsing already unescapes proper responses; this pass only cleans up
            // stray escapes some translators leak. A literal backslash in a translation would
            // make Regex.Unescape throw, so fall back to the raw text instead of failing the item.
            try
            {
                return Regex.Unescape(Regex.Replace(text, "\\[Uu]([0-9A-Fa-f]{4})",
                        m => char.ToString((char)ushort.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier)))
                    .Replace("\\\"", "\"")).Trim();
            }
            catch (ArgumentException)
            {
                return text.Trim();
            }
        }

        private static bool _networkPauseNotified;

        private static void NotifyNetworkStateIfChanged()
        {
            var isOpen = NetworkStateMonitor.IsOpen;
            if (isOpen == _networkPauseNotified) return;
            _networkPauseNotified = isOpen;
            // Messages must be shown from the main thread
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (isOpen)
                    Messages.Message("AT_Message_NetworkPaused".Translate(), MessageTypeDefOf.NegativeEvent);
                else
                    Messages.Message("AT_Message_NetworkResumed".Translate(), MessageTypeDefOf.PositiveEvent);
            });
        }

        public static void StartThread()
        {
            if (!Settings.EnableAutoTranslation)
            {
                Log.Message(ArabicAutoTranslation.LogPrefix + "Auto translation is disabled - translation thread not started.");
                return;
            }

            if (CurrentTranslator == null)
            {
                Log.Error(ArabicAutoTranslation.LogPrefix + $"::Critical Error:: CurrentTranslator was null. Couldn't get any available Translator within {translators.Select(x => x.Name).ToCommaList()}");
                return;
            }

            Working = true;
            _concurrencyLimiter = new SemaphoreSlim(EffectiveMaxConcurrency);
            _currentMaxConcurrency = EffectiveMaxConcurrency;

            _translationThread = Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    if (!Working)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    // Dynamically update concurrency limit if settings or translator changed
                    if (_currentMaxConcurrency != EffectiveMaxConcurrency)
                    {
                        _concurrencyLimiter?.Dispose();
                        _concurrencyLimiter = new SemaphoreSlim(EffectiveMaxConcurrency);
                        _currentMaxConcurrency = EffectiveMaxConcurrency;
                        Log.Message(ArabicAutoTranslation.LogPrefix + $"Updated concurrency limit to {_currentMaxConcurrency}");
                    }
                    
                    if (_queue.Count > 0)
                    {
                        NotifyNetworkStateIfChanged();

                        if (NetworkStateMonitor.IsOpen)
                        {
                            // Circuit open: requests are paused. Periodically let ONE item
                            // through as a probe; its outcome closes or keeps the circuit.
                            if (NetworkStateMonitor.TryEnterProbe() && _queue.TryDequeue(out var probeItem))
                            {
                                _inQueue.TryRemove(probeItem.Key, out _);
                                var original = probeItem;
                                var wrapped = new TranslationWork(original.Key, (t, s) =>
                                {
                                    if (!s && NetworkStateMonitor.IsOpen)
                                    {
                                        // Probe failed due to network: put the item back, don't drop it
                                        _queue.Enqueue(original);
                                        _inQueue.TryAdd(original.Key, 0);
                                        return;
                                    }
                                    original.Value(t, s);
                                }, original.Arabic);
                                ProcessIndividualTranslation(wrapped);
                            }
                            else
                            {
                                await Task.Delay(1000);
                            }
                            continue;
                        }

                        // Check if batch translation is supported and enabled
                        // (Arabic is translated one text at a time: every text needs its own glossary markers and validation)
                        bool useBatch = CurrentTranslator?.SupportsBatchTranslation == true &&
                                       !ArabicIntegration.Active &&
                                       _queue.Count > 1; // Only batch if multiple items

                        if (useBatch)
                        {
                            // Batch translation mode
                            await ProcessBatchTranslation(CurrentTranslator.BatchSizeTokens);
                        }
                        else
                        {
                            // Individual translation mode (original logic)
                            await _concurrencyLimiter.WaitAsync();
                            
                            if (_queue.TryDequeue(out var pair))
                            {
                                _inQueue.TryRemove(pair.Key, out _);
                                
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        ProcessIndividualTranslation(pair);
                                    }
                                    finally
                                    {
                                        _concurrencyLimiter.Release();
                                    }
                                });
                            }
                            else
                            {
                                _concurrencyLimiter.Release();
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(500);
                    }
                }
            }, TaskCreationOptions.LongRunning).Unwrap().ContinueWith(t =>
            {
                Log.Warning($"Translation thread was killed! {t.Exception?.Message}");
            });

            _cacheSaver = new Timer(state =>
            {
                if (!Working) return;
                try
                {
                    if (ArabicIntegration.Active)
                    {
                        // the Arabic cache is written by its own store
                        ArabicIntegration.SaveIfDirty();
                        _cacheCount = CachedTranslationsV2.Count;
                    }
                    else
                    {
                        // Sync local cache to manager
                        foreach (var pair in CachedTranslationsV2)
                        {
                            TranslationCacheManager.AddOrUpdate(pair.Key, pair.Value);
                        }

                        if (TranslationCacheManager.IsDirty || _cacheCount != CachedTranslationsV2.Count)
                        {
                            TranslationCacheManager.Save(nameof(CachedTranslationsV2));
                        
                            if (_cacheCount != CachedTranslationsV2.Count)
                            {
                                Log.Message(ArabicAutoTranslation.LogPrefix +
                                            $"Translation cache saved to your disk. translated: {CachedTranslationsV2.Count}");
                                _cacheCount = CachedTranslationsV2.Count;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Message($"ERROR: {e.Message}");
                }

                if (Settings.UsageDirty)
                {
                    Settings.UsageDirty = false;
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        try
                        {
                            LoadedModManager.GetMod<ArabicAutoTranslation>()?.WriteSettings();
                        }
                        catch (Exception e)
                        {
                            Log.Warning(ArabicAutoTranslation.LogPrefix + $"Failed to persist usage counters: {e.Message}");
                        }
                    });
                }
            }, null, 0, 60000);
        }

        public static void Translate(string orig, Action<string> callBack) => Translate(orig, string.Empty, string.Empty, callBack);

        public static void Translate(string orig, string additionalKey, Action<string> callBack) => Translate(orig, additionalKey, string.Empty, callBack);

        /// <summary>
        /// Same as the overload below for callers that do not need to know whether the text really was translated.
        /// </summary>
        public static void Translate(string orig, string additionalKey, string modPackageId, Action<string> callBack,
            string glossaryKey = null, string glossaryHint = null)
        {
            Translate(orig, additionalKey, modPackageId, (text, isTranslated) => callBack(text), glossaryKey, glossaryHint);
        }

        /// <summary>
        /// Translates a text (or answers from the cache). The callback receives the text and whether it really is a translation
        /// (a cache hit or a successful request); when the request failed, or translating is switched off, it receives the
        /// original text and false, so callers must not count it as translated.
        /// <paramref name="glossaryKey"/> / <paramref name="glossaryHint"/> tell the
        /// Arabic glossary where the text comes from (def type and field, grammatical hint); they are ignored for other languages
        /// and are not part of the cache key.
        /// </summary>
        public static void Translate(string orig, string additionalKey, string modPackageId, Action<string, bool> callBack,
            string glossaryKey = null, string glossaryHint = null)
        {
            if (string.IsNullOrEmpty(orig))
            {
                callBack(orig, true);
                return;
            }
            
            // Include ModPackageId in the key for proper grouping
            var keyPrefix = string.IsNullOrEmpty(modPackageId) ? $"{TranslationCacheManager.LEGACY_MOD_ID}:" : $"{modPackageId}:";
            var normalizedText = NormalizeKey(orig + additionalKey);
            var key = keyPrefix + normalizedText;
            
            // Check cache first (local dict is synced on load)
            if (CachedTranslationsV2.TryGetValue(key, out var translation))
            {
                callBack(Prefs.DevMode && Settings.AppendTranslationCompleteTag && orig != translation ? "::TEST::" + translation : translation, true);
                return;
            }
            
            // Backward compatibility: check for old cache entries without ModId
            // If found, return original text to trigger re-translation with proper ModId
            if (!string.IsNullOrEmpty(keyPrefix) && CachedTranslationsV2.ContainsKey(normalizedText))
            {
                // Old entry exists - will be cleaned up on next save
                // Don't use it, let it re-translate with ModId
            }

            // Covers both startup-disabled (Ready=false) and a runtime toggle-off:
            // cache misses return the original text and are never queued
            if (!Ready || !Settings.EnableAutoTranslation)
            {
                callBack(orig, false);
                return;
            }

            if (_inQueue.ContainsKey(key))
                return;
            _inQueue[key] = 0; // dummy value

            // Arabic: the job carries the context of the text; the finished entry is stored by the Arabic cache
            var arabicJob = ArabicIntegration.Active
                ? ArabicIntegration.CreateJob(key, orig, modPackageId, glossaryKey, glossaryHint)
                : null;

            _queue.Enqueue(new TranslationWork(orig, (t, s) =>
            {
                if (s)
                {
                    CachedTranslationsV2[key] = t;
                    if (arabicJob != null)
                    {
                        ArabicIntegration.Commit(arabicJob);
                    }
                    else
                    {
                        // Also update manager immediately? Or wait for timer?
                        // Timer syncs every minute, might be safer to update immediately for robustness
                        TranslationCacheManager.AddOrUpdate(key, t);
                    }
                }
                callBack(s && Prefs.DevMode && Settings.AppendTranslationCompleteTag && orig != t ? "::TEST::" + t : t, s);
            }, arabicJob));
        }

        /// <summary>
        /// The text the translation editor and the merge dialogs work with for a cache entry. For Arabic that is the plain
        /// Arabic text (never the shaped display string); for every other language it is the cached value itself.
        /// </summary>
        internal static string EditableTextOf(string key, string cachedValue)
        {
            if (ArabicIntegration.Active)
            {
                var logical = ArabicIntegration.LogicalOf(key);
                if (logical != null) return logical;
            }
            return cachedValue;
        }

        /// <summary>
        /// Stores a translation typed by the user or imported from a shared cache. <paramref name="stored"/> is the value
        /// that has to be injected into the game (for Arabic the processed display string). False with a reason when an
        /// Arabic text is not acceptable; nothing is changed then.
        /// </summary>
        internal static bool StoreEditedText(string key, string text, out string stored, out string error)
        {
            error = null;
            if (ArabicIntegration.Active)
            {
                if (!ArabicIntegration.SetUserText(key, text, out stored, out error)) return false;
                CachedTranslationsV2[key] = stored;
                TranslationCacheManager.AddOrUpdate(key, stored);   // the editor's list mirrors the display strings
                return true;
            }

            TranslationCacheManager.AddOrUpdate(key, text);
            CachedTranslationsV2[key] = text;
            stored = text;
            return true;
        }

        /// <summary>Removes a cached translation (memory and disk caches).</summary>
        internal static void RemoveCached(string key)
        {
            TranslationCacheManager.Remove(key);
            CachedTranslationsV2.TryRemove(key, out _);
            ArabicIntegration.Remove(key);
        }

        public static ITranslator GetTranslator(string name)
        {
            return translators.FirstOrDefault(x => x.Name == name);
        }

        #region Blocked-engine fallback

        // A keyless web engine can start answering every request with HTTP 401/403 (blocked network or region, anti-bot rule).
        // All texts would then fail for the whole session, so after a few refusals in a row the session continues with Google.
        // The selected engine (Settings.TranslatorName) is not changed: it is tried again at the next start.
        private const int ACCESS_DENIED_LIMIT = 3;
        private const string FALLBACK_ENGINE = "Google";
        private static int _accessDeniedStreak;
        private static readonly object _fallbackLock = new object();

        /// <summary>Name of the engine that was replaced because it refused the requests; null while the selected one is in use.</summary>
        internal static string FallbackFrom { get; private set; }

        /// <summary>A translator reports that the server refused one of its requests (access denied).</summary>
        internal static void ReportAccessDenied(ITranslator translator)
        {
            if (translator == null || !Settings.AutoFallback || !ReferenceEquals(translator, CurrentTranslator)) return;
            if (Interlocked.Increment(ref _accessDeniedStreak) >= ACCESS_DENIED_LIMIT) EngageFallback(translator);
        }

        /// <summary>A translator reports that the server accepted one of its requests.</summary>
        internal static void ReportAccessOk(ITranslator translator)
        {
            if (ReferenceEquals(translator, CurrentTranslator)) Interlocked.Exchange(ref _accessDeniedStreak, 0);
        }

        /// <summary>Forgets a fallback (the player restarted the translation work with the selected engine).</summary>
        internal static void ResetFallback()
        {
            FallbackFrom = null;
            Interlocked.Exchange(ref _accessDeniedStreak, 0);
        }

        private static void EngageFallback(ITranslator failed)
        {
            ITranslator replacement;
            lock (_fallbackLock)
            {
                // another thread may have been faster
                if (!ReferenceEquals(failed, CurrentTranslator)) return;

                replacement = GetTranslator(FALLBACK_ENGINE);
                if (replacement == null || !replacement.Ready || ReferenceEquals(replacement, failed)) return;

                var traditional = replacement as Translator_BaseTraditional;
                if (traditional != null && !traditional.SupportsCurrentLanguage()) return;

                CurrentTranslator = replacement;
                FallbackFrom = failed.Name;
                Interlocked.Exchange(ref _accessDeniedStreak, 0);
            }

            // Only logged here: this runs on the translation thread, where in-game messages must not be created.
            // The translator tab of the settings shows the switch (FallbackFrom).
            Log.Warning(ArabicAutoTranslation.LogPrefix + $"{failed.Name} keeps refusing the requests (access denied). Using {replacement.Name} for the rest of this session; {failed.Name} stays selected and is tried again at the next start.");
        }

        #endregion

        private static async Task ProcessBatchTranslation(int targetTokens)
        {
            await _concurrencyLimiter.WaitAsync();
            
            _ = Task.Run(() =>
            {
                try
                {
                    // Collect items for batch
                    var batchItems = new List<TranslationWork>();
                    int estimatedTokens = 0;
                    var startTime = DateTime.UtcNow;
                    
                    // Collect items up to target tokens or timeout
                    while (estimatedTokens < targetTokens && 
                           (DateTime.UtcNow - startTime).TotalMilliseconds < 500 &&
                           batchItems.Count < 100) // Max 100 items per batch for safety
                    {
                        if (_queue.TryDequeue(out var item))
                        {
                            batchItems.Add(item);
                            _inQueue.TryRemove(item.Key, out _);
                            
                            // Estimate tokens (rough: 1 token ≈ 4 characters for English)
                            estimatedTokens += item.Key.Length / 4;
                        }
                        else
                        {
                            // Queue empty, wait a bit for more items
                            if (batchItems.Count == 0)
                            {
                                break; // Nothing to process
                            }
                            Thread.Sleep(50);
                        }
                    }
                    
                    if (batchItems.Count == 0)
                    {
                        return; // Nothing collected
                    }
                    
                    // If only one item, use individual translation
                    if (batchItems.Count == 1)
                    {
                        var singleItem = batchItems[0];
                        ProcessIndividualTranslation(singleItem);
                        return;
                    }
                    
                    // Prepare batch
                    var texts = batchItems.Select(x => x.Key).ToList();
                    var callbacks = batchItems.Select(x => x.Value).ToList();
                    
                    // Execute batch translation
                    bool success = CurrentTranslator.TryTranslateBatch(texts, out var translatedTexts);
                    
                    if (success && translatedTexts != null && translatedTexts.Count == texts.Count)
                    {
                        // Batch succeeded - process results
                        for (int i = 0; i < texts.Count; i++)
                        {
                            var translated = PolishText(translatedTexts[i]);
                            _retryCount.TryRemove(texts[i], out _);
                            Interlocked.Increment(ref workCnt);
                            callbacks[i](translated, true);
                        }
                        
                        Log.Message(ArabicAutoTranslation.LogPrefix + 
                            $"Batch translation completed: {texts.Count} items in one request");
                    }
                    else
                    {
                        // Batch failed - requeue (they may be batched again), bounded by the
                        // retry budget so one permanently failing item can't loop the whole batch
                        Log.Warning(ArabicAutoTranslation.LogPrefix +
                            $"Batch translation failed, requeueing {batchItems.Count} items");

                        foreach (var item in batchItems)
                        {
                            // Network down: don't consume the retry budget (queue is paused anyway)
                            if (!NetworkStateMonitor.IsOpen &&
                                _retryCount.AddOrUpdate(item.Key, 1, (k, v) => v + 1) > MAX_RETRIES)
                            {
                                _retryCount.TryRemove(item.Key, out _);
                                item.Value(item.Key, false);
                                continue;
                            }
                            _queue.Enqueue(item);
                            _inQueue.TryAdd(item.Key, 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"{ArabicAutoTranslation.LogPrefix} Batch translation error: {ex.Message}");
                }
                finally
                {
                    _concurrencyLimiter.Release();
                }
            });
        }
        
        private static void ProcessIndividualTranslation(TranslationWork pair)
        {
            try
            {
                var translated = string.Empty;
                var success = true;

                if (pair.Arabic != null)
                {
                    // Arabic: the pipeline sends the text to the provider (piece by piece, glossary terms protected),
                    // enforces the glossary, applies grammar / pronoun rules and produces the final display string
                    success = ArabicIntegration.Translate(pair.Arabic, CurrentTranslator, out translated);
                    if (success) _retryCount.TryRemove(pair.Key, out _);
                }
                else
                {
                    // Batch/Tokenize logic for long text
                    if (pair.Key.Length > 200)
                    {
                        translated = pair.Key.Tokenize().Aggregate(translated, (current, token) =>
                        {
                            success &= CurrentTranslator.TryTranslate(token, out var tmp);
                            return current + ' ' + tmp;
                        });
                    }
                    else
                    {
                        success = CurrentTranslator.TryTranslate(pair.Key, out translated);
                    }

                    if (success)
                    {
                        translated = PolishText(translated);
                        _retryCount.TryRemove(pair.Key, out _);
                    }
                }
                Interlocked.Increment(ref workCnt);
                pair.Value(translated, success);
            }
            catch (Exception ex)
            {
                HandleTranslationError(pair, ex);
            }
        }

        private static void HandleTranslationError(TranslationWork pair, Exception ex)
        {
            // Check if this is a network timeout/error that we can retry
            bool shouldRetry = ex is WebException webEx && NetworkHelper.IsConnectionLevelFailure(webEx);

            if (shouldRetry && NetworkStateMonitor.IsOpen)
            {
                // Network is down: requeue without consuming the retry budget.
                // The queue loop is paused, so this does not spin.
                _queue.Enqueue(pair);
                _inQueue.TryAdd(pair.Key, 0);
                return;
            }

            if (shouldRetry)
            {
                var currentRetries = _retryCount.AddOrUpdate(pair.Key, 1, (k, v) => v + 1);
                
                if (currentRetries <= MAX_RETRIES)
                {
                    // Re-add to queue for retry
                    Log.Warning($"{ArabicAutoTranslation.LogPrefix} Translation failed (timeout/network error), adding back to queue (retry {currentRetries}/{MAX_RETRIES}): {pair.Key.Substring(0, Math.Min(50, pair.Key.Length))}...");
                    _queue.Enqueue(pair);
                    _inQueue.TryAdd(pair.Key, 0);
                }
                else
                {
                    // Max retries exceeded
                    Log.Error($"{ArabicAutoTranslation.LogPrefix} Translation failed after {MAX_RETRIES} retries: {ex.Message}");
                    _retryCount.TryRemove(pair.Key, out _);
                    pair.Value(pair.Key, false);
                }
            }
            else
            {
                // Non-retryable error
                Log.Error($"{ArabicAutoTranslation.LogPrefix} Translation failed with non-retryable error: {ex.Message}");
                _retryCount.TryRemove(pair.Key, out _);
                pair.Value(pair.Key, false);
            }
        }

        public static void ClearQueue()
        {
            Working = false;
            workCnt = 0;

            while (_queue.Count > 0)
            {
                _queue.TryDequeue(out var pair);
            }
            _inQueue.Clear();

            Working = true;
        }
    }
}

