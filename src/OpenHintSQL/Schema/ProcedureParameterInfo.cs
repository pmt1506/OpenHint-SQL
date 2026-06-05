namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Represents a stored procedure / function parameter.
    /// </summary>
    public class ProcedureParameterInfo
    {
        /// <summary>Parameter name including leading @.</summary>
        public string Name { get; set; }

        /// <summary>SQL data type name.</summary>
        public string DataType { get; set; }

        /// <summary>Ordinal position within the parameter list.</summary>
        public int OrdinalPosition { get; set; }

        /// <summary>Whether the parameter is OUTPUT.</summary>
        public bool IsOutput { get; set; }

        /// <summary>Human-readable parameter signature.</summary>
        public string DisplayText => IsOutput
            ? $"{Name} {DataType} OUTPUT"
            : $"{Name} {DataType}";
    }
}
