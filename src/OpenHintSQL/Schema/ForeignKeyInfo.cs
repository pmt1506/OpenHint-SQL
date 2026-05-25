using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a foreign-key constraint with both endpoint columns resolved
    /// to <see cref="TableInfo"/> / <see cref="ColumnInfo"/> object references.
    /// </summary>
    public class ForeignKeyInfo
    {
        /// <summary>Constraint name from sys.foreign_keys.</summary>
        public string Name { get; set; }

        /// <summary>The table that declares the FK (the "parent" side in SQL Server terminology).</summary>
        public TableInfo ParentTable { get; set; }

        /// <summary>Columns on the parent table, in constraint_column_id order.</summary>
        public List<ColumnInfo> ParentColumns { get; set; } = new List<ColumnInfo>();

        /// <summary>The table being referenced (the "referenced" side — usually the PK side).</summary>
        public TableInfo ReferencedTable { get; set; }

        /// <summary>Columns on the referenced table, paired by ordinal with <see cref="ParentColumns"/>.</summary>
        public List<ColumnInfo> ReferencedColumns { get; set; } = new List<ColumnInfo>();
    }
}
