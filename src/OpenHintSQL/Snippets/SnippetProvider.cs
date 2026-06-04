using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using OpenHintSQL.Providers;
using OpenHintSQL.Settings;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Snippets
{
    /// <summary>
    /// Result of expanding a snippet, containing the final text and the offset
    /// where the caret should be placed after insertion.
    /// </summary>
    public class SnippetExpansionResult
    {
        /// <summary>The expanded text with placeholders and cursor marker removed.</summary>
        public string Text { get; set; }

        /// <summary>
        /// Zero-based offset within <see cref="Text"/> where the caret should be
        /// placed. -1 if no $cursor$ placeholder was present (caret goes to end).
        /// </summary>
        public int CursorOffset { get; set; }
    }

    /// <summary>
    /// Singleton provider that loads snippet definitions from Config/snippets.json
    /// and provides O(1) shortcut lookup plus prefix-based searching for the
    /// completion popup.
    /// </summary>
    internal sealed class SnippetProvider
    {
        private const string CursorPlaceholder = "$cursor$";
        private const string SnippetFileName = "Config\\snippets.json";

        private readonly Dictionary<string, SnippetDefinition> _shortcuts;
        private SnippetDefinition[] _allSnippets;

        // ──────────────────────────────────────────────
        //  Thread-safe lazy singleton
        // ──────────────────────────────────────────────

        private static readonly Lazy<SnippetProvider> _lazy =
            new Lazy<SnippetProvider>(() => new SnippetProvider(), isThreadSafe: true);

        /// <summary>Gets the singleton instance (created on first access).</summary>
        public static SnippetProvider Instance => _lazy.Value;

        // ──────────────────────────────────────────────
        //  Constructor — loads snippets from JSON
        // ──────────────────────────────────────────────

        private SnippetProvider()
        {
            _shortcuts = new Dictionary<string, SnippetDefinition>(StringComparer.OrdinalIgnoreCase);
            _allSnippets = Array.Empty<SnippetDefinition>();
            Reload();
        }

        public void Reload()
        {
            _shortcuts.Clear();
            _allSnippets = Array.Empty<SnippetDefinition>();

            try
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string assemblyPath = Uri.UnescapeDataString(uri.Path);
                string basePath = Path.GetDirectoryName(assemblyPath);
                string jsonPath = Path.Combine(basePath, SnippetFileName);

                if (!File.Exists(jsonPath))
                {
                    Logger.Warn($"Snippet file not found: {jsonPath}");
                    return;
                }

                string json = File.ReadAllText(jsonPath);
                var config = JsonConvert.DeserializeObject<SnippetConfig>(json);

                if (config?.Snippets == null || config.Snippets.Count == 0)
                {
                    Logger.Warn("Snippet file contained no snippets.");
                    return;
                }

                foreach (var snippet in config.Snippets)
                {
                    if (string.IsNullOrWhiteSpace(snippet.Shortcut))
                        continue;

                    // Last-one-wins for duplicate shortcuts
                    _shortcuts[snippet.Shortcut] = snippet;
                }

                foreach (var customSnippet in SettingsProvider.GetSettings().CustomSnippets)
                {
                    if (string.IsNullOrWhiteSpace(customSnippet.Shortcut) ||
                        string.IsNullOrWhiteSpace(customSnippet.Expansion))
                    {
                        continue;
                    }

                    _shortcuts[customSnippet.Shortcut] = new SnippetDefinition
                    {
                        Shortcut = customSnippet.Shortcut,
                        Title = customSnippet.Shortcut,
                        Expansion = customSnippet.Expansion,
                        Description = customSnippet.Description
                    };
                }

                _allSnippets = _shortcuts.Values.ToArray();
                Logger.Log($"SnippetProvider loaded {_allSnippets.Length} snippets");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load snippets", ex);
            }
        }

        // ──────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────

        /// <summary>Total number of loaded snippets.</summary>
        public int Count => _allSnippets.Length;

        /// <summary>
        /// Attempts to find a snippet by its exact shortcut (case-insensitive).
        /// </summary>
        public bool TryGetSnippet(string shortcut, out SnippetDefinition snippet)
        {
            if (string.IsNullOrEmpty(shortcut))
            {
                snippet = null;
                return false;
            }
            return _shortcuts.TryGetValue(shortcut, out snippet);
        }

        /// <summary>
        /// Returns snippets whose shortcuts start with <paramref name="prefix"/>
        /// (case-insensitive), wrapped as <see cref="CompletionItemData"/> for the
        /// completion popup.
        /// </summary>
        public IEnumerable<CompletionItemData> GetMatchingSnippets(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                yield break;

            for (int i = 0; i < _allSnippets.Length; i++)
            {
                var s = _allSnippets[i];
                if (s.Shortcut.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new CompletionItemData
                    {
                        Text = s.Shortcut,
                        InsertText = s.Shortcut,
                        Description = s.Description ?? s.Title,
                        Kind = CompletionItemKind.Snippet,
                        Priority = 50,  // Snippets appear above keywords
                        IconKey = "Snippet"
                    };
                }
            }
        }

        /// <summary>
        /// Processes a snippet expansion string: replaces literal \n with newlines,
        /// locates and removes the $cursor$ placeholder, and returns the final text
        /// together with the cursor offset.
        /// </summary>
        /// <param name="expansion">Raw expansion string from the snippet definition.</param>
        /// <returns>
        /// A <see cref="SnippetExpansionResult"/> with the cleaned text and cursor
        /// position. If no $cursor$ placeholder is found the cursor offset is set to
        /// the end of the text.
        /// </returns>
        public static SnippetExpansionResult ExpandSnippet(string expansion)
        {
            if (string.IsNullOrEmpty(expansion))
            {
                return new SnippetExpansionResult { Text = string.Empty, CursorOffset = 0 };
            }

            // Replace literal \n sequences with actual newlines
            string text = expansion.Replace("\\n", Environment.NewLine);

            // Locate $cursor$ placeholder
            int cursorIndex = text.IndexOf(CursorPlaceholder, StringComparison.Ordinal);

            if (cursorIndex >= 0)
            {
                // Remove the placeholder from the output
                text = text.Remove(cursorIndex, CursorPlaceholder.Length);
                return new SnippetExpansionResult { Text = text, CursorOffset = cursorIndex };
            }

            // No cursor placeholder — position at end
            return new SnippetExpansionResult { Text = text, CursorOffset = text.Length };
        }
    }
}
