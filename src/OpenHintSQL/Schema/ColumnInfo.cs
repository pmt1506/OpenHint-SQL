namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a single column within a table or view.
    /// </summary>
    public class ColumnInfo
    {
        /// <summary>Column name.</summary>
        public string Name { get; set; }

        /// <summary>SQL data type name (e.g. nvarchar, int, datetime2).</summary>
        public string DataType { get; set; }

        /// <summary>Maximum length in bytes (-1 for MAX).</summary>
        public int MaxLength { get; set; }

        /// <summary>Whether the column allows NULL values.</summary>
        public bool IsNullable { get; set; }

        /// <summary>Whether the column is an IDENTITY column.</summary>
        public bool IsIdentity { get; set; }

        /// <summary>Whether the column participates in the table's primary key.</summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>Ordinal position of the column within its table (1-based from sys.columns).</summary>
        public int OrdinalPosition { get; set; }

        /// <summary>
        /// Formatted display text including data type and PK/nullable/identity annotations.
        /// PK takes precedence over IDENTITY/NULL in the label.
        /// </summary>
        public string DisplayText => IsPrimaryKey
            ? $"{Name} ({DataType}, PK)"
            : IsIdentity
                ? $"{Name} ({DataType}, IDENTITY)"
                : IsNullable
                    ? $"{Name} ({DataType}, NULL)"
                    : $"{Name} ({DataType})";
    }
}
