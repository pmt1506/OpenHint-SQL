using System.Windows.Media;

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
        Alias,
        Column,
        Procedure,
        Function,
        DataType,
        Schema,
        Database,
        Status,
        /// <summary>
        /// A composite "JOIN <table> <alias> ON ..." suggestion derived from a foreign key.
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

        /// <summary>Sort priority. Higher values appear first in the list.</summary>
        public int Priority { get; set; }

        /// <summary>Usage count captured from local completion history.</summary>
        public int UsageScore { get; set; }

        /// <summary>True when this item is currently in the user's top favorites.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>
        /// Optional stable ordering hint used before alphabetical sorting.
        /// Lower values appear earlier. Null means there is no explicit order.
        /// </summary>
        public int? SortOrder { get; set; }

        /// <summary>Key used to select the appropriate icon for this item.</summary>
        public string IconKey { get; set; }

        public Geometry IconGeometry => GetIconGeometry(Kind);
        public string FavoriteMarker => IsFavorite ? "\u2605" : string.Empty;
        public Brush BadgeBackgroundBrush => GetBadgeBackgroundBrush(Kind);
        public Brush BadgeForegroundBrush => GetBadgeForegroundBrush(Kind);
        public Brush PrimaryTextBrush => GetPrimaryTextBrush(Kind);
        public Brush RowTintBrush => GetRowTintBrush(Kind);
        public Brush FavoriteBrush => FavoriteForegroundBrush;

        private static readonly Brush TableBadgeBackgroundBrush = CreateFrozenBrush("#DCEBFF");
        private static readonly Brush TableBadgeForegroundBrush = CreateFrozenBrush("#005CC5");
        private static readonly Brush ViewBadgeBackgroundBrush = CreateFrozenBrush("#EEE3FF");
        private static readonly Brush ViewBadgeForegroundBrush = CreateFrozenBrush("#6F42C1");
        private static readonly Brush AliasBadgeBackgroundBrush = CreateFrozenBrush("#FFF1D6");
        private static readonly Brush AliasBadgeForegroundBrush = CreateFrozenBrush("#B26A00");
        private static readonly Brush ColumnBadgeBackgroundBrush = CreateFrozenBrush("#E9EEF3");
        private static readonly Brush ColumnBadgeForegroundBrush = CreateFrozenBrush("#57606A");
        private static readonly Brush FunctionBadgeBackgroundBrush = CreateFrozenBrush("#DDF5E5");
        private static readonly Brush FunctionBadgeForegroundBrush = CreateFrozenBrush("#1A7F37");
        private static readonly Brush ProcedureBadgeBackgroundBrush = CreateFrozenBrush("#E0F2FE");
        private static readonly Brush ProcedureBadgeForegroundBrush = CreateFrozenBrush("#0A7EA4");
        private static readonly Brush DatabaseBadgeBackgroundBrush = CreateFrozenBrush("#E5F9F6");
        private static readonly Brush DatabaseBadgeForegroundBrush = CreateFrozenBrush("#0F766E");
        private static readonly Brush NeutralBadgeBackgroundBrush = CreateFrozenBrush("#EEF4FF");
        private static readonly Brush NeutralBadgeForegroundBrush = CreateFrozenBrush("#0969DA");

        private static readonly Brush TableTextBrush = CreateFrozenBrush("#0B3D91");
        private static readonly Brush ViewTextBrush = CreateFrozenBrush("#5A32A3");
        private static readonly Brush AliasTextBrush = CreateFrozenBrush("#9A5B00");
        private static readonly Brush ColumnTextBrush = CreateFrozenBrush("#1F2328");
        private static readonly Brush FunctionTextBrush = CreateFrozenBrush("#176F2C");
        private static readonly Brush ProcedureTextBrush = CreateFrozenBrush("#0E7490");
        private static readonly Brush NeutralTextBrush = CreateFrozenBrush("#1F2328");

        private static readonly Brush TableRowTintBrush = CreateFrozenBrush("#F6FAFF");
        private static readonly Brush ViewRowTintBrush = CreateFrozenBrush("#FBF8FF");
        private static readonly Brush AliasRowTintBrush = CreateFrozenBrush("#FFFBF2");
        private static readonly Brush ColumnRowTintBrush = CreateFrozenBrush("#FBFCFD");
        private static readonly Brush FunctionRowTintBrush = CreateFrozenBrush("#F5FCF7");
        private static readonly Brush ProcedureRowTintBrush = CreateFrozenBrush("#F5FBFE");
        private static readonly Brush NeutralRowTintBrush = CreateFrozenBrush("#FFFFFF");
        private static readonly Brush FavoriteForegroundBrush = CreateFrozenBrush("#BF8700");

        private static readonly Geometry GridIconGeometry = CreateFrozenGeometry("F1 M1,1 L4.5,1 4.5,4.5 1,4.5 Z M5.5,1 L9,1 9,4.5 5.5,4.5 Z M1,5.5 L4.5,5.5 4.5,9 1,9 Z M5.5,5.5 L9,5.5 9,9 5.5,9 Z");
        private static readonly Geometry ColumnIconGeometry = CreateFrozenGeometry("F1 M2,1 L8,1 8,2.2 2,2.2 Z M2,4 L8,4 8,5.2 2,5.2 Z M2,7 L8,7 8,8.2 2,8.2 Z");
        private static readonly Geometry FunctionIconGeometry = CreateFrozenGeometry("F1 M2,1 L8,1 8,2.2 5.2,2.2 5.2,4 7.4,4 7.4,5.2 5.2,5.2 5.2,9 3.8,9 3.8,5.2 2,5.2 2,4 3.8,4 3.8,2.2 2,2.2 Z");
        private static readonly Geometry ProcedureIconGeometry = CreateFrozenGeometry("F1 M2,1 L6.5,1 8.5,3 8.5,9 2,9 Z M6.2,1.8 L6.2,3.4 7.8,3.4");
        private static readonly Geometry AliasIconGeometry = CreateFrozenGeometry("F1 M5,1 C7.2,1 9,2.8 9,5 9,7.2 7.2,9 5,9 2.8,9 1,7.2 1,5 1,2.8 2.8,1 5,1 Z M5,2.2 C3.5,2.2 2.2,3.5 2.2,5 2.2,6.5 3.5,7.8 5,7.8 6.5,7.8 7.8,6.5 7.8,5 7.8,3.5 6.5,2.2 5,2.2 Z M5.8,3.1 L4.3,5.6 6.1,5.6 5.6,6.9 3.3,6.9 4.7,4.4 3.1,4.4 3.6,3.1 Z");
        private static readonly Geometry DotIconGeometry = CreateFrozenGeometry("F1 M5,2.2 A2.8,2.8 0 1 1 4.999,2.2 Z");

        private static Geometry GetIconGeometry(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table:
                case CompletionItemKind.View:
                case CompletionItemKind.Database:
                    return GridIconGeometry;
                case CompletionItemKind.Alias:
                    return AliasIconGeometry;
                case CompletionItemKind.Column:
                    return ColumnIconGeometry;
                case CompletionItemKind.Function:
                    return FunctionIconGeometry;
                case CompletionItemKind.Procedure:
                    return ProcedureIconGeometry;
                default:
                    return DotIconGeometry;
            }
        }

        private static Brush GetBadgeBackgroundBrush(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return TableBadgeBackgroundBrush;
                case CompletionItemKind.View: return ViewBadgeBackgroundBrush;
                case CompletionItemKind.Alias: return AliasBadgeBackgroundBrush;
                case CompletionItemKind.Column: return ColumnBadgeBackgroundBrush;
                case CompletionItemKind.Function: return FunctionBadgeBackgroundBrush;
                case CompletionItemKind.Procedure: return ProcedureBadgeBackgroundBrush;
                case CompletionItemKind.Database: return DatabaseBadgeBackgroundBrush;
                default: return NeutralBadgeBackgroundBrush;
            }
        }

        private static Brush GetBadgeForegroundBrush(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return TableBadgeForegroundBrush;
                case CompletionItemKind.View: return ViewBadgeForegroundBrush;
                case CompletionItemKind.Alias: return AliasBadgeForegroundBrush;
                case CompletionItemKind.Column: return ColumnBadgeForegroundBrush;
                case CompletionItemKind.Function: return FunctionBadgeForegroundBrush;
                case CompletionItemKind.Procedure: return ProcedureBadgeForegroundBrush;
                case CompletionItemKind.Database: return DatabaseBadgeForegroundBrush;
                default: return NeutralBadgeForegroundBrush;
            }
        }

        private static Brush GetPrimaryTextBrush(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return TableTextBrush;
                case CompletionItemKind.View: return ViewTextBrush;
                case CompletionItemKind.Alias: return AliasTextBrush;
                case CompletionItemKind.Column: return ColumnTextBrush;
                case CompletionItemKind.Function: return FunctionTextBrush;
                case CompletionItemKind.Procedure: return ProcedureTextBrush;
                default: return NeutralTextBrush;
            }
        }

        private static Brush GetRowTintBrush(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return TableRowTintBrush;
                case CompletionItemKind.View: return ViewRowTintBrush;
                case CompletionItemKind.Alias: return AliasRowTintBrush;
                case CompletionItemKind.Column: return ColumnRowTintBrush;
                case CompletionItemKind.Function: return FunctionRowTintBrush;
                case CompletionItemKind.Procedure: return ProcedureRowTintBrush;
                default: return NeutralRowTintBrush;
            }
        }

        private static Brush CreateFrozenBrush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }

        private static Geometry CreateFrozenGeometry(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
    }
}
