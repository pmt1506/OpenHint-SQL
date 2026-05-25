using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a table or view in the database schema.
    /// </summary>
    public class TableInfo
    {
        /// <summary>Schema name (e.g. dbo, Sales).</summary>
        public string SchemaName { get; set; }

        /// <summary>Object name (e.g. Customers, OrdersView).</summary>
        public string Name { get; set; }

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

        /// <summary>Schema-qualified name (e.g. dbo.Customers).</summary>
        public string FullName => $"{SchemaName}.{Name}";

        /// <summary>Bracket-escaped full name (e.g. [dbo].[Customers]).</summary>
        public string BracketedName => $"[{SchemaName}].[{Name}]";
    }
}
