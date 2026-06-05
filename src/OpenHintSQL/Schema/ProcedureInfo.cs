namespace OpenHintSQL.Schema
{
    using System.Collections.Generic;
    using System.Linq;

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

        /// <summary>Ordered list of parameters for this procedure/function.</summary>
        public List<ProcedureParameterInfo> Parameters { get; } = new List<ProcedureParameterInfo>();

        /// <summary>Schema-qualified name (e.g. dbo.usp_GetCustomers).</summary>
        public string FullName => $"{SchemaName}.{Name}";

        /// <summary>True when this object is any kind of SQL function.</summary>
        public bool IsFunction => !string.IsNullOrEmpty(ObjectType) && ObjectType.Contains("FUNCTION");

        /// <summary>Function/procedure signature for tooltips.</summary>
        public string Signature
        {
            get
            {
                var parameters = Parameters.Count == 0
                    ? string.Empty
                    : string.Join(", ", Parameters.OrderBy(p => p.OrdinalPosition).Select(p => p.DisplayText));
                return $"{FullName}({parameters})";
            }
        }
    }
}
