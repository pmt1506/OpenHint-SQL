using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a table or view in the database schema.
    /// </summary>
    public class TableInfo
    {
        private string _schemaName;
        private string _name;
        private string _fullName;
        private string _bracketedName;

        /// <summary>Schema name (e.g. dbo, Sales).</summary>
        public string SchemaName
        {
            get => _schemaName;
            set { _schemaName = value; ResetDerivedNames(); }
        }

        /// <summary>Object name (e.g. Customers, OrdersView).</summary>
        public string Name
        {
            get => _name;
            set { _name = value; ResetDerivedNames(); }
        }

        /// <summary>Object type: "TABLE" or "VIEW".</summary>
        public string ObjectType { get; set; }

        /// <summary>Columns belonging to this table/view, ordered by ordinal position.</summary>
        public List<ColumnInfo> Columns { get; set; } = new List<ColumnInfo>();

        /// <summary>Primary-key columns in key_ordinal order. Empty when the table has no PK.</summary>
        /// <remarks>
        /// Re-derived from <see cref="ColumnInfo.IsPrimaryKey"/> in <see cref="DatabaseSchema.Build"/>.
        /// Excluded from JSON by <c>SchemaPersister</c>'s contract resolver (do NOT use [JsonIgnore]
        /// here — type-level Newtonsoft attributes break early MEF/VSPackage type discovery in SSMS).
        /// </remarks>
        public List<ColumnInfo> PrimaryKeyColumns { get; set; } = new List<ColumnInfo>();

        /// <summary>FK constraints declared on this table (this table is the parent / referencing side).</summary>
        /// <remarks>Re-resolved from <see cref="DatabaseSchema.PendingForeignKeys"/> in Build(); excluded from JSON via contract resolver.</remarks>
        public List<ForeignKeyInfo> ForeignKeys { get; set; } = new List<ForeignKeyInfo>();

        /// <summary>FK constraints from other tables that reference this table (this table is the referenced side).</summary>
        public List<ForeignKeyInfo> IncomingForeignKeys { get; set; } = new List<ForeignKeyInfo>();

        // Both names are read for every table on every completion keystroke, so they
        // are built once and reset only when SchemaName or Name changes.

        /// <summary>Schema-qualified name (e.g. dbo.Customers).</summary>
        public string FullName => _fullName ?? (_fullName = $"{SchemaName}.{Name}");

        /// <summary>Bracket-escaped full name (e.g. [dbo].[Customers]).</summary>
        public string BracketedName =>
            _bracketedName ?? (_bracketedName = $"{QuoteIdentifier(SchemaName)}.{QuoteIdentifier(Name)}");

        private void ResetDerivedNames()
        {
            _fullName = null;
            _bracketedName = null;
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"[{(identifier ?? string.Empty).Replace("]", "]]")}]";
        }
    }
}
