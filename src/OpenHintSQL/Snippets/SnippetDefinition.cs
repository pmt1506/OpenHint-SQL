using System.Collections.Generic;

namespace OpenHintSQL.Snippets
{
    /// <summary>
    /// Represents a single snippet with its shortcut trigger and expansion text.
    /// </summary>
    public class SnippetDefinition
    {
        /// <summary>Short trigger text (e.g. "ssf") that activates this snippet.</summary>
        public string Shortcut { get; set; }

        /// <summary>Human-readable title for the snippet.</summary>
        public string Title { get; set; }

        /// <summary>
        /// Expansion template. Use \n for newlines and $cursor$ for cursor placement.
        /// </summary>
        public string Expansion { get; set; }

        /// <summary>Tooltip description of what the snippet produces.</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Root object for deserializing Config/snippets.json.
    /// </summary>
    public class SnippetConfig
    {
        /// <summary>List of snippet definitions loaded from configuration.</summary>
        public List<SnippetDefinition> Snippets { get; set; } = new List<SnippetDefinition>();
    }
}
