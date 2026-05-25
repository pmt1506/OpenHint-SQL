namespace OpenHintSQL.Providers
{
    /// <summary>
    /// Identifies the category of a completion item for icon and sorting purposes.
    /// </summary>
    public enum CompletionItemKind
    {
        Keyword,
        Snippet,
        Table,
        View,
        Column,
        Procedure,
        Function,
        DataType,
        Schema,
        Status,
        /// <summary>
        /// A composite "JOIN &lt;table&gt; &lt;alias&gt; ON ..." suggestion derived from a foreign key.
        /// <c>InsertText</c> begins at the table name (the leading "JOIN " is preserved from what the user typed).
        /// </summary>
        JoinSuggestion
    }

    /// <summary>
    /// Represents a single item in the completion popup list.
    /// </summary>
    public class CompletionItemData
    {
        /// <summary>Display text shown in the completion list.</summary>
        public string Text { get; set; }

        /// <summary>Text to insert into the editor (may differ from display text).</summary>
        public string InsertText { get; set; }

        /// <summary>Tooltip description shown alongside the item.</summary>
        public string Description { get; set; }

        /// <summary>Category of this completion item.</summary>
        public CompletionItemKind Kind { get; set; }

        /// <summary>Sort priority — lower values appear first in the list.</summary>
        public int Priority { get; set; }

        /// <summary>Key used to select the appropriate icon for this item.</summary>
        public string IconKey { get; set; }

        /// <summary>Compact glyph shown in the completion popup.</summary>
        public string KindGlyph
        {
            get
            {
                switch (Kind)
                {
                    case CompletionItemKind.Snippet:
                        return "S";
                    case CompletionItemKind.Table:
                        return "T";
                    case CompletionItemKind.View:
                        return "V";
                    case CompletionItemKind.Column:
                        return "C";
                    case CompletionItemKind.Procedure:
                        return "P";
                    case CompletionItemKind.Function:
                        return "F";
                    case CompletionItemKind.DataType:
                        return "D";
                    case CompletionItemKind.Schema:
                        return "N";
                    case CompletionItemKind.Status:
                        return "i";
                    case CompletionItemKind.JoinSuggestion:
                        return "J";
                    default:
                        return "K";
                }
            }
        }
    }
}
