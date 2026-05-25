using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenHintSQL.Providers
{
    /// <summary>
    /// Static provider of T-SQL keywords, data types, built-in functions, and system
    /// stored procedures. All items are pre-built at class load time — zero allocation
    /// per query.
    /// </summary>
    internal static class SqlKeywordProvider
    {
        // ──────────────────────────────────────────────
        //  Priority bands (lower = shown first)
        // ──────────────────────────────────────────────
        private const int PriorityKeyword  = 100;
        private const int PriorityFunction = 200;
        private const int PriorityDataType = 300;
        private const int PrioritySysSp    = 400;
        private const int PrioritySetOpt   = 500;

        // ──────────────────────────────────────────────
        //  Raw keyword / function / type lists
        // ──────────────────────────────────────────────

        #region T-SQL Keywords

        private static readonly string[] Keywords =
        {
            // DML
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
            "INTO", "VALUES", "SET", "FROM", "WHERE", "AND", "OR", "NOT",
            "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL", "AS",
            "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "APPLY",
            "UNION", "UNION ALL", "EXCEPT", "INTERSECT",
            "ORDER BY", "GROUP BY", "HAVING", "DISTINCT", "TOP", "PERCENT",
            "WITH", "TIES", "NOLOCK", "READUNCOMMITTED", "READCOMMITTED",
            "REPEATABLEREAD", "SERIALIZABLE", "ROWLOCK", "UPDLOCK", "TABLOCK",
            "HOLDLOCK", "PAGLOCK", "XLOCK",
            "OVER", "PARTITION BY", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING",
            "FOLLOWING", "CURRENT ROW",
            "PIVOT", "UNPIVOT", "TABLESAMPLE",
            "OUTPUT", "INSERTED", "DELETED",
            "OPTION", "MAXRECURSION", "RECOMPILE", "OPTIMIZE FOR",

            // DDL
            "CREATE", "ALTER", "DROP", "ADD", "COLUMN",
            "TABLE", "VIEW", "INDEX", "PROCEDURE", "FUNCTION", "TRIGGER",
            "SCHEMA", "DATABASE", "SEQUENCE", "SYNONYM", "TYPE",
            "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "CONSTRAINT",
            "UNIQUE", "CLUSTERED", "NONCLUSTERED", "CHECK", "DEFAULT",
            "IDENTITY", "NOT NULL", "NULL",
            "IF EXISTS", "IF NOT EXISTS",
            "INCLUDE", "FILLFACTOR", "WITH",

            // Control flow
            "BEGIN", "END", "IF", "ELSE", "WHILE", "BREAK", "CONTINUE",
            "GOTO", "RETURN", "THROW", "RAISERROR", "WAITFOR",
            "TRY", "CATCH", "BEGIN TRY", "END TRY", "BEGIN CATCH", "END CATCH",

            // Transaction
            "BEGIN TRANSACTION", "COMMIT", "COMMIT TRANSACTION",
            "ROLLBACK", "ROLLBACK TRANSACTION", "SAVE TRANSACTION",

            // Variable / cursor
            "DECLARE", "CURSOR", "OPEN", "CLOSE", "DEALLOCATE",
            "FETCH", "NEXT", "PRIOR", "FIRST", "LAST", "ABSOLUTE", "RELATIVE",

            // Execution
            "EXEC", "EXECUTE", "PRINT", "USE", "GO",

            // Misc
            "CASE", "WHEN", "THEN", "ELSE", "END",
            "ASC", "DESC", "OFFSET", "FETCH NEXT", "ROWS ONLY",
            "ALL", "ANY", "SOME",
            "GRANT", "REVOKE", "DENY",
            "COLLATE", "COALESCE", "NULLIF",
            "CROSS APPLY", "OUTER APPLY",
            "FOR XML PATH", "FOR JSON PATH", "FOR JSON AUTO",
            "STRING_SPLIT", "OPENJSON", "JSON_VALUE", "JSON_QUERY", "JSON_MODIFY",
            "AT TIME ZONE",
            "BULK INSERT", "OPENROWSET", "OPENQUERY",
            "ENABLE", "DISABLE", "REBUILD", "REORGANIZE",
            "BACKUP", "RESTORE",
        };

        #endregion

        #region T-SQL Data Types

        private static readonly string[] DataTypes =
        {
            // Exact numerics
            "BIT", "TINYINT", "SMALLINT", "INT", "BIGINT",
            "DECIMAL", "NUMERIC", "MONEY", "SMALLMONEY",

            // Approximate numerics
            "FLOAT", "REAL",

            // Date and time
            "DATE", "TIME", "DATETIME", "DATETIME2", "DATETIMEOFFSET", "SMALLDATETIME",

            // Character strings
            "CHAR", "VARCHAR", "TEXT", "NCHAR", "NVARCHAR", "NTEXT",

            // Binary
            "BINARY", "VARBINARY", "IMAGE",

            // Other
            "UNIQUEIDENTIFIER", "XML", "SQL_VARIANT",
            "GEOGRAPHY", "GEOMETRY", "HIERARCHYID",
            "ROWVERSION", "TIMESTAMP", "CURSOR", "TABLE",
            "SYSNAME",
        };

        #endregion

        #region Built-in Functions

        private static readonly string[] Functions =
        {
            // Aggregate
            "COUNT", "SUM", "AVG", "MIN", "MAX",
            "COUNT_BIG", "STDEV", "STDEVP", "VAR", "VARP",
            "GROUPING", "GROUPING_ID", "CHECKSUM_AGG",
            "STRING_AGG", "APPROX_COUNT_DISTINCT",

            // Window / ranking
            "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE",
            "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE",
            "CUME_DIST", "PERCENT_RANK",

            // String
            "LEN", "DATALENGTH", "LEFT", "RIGHT", "SUBSTRING",
            "CHARINDEX", "PATINDEX", "REPLACE", "STUFF",
            "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM",
            "REPLICATE", "REVERSE", "SPACE", "STR",
            "CONCAT", "CONCAT_WS", "FORMAT",
            "STRING_SPLIT", "STRING_AGG", "STRING_ESCAPE",
            "TRANSLATE", "QUOTENAME", "UNICODE", "NCHAR",
            "ASCII", "CHAR", "SOUNDEX", "DIFFERENCE",

            // Date / time
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME",
            "SYSDATETIMEOFFSET", "CURRENT_TIMESTAMP",
            "DATEADD", "DATEDIFF", "DATEDIFF_BIG",
            "DATEPART", "DATENAME", "DAY", "MONTH", "YEAR",
            "EOMONTH", "DATEFROMPARTS", "DATETIME2FROMPARTS",
            "DATETIMEFROMPARTS", "SMALLDATETIMEFROMPARTS",
            "TIMEFROMPARTS", "DATETIMEOFFSETFROMPARTS",
            "ISDATE", "SWITCHOFFSET", "TODATETIMEOFFSET",

            // Conversion
            "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT",
            "PARSE", "TRY_PARSE",

            // Logical / conditional
            "IIF", "CHOOSE", "ISNULL", "COALESCE", "NULLIF",

            // Math
            "ABS", "CEILING", "FLOOR", "ROUND",
            "POWER", "SQRT", "SIGN", "RAND",
            "LOG", "LOG10", "EXP",
            "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "ATN2",
            "PI", "DEGREES", "RADIANS",

            // System / metadata
            "NEWID", "NEWSEQUENTIALID",
            "SCOPE_IDENTITY", "@@IDENTITY", "IDENT_CURRENT",
            "@@ROWCOUNT", "@@ERROR", "@@TRANCOUNT",
            "@@SPID", "@@SERVERNAME", "@@VERSION",
            "DB_ID", "DB_NAME", "OBJECT_ID", "OBJECT_NAME",
            "OBJECT_DEFINITION", "SCHEMA_ID", "SCHEMA_NAME",
            "TYPE_ID", "TYPE_NAME",
            "COL_NAME", "COL_LENGTH", "COLUMNPROPERTY",
            "TABLE_NAME", "INDEX_COL", "INDEXKEY_PROPERTY",
            "SERVERPROPERTY", "DATABASEPROPERTYEX",
            "USER_ID", "USER_NAME", "SUSER_ID", "SUSER_NAME",
            "SUSER_SNAME", "SYSTEM_USER", "SESSION_USER", "CURRENT_USER",
            "HOST_NAME", "APP_NAME", "ORIGINAL_LOGIN",
            "HAS_PERMS_BY_NAME", "IS_MEMBER", "IS_ROLEMEMBER",
            "PERMISSIONS",

            // Security
            "ENCRYPTBYKEY", "DECRYPTBYKEY",
            "HASHBYTES", "CERTENCODED", "CERTPRIVATEKEY",

            // Error handling
            "ERROR_NUMBER", "ERROR_MESSAGE", "ERROR_SEVERITY",
            "ERROR_STATE", "ERROR_LINE", "ERROR_PROCEDURE",

            // JSON
            "JSON_VALUE", "JSON_QUERY", "JSON_MODIFY",
            "ISJSON", "OPENJSON",

            // XML
            "OPENXML",

            // Misc
            "FORMATMESSAGE", "COMPRESS", "DECOMPRESS",
            "GREATEST", "LEAST",
            "GENERATE_SERIES", "DATE_BUCKET",
        };

        #endregion

        #region System Stored Procedures

        private static readonly string[] SystemProcedures =
        {
            "sp_help", "sp_helptext", "sp_helpdb", "sp_helpindex",
            "sp_who", "sp_who2", "sp_lock",
            "sp_columns", "sp_tables", "sp_stored_procedures",
            "sp_depends", "sp_rename", "sp_executesql",
            "sp_addrolemember", "sp_droprolemember",
            "sp_configure", "sp_spaceused",
            "sp_updatestats", "sp_recompile",
            "sp_refreshview", "sp_getapplock", "sp_releaseapplock",
            "sp_MSforeachtable", "sp_MSforeachdb",
            "xp_cmdshell", "xp_fixeddrives", "xp_logininfo",
        };

        #endregion

        #region SET Options

        private static readonly string[] SetOptions =
        {
            "SET NOCOUNT ON", "SET NOCOUNT OFF",
            "SET XACT_ABORT ON", "SET XACT_ABORT OFF",
            "SET ANSI_NULLS ON", "SET ANSI_NULLS OFF",
            "SET QUOTED_IDENTIFIER ON", "SET QUOTED_IDENTIFIER OFF",
            "SET ANSI_PADDING ON", "SET ANSI_PADDING OFF",
            "SET ANSI_WARNINGS ON", "SET ANSI_WARNINGS OFF",
            "SET ARITHABORT ON", "SET ARITHABORT OFF",
            "SET CONCAT_NULL_YIELDS_NULL ON", "SET CONCAT_NULL_YIELDS_NULL OFF",
            "SET NUMERIC_ROUNDABORT ON", "SET NUMERIC_ROUNDABORT OFF",
            "SET STATISTICS IO ON", "SET STATISTICS IO OFF",
            "SET STATISTICS TIME ON", "SET STATISTICS TIME OFF",
            "SET STATISTICS XML ON", "SET STATISTICS XML OFF",
            "SET SHOWPLAN_ALL ON", "SET SHOWPLAN_ALL OFF",
            "SET SHOWPLAN_TEXT ON", "SET SHOWPLAN_TEXT OFF",
            "SET SHOWPLAN_XML ON", "SET SHOWPLAN_XML OFF",
            "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED",
            "SET TRANSACTION ISOLATION LEVEL READ COMMITTED",
            "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ",
            "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            "SET TRANSACTION ISOLATION LEVEL SNAPSHOT",
            "SET IDENTITY_INSERT",
            "SET DATEFORMAT",
            "SET LANGUAGE",
            "SET LOCK_TIMEOUT",
            "SET DEADLOCK_PRIORITY",
            "SET ROWCOUNT",
        };

        #endregion

        // ──────────────────────────────────────────────
        //  Pre-built completion items (allocated once)
        // ──────────────────────────────────────────────
        private static readonly CompletionItemData[] AllItems;

        /// <summary>
        /// Static constructor — builds every <see cref="CompletionItemData"/> once at
        /// class-load time so that <see cref="GetMatches"/> and <see cref="GetAll"/>
        /// never allocate item objects.
        /// </summary>
        static SqlKeywordProvider()
        {
            var list = new List<CompletionItemData>(
                Keywords.Length + DataTypes.Length + Functions.Length +
                SystemProcedures.Length + SetOptions.Length);

            foreach (var kw in Keywords)
                list.Add(MakeItem(kw, CompletionItemKind.Keyword, PriorityKeyword, "Keyword"));

            foreach (var dt in DataTypes)
                list.Add(MakeItem(dt, CompletionItemKind.DataType, PriorityDataType, "DataType"));

            foreach (var fn in Functions)
                list.Add(MakeItem(fn, CompletionItemKind.Function, PriorityFunction, "Function"));

            foreach (var sp in SystemProcedures)
                list.Add(MakeItem(sp, CompletionItemKind.Procedure, PrioritySysSp, "Procedure"));

            foreach (var so in SetOptions)
                list.Add(MakeItem(so, CompletionItemKind.Keyword, PrioritySetOpt, "SetOption"));

            AllItems = list.ToArray();
        }

        // ──────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────

        /// <summary>
        /// Returns every pre-built completion item. The array is shared and must not be
        /// mutated by callers.
        /// </summary>
        public static IReadOnlyList<CompletionItemData> GetAll() => AllItems;

        /// <summary>
        /// Returns completion items whose <see cref="CompletionItemData.Text"/> starts
        /// with <paramref name="prefix"/> (case-insensitive).
        /// </summary>
        public static IEnumerable<CompletionItemData> GetMatches(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return Array.Empty<CompletionItemData>();

            // Linear scan is fast enough for ~400 static items; avoids dictionary overhead.
            var results = new List<CompletionItemData>();
            for (int i = 0; i < AllItems.Length; i++)
            {
                if (AllItems[i].Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(AllItems[i]);
            }
            return results;
        }

        // ──────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────

        private static CompletionItemData MakeItem(
            string text, CompletionItemKind kind, int priority, string iconKey)
        {
            return new CompletionItemData
            {
                Text = text,
                InsertText = text,
                Description = $"{kind}: {text}",
                Kind = kind,
                Priority = priority,
                IconKey = iconKey
            };
        }
    }
}
