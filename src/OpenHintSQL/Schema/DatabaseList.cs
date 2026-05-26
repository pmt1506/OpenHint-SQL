using System;
using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// In-memory list of databases visible from the active SQL Server connection.
    /// </summary>
    internal sealed class DatabaseList
    {
        public List<string> Names { get; } = new List<string>();

        public bool IsLoaded { get; set; }

        public DateTime LoadedAt { get; set; }

        public string LoadError { get; set; }

        public static DatabaseList Empty
        {
            get { return new DatabaseList { IsLoaded = false }; }
        }
    }
}
