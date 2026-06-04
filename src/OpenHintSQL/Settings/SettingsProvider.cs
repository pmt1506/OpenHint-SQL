using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Settings
{
    internal static class SettingsProvider
    {
        private static readonly object Gate = new object();
        private static OpenHintSqlSettings _settings;

        public static OpenHintSqlSettings GetSettings()
        {
            lock (Gate)
            {
                if (_settings == null)
                    _settings = LoadSettings();

                return Clone(_settings);
            }
        }

        public static void SaveSettings(OpenHintSqlSettings settings)
        {
            lock (Gate)
            {
                _settings = Normalize(settings);

                try
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    File.WriteAllText(
                        SettingsPath,
                        JsonConvert.SerializeObject(_settings, Formatting.Indented));
                    Logger.Log("OpenHint SQL settings saved.");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not save settings: {ex.Message}");
                }
            }
        }

        private static OpenHintSqlSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new OpenHintSqlSettings();

                return Normalize(JsonConvert.DeserializeObject<OpenHintSqlSettings>(File.ReadAllText(SettingsPath)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load settings: {ex.Message}");
                return new OpenHintSqlSettings();
            }
        }

        private static OpenHintSqlSettings Normalize(OpenHintSqlSettings settings)
        {
            settings = settings ?? new OpenHintSqlSettings();
            settings.CustomSnippets = settings.CustomSnippets ?? new List<CustomSnippetEntry>();

            var filtered = new List<CustomSnippetEntry>();
            foreach (var entry in settings.CustomSnippets)
            {
                var shortcut = entry?.Shortcut?.Trim();
                var expansion = entry?.Expansion?.Trim();
                var description = entry?.Description?.Trim();
                if (string.IsNullOrWhiteSpace(shortcut) || string.IsNullOrWhiteSpace(expansion))
                    continue;

                filtered.Add(new CustomSnippetEntry
                {
                    Shortcut = shortcut,
                    Expansion = expansion,
                    Description = description
                });
            }

            settings.CustomSnippets = filtered;
            return settings;
        }

        private static OpenHintSqlSettings Clone(OpenHintSqlSettings settings)
        {
            return new OpenHintSqlSettings
            {
                OmitDboSchemaOnInsert = settings.OmitDboSchemaOnInsert,
                CustomSnippets = new List<CustomSnippetEntry>(
                    settings.CustomSnippets.ConvertAll(entry => new CustomSnippetEntry
                    {
                        Shortcut = entry.Shortcut,
                        Expansion = entry.Expansion,
                        Description = entry.Description
                    }))
            };
        }

        private static string SettingsDirectory
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "OpenHintSQL");
            }
        }

        private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    }
}
