using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Loads database names visible to the active SQL Server login.
    /// </summary>
    internal static class AsyncDatabaseLoader
    {
        private const string DatabaseListQuery = @"
SELECT name
FROM sys.databases
WHERE state = 0
ORDER BY
    CASE WHEN database_id > 4 THEN 0 ELSE 1 END,
    name;";

        public static async Task<DatabaseList> LoadAsync(string connectionString)
        {
            var databases = new DatabaseList();

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    using (var command = new SqlCommand(DatabaseListQuery, connection))
                    {
                        command.CommandTimeout = 30;

                        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                databases.Names.Add(reader.GetString(0));
                            }
                        }
                    }
                }

                databases.IsLoaded = true;
                databases.LoadedAt = DateTime.UtcNow;
                Logger.Log($"Database list loaded: {databases.Names.Count} database(s)");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load database list", ex);
                databases.LoadError = ex.Message;
            }

            return databases;
        }
    }
}
