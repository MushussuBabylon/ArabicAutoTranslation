using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// The Arabic translation cache as the game uses it: a folder with one file per mod, named after the mod
    /// ("SimpleCameraSetting-ar.xml"). Every file is a complete cache file that an <see cref="ArabicCacheStore"/> reads and
    /// writes (backup, atomic swap and damaged-file handling included); this class only decides which file an entry belongs
    /// to. Deleting one file (with the game closed) makes the game translate that mod again, and nothing else.
    /// The single file of earlier versions (ArabicCache.xml) is split into the files of the mods on the first start and is
    /// renamed to ArabicCache.xml.migrated once the new files are written.
    /// The members are thread-safe: the translation worker adds entries while the main thread reads and saves.
    /// </summary>
    internal sealed class ArabicModCache
    {
        /// <summary>The end of the name of a mod's cache file.</summary>
        internal const string FileSuffix = "-ar.xml";

        private readonly string _directory;
        private readonly string _oldFile;
        private readonly Func<string, string> _modNameOf;
        private readonly object _gate = new object();
        private readonly List<ArabicCacheStore> _stores = new List<ArabicCacheStore>();
        private readonly Dictionary<string, ArabicCacheStore> _homeOfMod =
            new Dictionary<string, ArabicCacheStore>(StringComparer.OrdinalIgnoreCase);
        private bool _legacyHandled;
        private volatile string _takenOver;   // the file of earlier versions whose entries were taken over; renamed by the next save

        /// <param name="directory">Folder of the per-mod files (null: memory only, used by the tests).</param>
        /// <param name="oldFile">The single cache file of earlier versions, or null.</param>
        /// <param name="modNameOf">Gives the name of a mod for its package id (null when the mod is not known).</param>
        internal ArabicModCache(string directory, string oldFile, Func<string, string> modNameOf)
        {
            _directory = directory;
            _oldFile = oldFile;
            _modNameOf = modNameOf;
        }

        /// <summary>The folder of the per-mod files (null: memory only).</summary>
        internal string FolderPath
        {
            get { return _directory; }
        }

        /// <summary>Text of the last file problem (for the log), or null.</summary>
        internal string LastError { get; private set; }

        /// <summary>A remark for the log about the file of earlier versions, or null.</summary>
        internal string Note { get; private set; }

        /// <summary>The old unprocessed cache has been migrated (or found empty) already.</summary>
        internal bool LegacyHandled
        {
            get { return _legacyHandled; }
            set
            {
                lock (_gate)
                {
                    _legacyHandled = value;
                    foreach (var s in _stores)
                    {
                        if (s.LegacyHandled == value) continue;
                        s.LegacyHandled = value;
                        s.MarkDirty();
                    }
                }
            }
        }

        internal int Count
        {
            get
            {
                int n = 0;
                foreach (var s in Stores()) n += s.Count;
                return n;
            }
        }

        internal bool IsDirty
        {
            get
            {
                if (_takenOver != null) return true;
                foreach (var s in Stores())
                {
                    if (s.IsDirty) return true;
                }
                return false;
            }
        }

        private ArabicCacheStore[] Stores()
        {
            lock (_gate)
            {
                return _stores.ToArray();
            }
        }

        // ---------------------------------------------------------------------------------------- entries

        internal bool TryGet(string key, out ArabicCacheEntry entry)
        {
            foreach (var s in Stores())
            {
                if (s.TryGet(key, out entry)) return true;
            }
            entry = null;
            return false;
        }

        internal bool ContainsKey(string key)
        {
            foreach (var s in Stores())
            {
                if (s.ContainsKey(key)) return true;
            }
            return false;
        }

        /// <summary>
        /// Adds or replaces an entry. An entry whose key is stored already stays in the file it is in; a new one goes to the
        /// file of its mod (made when the mod has none yet). Only fully processed entries are accepted.
        /// </summary>
        internal bool Put(ArabicCacheEntry entry)
        {
            if (!ArabicCacheStore.Acceptable(entry)) return false;
            ArabicCacheStore home;
            lock (_gate)
            {
                home = OwnerLocked(entry.Key) ?? HomeOfModLocked(ModOf(entry));
            }
            return home.Put(entry);
        }

        internal bool Remove(string key)
        {
            foreach (var s in Stores())
            {
                if (s.Remove(key)) return true;
            }
            return false;
        }

        internal void Clear()
        {
            foreach (var s in Stores()) s.Clear();
        }

        /// <summary>Forces the next <see cref="Save"/> to write every file.</summary>
        internal void MarkDirty()
        {
            foreach (var s in Stores()) s.MarkDirty();
        }

        /// <summary>A copy of the current entries of all mods (safe to enumerate while other threads add entries).</summary>
        internal List<ArabicCacheEntry> Snapshot()
        {
            var all = new List<ArabicCacheEntry>();
            foreach (var s in Stores()) all.AddRange(s.Snapshot());
            return all;
        }

        private ArabicCacheStore OwnerLocked(string key)
        {
            foreach (var s in _stores)
            {
                if (s.ContainsKey(key)) return s;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------- which file

        /// <summary>The mod an entry belongs to: its mod id, otherwise the part of its key before the first colon.</summary>
        internal static string ModOf(ArabicCacheEntry e)
        {
            if (!string.IsNullOrEmpty(e.ModId)) return e.ModId;
            int colon = e.Key.IndexOf(':');
            return colon > 0 ? e.Key.Substring(0, colon) : ArabicCacheMaintenance.LegacyModId;
        }

        /// <summary>
        /// A name as the stem of a file name: without spaces and without the characters a file name cannot hold ("Simple
        /// Camera Setting" gives "SimpleCameraSetting"), at most 60 characters. Empty when nothing is left.
        /// </summary>
        internal static string FileStem(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            const string forbidden = "<>:\"/\\|?*";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || forbidden.IndexOf(c) >= 0) continue;
                sb.Append(c);
                if (sb.Length >= 60) break;
            }
            if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
            return sb.ToString().Trim('.');
        }

        private ArabicCacheStore HomeOfModLocked(string mod)
        {
            ArabicCacheStore home;
            if (_homeOfMod.TryGetValue(mod, out home)) return home;
            home = new ArabicCacheStore(PathForLocked(mod));
            home.LegacyHandled = _legacyHandled;
            _stores.Add(home);
            _homeOfMod[mod] = home;
            return home;
        }

        /// <summary>The file for a mod that has none yet: its name, or its name with its package id when the name is taken.</summary>
        private string PathForLocked(string mod)
        {
            if (string.IsNullOrEmpty(_directory)) return null;

            string name = null;
            try
            {
                if (_modNameOf != null) name = _modNameOf(mod);
            }
            catch (Exception)
            {
                name = null;
            }
            string stem = FileStem(name);
            if (stem.Length == 0) stem = FileStem(mod);
            if (stem.Length == 0) stem = "mod";

            string path = Path.Combine(_directory, stem + FileSuffix);
            if (!InUseLocked(path)) return path;

            // two mods can be called alike: the package id tells them apart
            string id = FileStem(mod);
            for (int n = 1; ; n++)
            {
                path = Path.Combine(_directory, stem + "." + id + (n == 1 ? "" : "-" + n) + FileSuffix);
                if (!InUseLocked(path)) return path;
            }
        }

        private bool InUseLocked(string path)
        {
            foreach (var s in _stores)
            {
                if (string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return File.Exists(path);
        }

        // ---------------------------------------------------------------------------------------- files

        /// <summary>
        /// Reads the files of all mods (and the file of earlier versions, whose entries are moved to the files of their mods)
        /// and checks every entry, see <see cref="ArabicCacheMaintenance.LoadAndCheck"/>.
        /// </summary>
        internal CacheReport Load(ArabicPipeline pipeline)
        {
            var report = new CacheReport();
            string[] files = new string[0];
            if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
            {
                try
                {
                    files = Directory.GetFiles(_directory, "*" + FileSuffix);
                }
                catch (Exception ex)
                {
                    LastError = "cannot list " + _directory + ": " + ex.Message;
                }
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                if (path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)) LoadFile(path, pipeline, report);
            }
            TakeOverOldFile(pipeline, report);
            return report;
        }

        private void LoadFile(string path, ArabicPipeline pipeline, CacheReport report)
        {
            var store = new ArabicCacheStore(path);
            report.Add(ArabicCacheMaintenance.LoadAndCheck(store, pipeline));
            if (store.LastError != null) LastError = store.LastError;

            lock (_gate)
            {
                // a key that an earlier file has already is not kept twice
                foreach (var e in store.Snapshot())
                {
                    if (OwnerLocked(e.Key) != null && store.Remove(e.Key)) report.Rejected++;
                }
                _stores.Add(store);
                if (store.LegacyHandled) _legacyHandled = true;
                foreach (var e in store.Snapshot())
                {
                    string mod = ModOf(e);
                    if (!_homeOfMod.ContainsKey(mod)) _homeOfMod[mod] = store;
                }
            }
        }

        /// <summary>Moves the entries of the single file of earlier versions into the files of their mods.</summary>
        private void TakeOverOldFile(ArabicPipeline pipeline, CacheReport report)
        {
            if (string.IsNullOrEmpty(_oldFile) || !File.Exists(_oldFile)) return;

            var old = new ArabicCacheStore(_oldFile);
            var r = ArabicCacheMaintenance.LoadAndCheck(old, pipeline);
            if (old.LastError != null)
            {
                LastError = old.LastError;
                if (old.Count == 0) return;   // not readable: the file stays as it is
            }
            report.Add(r);
            foreach (var e in old.Snapshot())
            {
                if (ContainsKey(e.Key)) continue;   // the file of the mod wins
                Put(e);
            }
            if (old.LegacyHandled) LegacyHandled = true;
            _takenOver = _oldFile;
            Note = "The cache of earlier versions (" + Path.GetFileName(_oldFile) + ", " + r.Loaded + " entries) was split into one file per mod; " +
                   "the old file is kept as " + Path.GetFileName(_oldFile) + ".migrated.";
        }

        /// <summary>
        /// Writes the file of every mod that changed (a mod without entries has no file). Returns false (and keeps what could not
        /// be written dirty) when a file could not be written.
        /// </summary>
        internal bool Save(ArabicPipeline pipeline)
        {
            if (string.IsNullOrEmpty(_directory)) return false;

            bool ok = true;
            string error = null;
            foreach (var store in Stores())
            {
                if (!store.IsDirty) continue;
                bool done = store.Count == 0 ? store.DeleteFiles() : store.Save(pipeline);
                if (!done)
                {
                    ok = false;
                    if (error == null) error = store.LastError;
                }
            }
            if (ok) RetireOldFile();
            LastError = ok ? null : error;
            return ok;
        }

        /// <summary>Renames the file of earlier versions once its entries are safe in the files of the mods.</summary>
        private void RetireOldFile()
        {
            var path = _takenOver;
            if (path == null) return;
            _takenOver = null;   // tried once: a file that cannot be renamed must not make every save fail
            try
            {
                var target = path + ".migrated";
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                Note = "The cache of earlier versions (" + Path.GetFileName(path) + ") could not be renamed: " + ex.Message +
                       " Its entries are in the files of the mods now; delete the old file by hand.";
            }
        }
    }
}
