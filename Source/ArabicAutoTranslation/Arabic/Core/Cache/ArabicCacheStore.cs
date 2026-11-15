using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// The persistent Arabic translation cache: a thread-safe map from cache key to <see cref="ArabicCacheEntry"/> and its
    /// file. Entries are replaced as a whole and never changed in place, so a reader sees either the old or the new entry,
    /// never a mixture; a failed translation simply never reaches <see cref="Put"/>. The file is written to a temporary file
    /// first and swapped in (the previous generation stays as .bak), so a crash cannot leave half a cache behind.
    /// </summary>
    internal sealed class ArabicCacheStore
    {
        private readonly ConcurrentDictionary<string, ArabicCacheEntry> _entries =
            new ConcurrentDictionary<string, ArabicCacheEntry>(StringComparer.Ordinal);
        private readonly object _fileLock = new object();
        private long _version;
        private long _savedVersion;

        /// <summary>Path of the cache file (null: memory only, used by the tests).</summary>
        internal string FilePath { get; private set; }

        /// <summary>The old unprocessed cache has been migrated (or found empty) already.</summary>
        internal bool LegacyHandled { get; set; }

        /// <summary>Text of the last file problem (for the log), or null.</summary>
        internal string LastError { get; private set; }

        /// <summary>True when the entries were read from the backup because the main file was damaged.</summary>
        internal bool LoadedFromBackup { get; private set; }

        internal ArabicCacheStore(string filePath)
        {
            FilePath = filePath;
        }

        internal int Count
        {
            get { return _entries.Count; }
        }

        internal bool IsDirty
        {
            get { return Interlocked.Read(ref _version) != Interlocked.Read(ref _savedVersion); }
        }

        internal bool TryGet(string key, out ArabicCacheEntry entry)
        {
            if (key == null)
            {
                entry = null;
                return false;
            }
            return _entries.TryGetValue(key, out entry);
        }

        internal bool ContainsKey(string key)
        {
            return key != null && _entries.ContainsKey(key);
        }

        /// <summary>Adds or replaces an entry. Only fully processed entries are accepted.</summary>
        internal bool Put(ArabicCacheEntry entry)
        {
            if (!Acceptable(entry)) return false;
            _entries[entry.Key] = entry;
            Interlocked.Increment(ref _version);
            return true;
        }

        /// <summary>Adds an entry that was just read from the file (does not make the cache dirty).</summary>
        internal bool PutLoaded(ArabicCacheEntry entry)
        {
            if (!Acceptable(entry)) return false;
            _entries[entry.Key] = entry;
            return true;
        }

        /// <summary>True for an entry the cache may hold: fully processed, with a key and both texts.</summary>
        internal static bool Acceptable(ArabicCacheEntry e)
        {
            return e != null && e.Status == CacheStatus.Final && !string.IsNullOrEmpty(e.Key) &&
                   !string.IsNullOrEmpty(e.Display) && !string.IsNullOrEmpty(e.Logical);
        }

        internal bool Remove(string key)
        {
            ArabicCacheEntry old;
            if (key != null && _entries.TryRemove(key, out old))
            {
                Interlocked.Increment(ref _version);
                return true;
            }
            return false;
        }

        internal void Clear()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Interlocked.Increment(ref _version);
        }

        /// <summary>Forces the next <see cref="Save"/> to write the file.</summary>
        internal void MarkDirty()
        {
            Interlocked.Increment(ref _version);
        }

        /// <summary>A copy of the current entries (safe to enumerate while other threads add entries).</summary>
        internal List<ArabicCacheEntry> Snapshot()
        {
            return new List<ArabicCacheEntry>(_entries.Values);
        }

        // ---------------------------------------------------------------------------------------- file

        /// <summary>
        /// Reads the cache file (or its backup when the main file is damaged). Returns null when there is no readable file.
        /// The entries are returned unchecked: <see cref="ArabicCacheMaintenance"/> decides which of them are still valid.
        /// </summary>
        internal ParsedCache ReadFile()
        {
            LastError = null;
            LoadedFromBackup = false;
            if (string.IsNullOrEmpty(FilePath)) return null;

            lock (_fileLock)
            {
                var candidates = new[] { FilePath, FilePath + ".bak" };
                for (int i = 0; i < candidates.Length; i++)
                {
                    var path = candidates[i];
                    try
                    {
                        if (!File.Exists(path)) continue;
                        var parsed = ArabicCacheFormat.Parse(File.ReadAllText(path, Encoding.UTF8));
                        LoadedFromBackup = i > 0;
                        return parsed;
                    }
                    catch (Exception ex)
                    {
                        LastError = "cannot read " + path + ": " + ex.Message;
                        try
                        {
                            // keep the damaged file for inspection; the next save replaces it
                            File.Copy(path, path + ".corrupt", true);
                        }
                        catch (Exception)
                        {
                            // best effort only
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Deletes the file and its backup: a cache that has lost its last entry leaves no empty file behind. The store is
        /// clean afterwards, unless an entry was added meanwhile (the next save then writes the file again). Returns false
        /// (and keeps the store dirty) when a file could not be deleted.
        /// </summary>
        internal bool DeleteFiles()
        {
            if (string.IsNullOrEmpty(FilePath)) return false;

            lock (_fileLock)
            {
                long version = Interlocked.Read(ref _version);
                try
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    if (File.Exists(FilePath + ".bak")) File.Delete(FilePath + ".bak");
                    if (_entries.Count == 0) Interlocked.Exchange(ref _savedVersion, version);
                    LastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "cannot delete " + FilePath + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes the cache file. <paramref name="pipeline"/> supplies the identifiers stored in the header. Returns false
        /// (and keeps the previous file) when writing failed; the cache stays dirty then.
        /// </summary>
        internal bool Save(ArabicPipeline pipeline)
        {
            if (string.IsNullOrEmpty(FilePath)) return false;

            lock (_fileLock)
            {
                long version = Interlocked.Read(ref _version);
                var entries = Snapshot();
                entries.Sort(delegate (ArabicCacheEntry a, ArabicCacheEntry b) { return string.CompareOrdinal(a.Key, b.Key); });

                var header = new CacheHeader
                {
                    GlossaryVersion = pipeline.GlossaryVersion,
                    PipelineVersion = ArabicVersions.PipelineVersion,
                    ShapingVersion = ArabicVersions.ShapingVersion,
                    BidiVersion = ArabicVersions.BidiVersion,
                    DisplayVersion = pipeline.DisplayVersion,
                    LegacyHandled = LegacyHandled
                };

                var tmp = FilePath + ".tmp";
                var bak = FilePath + ".bak";
                try
                {
                    var xml = ArabicCacheFormat.Serialize(entries, header);
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(tmp, xml, new UTF8Encoding(false));

                    if (File.Exists(FilePath))
                    {
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(FilePath, bak);
                    }
                    File.Move(tmp, FilePath);
                    Interlocked.Exchange(ref _savedVersion, version);
                    LastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "cannot write " + FilePath + ": " + ex.Message;
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        // the main file was moved aside and the new one could not be put in place: bring it back
                        if (!File.Exists(FilePath) && File.Exists(bak)) File.Move(bak, FilePath);
                    }
                    catch (Exception)
                    {
                        // nothing more can be done
                    }
                    return false;
                }
            }
        }
    }
}
