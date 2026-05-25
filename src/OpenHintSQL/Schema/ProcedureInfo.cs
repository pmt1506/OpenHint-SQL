namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a stored procedure or function in the database schema.
    /// </summary>
    public class ProcedureInfo
    {
        /// <summary>Schema name (e.g. dbo).</summary>
        public string SchemaName { get; set; }

        /// <summary>Object name (e.g. usp_GetCustomers).</summary>
        public string Name { get; set; }

        /// <summary>Object type description: "PROCEDURE", "FUNCTION", "INLINE_TABLE_VALUED_FUNCTION", etc.</summary>
        public string ObjectType { get; set; }

        /// <summary>Schema-qualified name (e.g. dbo.usp_GetCustomers).</summary>
        public string FullName => $"{SchemaName}.{Name}";
    }
}
