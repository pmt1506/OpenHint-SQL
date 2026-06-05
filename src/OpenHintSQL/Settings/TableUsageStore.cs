using System.Collections.Generic;

namespace OpenHintSQL.Settings
{
    internal sealed class TableUsageStore
    {
        public Dictionary<string, int> TableUsage { get; set; } =
            new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
    }
}
