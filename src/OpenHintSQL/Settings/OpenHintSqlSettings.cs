using System.Collections.Generic;

namespace OpenHintSQL.Settings
{
    internal sealed class OpenHintSqlSettings
    {
        public bool OmitDboSchemaOnInsert { get; set; } = true;

        public List<CustomSnippetEntry> CustomSnippets { get; set; } = new List<CustomSnippetEntry>();
    }

    internal sealed class CustomSnippetEntry
    {
        public string Shortcut { get; set; }

        public string Expansion { get; set; }

        public string Description { get; set; }
    }
}
