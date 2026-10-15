using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Settings
{
    internal static class TableUsageProvider
    {
        private const int FavoriteItemLimit = 3;

        /// <summary>
        /// Usage changes are flushed to disk after this quiet period, so accepting
        /// several items in a row costs one background write instead of one
        /// synchronous write per accepted item on the UI thread.
        /// </summary>
        private const int SaveDelayMs = 1500;

        private static readonly object Gate = new object();
        private static readonly object SaveGate = new object();
        private static TableUsageStore _store;
        private static Timer _saveTimer;

        /// <summary>
        /// Top-N favorite keys per item kind. Score and favorite lookups run once per
        /// suggested item (thousands per popup on large databases), so the ranking is
        /// computed once and only invalidated when usage is recorded.
        /// </summary>
        private static readonly Dictionary<OpenHintSQL.Providers.CompletionItemKind, HashSet<string>> FavoritesByKind =
            new Dictionary<OpenHintSQL.Providers.CompletionItemKind, HashSet<string>>();

        public static void RecordTableUsage(string fullName)
            => RecordItemUsage(OpenHintSQL.Providers.CompletionItemKind.Table, fullName);

        public static void RecordItemUsage(OpenHintSQL.Providers.CompletionItemKind kind, string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return;

            lock (Gate)
            {
                EnsureLoaded();

                string key = BuildKey(kind, fullName);
                if (!_store.TableUsage.TryGetValue(key, out var score))
                    score = 0;

                _store.TableUsage[key] = score + 1;
                FavoritesByKind.Remove(kind);
                ScheduleSaveUnsafe();
            }
        }

        public static int GetUsageScore(string fullName)
            => GetUsageScore(OpenHintSQL.Providers.CompletionItemKind.Table, fullName);

        public static int GetUsageScore(OpenHintSQL.Providers.CompletionItemKind kind, string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return 0;

            lock (Gate)
            {
                EnsureLoaded();
                string key = BuildKey(kind, fullName);
                if (_store.TableUsage.TryGetValue(key, out var score))
                    return score;

                return _store.TableUsage.TryGetValue(fullName, out score) ? score : 0;
            }
        }

        public static bool IsFavorite(string fullName)
            => IsFavorite(OpenHintSQL.Providers.CompletionItemKind.Table, fullName);

        public static bool IsFavorite(OpenHintSQL.Providers.CompletionItemKind kind, string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return false;

            lock (Gate)
            {
                EnsureLoaded();
                return GetFavoriteItemNamesUnsafe(kind).Contains(BuildKey(kind, fullName));
            }
        }

        private static HashSet<string> GetFavoriteItemNamesUnsafe(OpenHintSQL.Providers.CompletionItemKind kind)
        {
            if (FavoritesByKind.TryGetValue(kind, out var cached))
                return cached;

            string keyPrefix = BuildKeyPrefix(kind);
            var favorites = new HashSet<string>(
                _store.TableUsage
                    .Where(kvp => kvp.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(kvp => kvp.Value > 0)
                    .OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(FavoriteItemLimit)
                    .Select(kvp => kvp.Key),
                StringComparer.OrdinalIgnoreCase);

            FavoritesByKind[kind] = favorites;
            return favorites;
        }

        private static string BuildKey(OpenHintSQL.Providers.CompletionItemKind kind, string fullName)
            => $"{kind}|{fullName}";

        private static string BuildKeyPrefix(OpenHintSQL.Providers.CompletionItemKind kind)
            => $"{kind}|";

        private static void EnsureLoaded()
        {
            if (_store != null)
                return;

            try
            {
                if (!File.Exists(UsagePath))
                {
                    _store = new TableUsageStore();
                    return;
                }

                _store = JsonConvert.DeserializeObject<TableUsageStore>(File.ReadAllText(UsagePath)) ??
                         new TableUsageStore();
                _store.TableUsage = _store.TableUsage == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(_store.TableUsage, StringComparer.OrdinalIgnoreCase);
                MigrateLegacyKeysUnsafe();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load table usage: {ex.Message}");
                _store = new TableUsageStore();
            }
        }

        private static void MigrateLegacyKeysUnsafe()
        {
            if (_store?.TableUsage == null || _store.TableUsage.Count == 0)
                return;

            var legacyKeys = _store.TableUsage.Keys
                .Where(key => !key.Contains("|"))
                .ToList();

            foreach (var legacyKey in legacyKeys)
            {
                if (_store.TableUsage.TryGetValue(legacyKey, out var score))
                {
                    string migratedKey = BuildKey(OpenHintSQL.Providers.CompletionItemKind.Table, legacyKey);
                    if (!_store.TableUsage.ContainsKey(migratedKey))
                        _store.TableUsage[migratedKey] = score;
                }
            }
        }

        private static void ScheduleSaveUnsafe()
        {
            if (_saveTimer == null)
                _saveTimer = new Timer(_ => SaveNow(), null, SaveDelayMs, Timeout.Infinite);
            else
                _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }

        /// <summary>
        /// Runs on a thread-pool thread. Serialises under <see cref="Gate"/> (so the
        /// dictionary can't change mid-write) but does the file I/O outside it, so
        /// completion lookups never wait on the disk. <see cref="SaveGate"/> spans both
        /// steps so overlapping saves can't write an older snapshot last.
        /// </summary>
        private static void SaveNow()
        {
            try
            {
                lock (SaveGate)
                {
                    string json;
                    lock (Gate)
                    {
                        if (_store == null)
                            return;

                        json = JsonConvert.SerializeObject(_store, Formatting.Indented);
                    }

                    Directory.CreateDirectory(SettingsDirectory);
                    File.WriteAllText(UsagePath, json);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not save table usage: {ex.Message}");
            }
        }

        private static string SettingsDirectory
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "OpenHintSQL");
            }
        }

        private static string UsagePath => Path.Combine(SettingsDirectory, "table-usage.json");
    }
}
