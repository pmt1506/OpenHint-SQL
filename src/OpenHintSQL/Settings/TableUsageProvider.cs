using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Settings
{
    internal static class TableUsageProvider
    {
        private const int FavoriteTableLimit = 3;
        private static readonly object Gate = new object();
        private static TableUsageStore _store;

        public static void RecordTableUsage(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return;

            lock (Gate)
            {
                EnsureLoaded();

                if (!_store.TableUsage.TryGetValue(fullName, out var score))
                    score = 0;

                _store.TableUsage[fullName] = score + 1;
                SaveUnsafe();
            }
        }

        public static int GetUsageScore(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return 0;

            lock (Gate)
            {
                EnsureLoaded();
                return _store.TableUsage.TryGetValue(fullName, out var score) ? score : 0;
            }
        }

        public static bool IsFavorite(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return false;

            lock (Gate)
            {
                EnsureLoaded();
                return GetFavoriteTableNamesUnsafe().Contains(fullName);
            }
        }

        private static HashSet<string> GetFavoriteTableNamesUnsafe()
        {
            return new HashSet<string>(
                _store.TableUsage
                    .Where(kvp => kvp.Value > 0)
                    .OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(FavoriteTableLimit)
                    .Select(kvp => kvp.Key),
                StringComparer.OrdinalIgnoreCase);
        }

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
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load table usage: {ex.Message}");
                _store = new TableUsageStore();
            }
        }

        private static void SaveUnsafe()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(
                    UsagePath,
                    JsonConvert.SerializeObject(_store, Formatting.Indented));
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
