using System;
using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Thread-safe, non-blocking cache for database schemas.
    /// Returns cached schemas immediately or <see cref="DatabaseSchema.Empty"/>
    /// while a background load is in progress (never blocks the UI thread).
    /// </summary>
    internal static class SchemaCache
    {
        /// <summary>Cache TTL — schemas older than this are considered stale.</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        /// <summary>Cached schemas keyed by "server|database|connection-fingerprint".</summary>
        private static readonly ConcurrentDictionary<string, DatabaseSchema> _cache
            = new ConcurrentDictionary<string, DatabaseSchema>(StringComparer.OrdinalIgnoreCase);

        /// <summary>In-flight loading tasks for deduplication — prevents multiple simultaneous loads for the same key.</summary>
        private static readonly ConcurrentDictionary<string, Task> _loadingTasks
            = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised when a schema finishes loading in the background.
        /// Parameters: cache key (server|database|connection-fingerprint), loaded schema.
        /// </summary>
        public static event Action<string, DatabaseSchema> OnSchemaLoaded;

        /// <summary>
        /// Gets the cached schema for the given server/database, or returns
        /// <see cref="DatabaseSchema.Empty"/> and fires a background load.
        /// This method NEVER blocks.
        /// </summary>
        /// <param name="server">SQL Server instance name.</param>
        /// <param name="database">Database name.</param>
        /// <param name="connectionString">Connection string for loading.</param>
        /// <returns>The cached schema, or Empty if not yet loaded.</returns>
        public static DatabaseSchema GetOrLoad(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);

            // Check for a valid cached entry
            if (_cache.TryGetValue(key, out var cached) && cached.IsLoaded)
            {
                // Check TTL
                if ((DateTime.UtcNow - cached.LoadedAt) < CacheTtl)
                {
                    return cached;
                }

                // Stale — trigger background refresh but still return stale data
                Logger.Log($"Schema stale for [{key}], triggering background refresh");
                StartBackgroundLoad(key, connectionString, allowDiskCache: false);
                return cached;
            }

            // Cache miss — fire background load and return Empty immediately
            Logger.Log($"Schema cache miss for [{key}], starting background load");
            StartBackgroundLoad(key, connectionString, allowDiskCache: true);
            return DatabaseSchema.Empty;
        }

        /// <summary>
        /// Forces a refresh of the schema for the given server/database. Invalidates both
        /// the in-memory and on-disk caches so the next load is a live server query.
        /// </summary>
        public static Task RefreshAsync(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);
            Logger.Log($"Manual schema refresh requested for [{key}]");

            _cache.TryRemove(key, out _);
            SchemaPersister.Invalidate(key);

            return LoadSchemaAsync(key, connectionString, allowDiskCache: false);
        }

        /// <summary>
        /// Clears all cached schemas and cancels any in-flight information.
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
            _loadingTasks.Clear();
            Logger.Log("Schema cache cleared");
        }

        /// <summary>
        /// Starts a background schema load if one isn't already in progress for this key.
        /// </summary>
        private static void StartBackgroundLoad(string key, string connectionString, bool allowDiskCache)
        {
            // Use GetOrAdd to ensure only one load per key
            _loadingTasks.GetOrAdd(key, k =>
            {
                return Task.Run(async () =>
                {
                    try
                    {
                        await LoadSchemaAsync(k, connectionString, allowDiskCache).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Background schema load failed for [{k}]", ex);
                    }
                    finally
                    {
                        // Remove from in-flight tracking so future requests can retry
                        _loadingTasks.TryRemove(k, out _);
                    }
                });
            });
        }

        /// <summary>
        /// Performs the actual async schema load. Tries the on-disk persister first;
        /// only falls back to a live DB query if the disk cache is missing or stale.
        /// Successful DB loads are written back to disk so the next SSMS restart is fast.
        /// </summary>
        private static async Task LoadSchemaAsync(string key, string connectionString, bool allowDiskCache)
        {
            // 1. Fast path: disk cache. Only use this for cold cache misses.
            // Stale in-memory entries must refresh from the server, otherwise a
            // stale disk snapshot can reload itself forever and repeatedly retrigger
            // the completion popup.
            if (allowDiskCache)
            {
                var fromDisk = await SchemaPersister.TryLoadAsync(key).ConfigureAwait(false);
                if (fromDisk != null && fromDisk.IsLoaded)
                {
                    _cache[key] = fromDisk;
                    NotifySchemaLoaded(key, fromDisk);
                    return;
                }
            }

            // 2. Slow path: query the server.
            Logger.Log($"Loading schema for [{key}] from server...");
            var schema = await AsyncSchemaLoader.LoadAsync(connectionString).ConfigureAwait(false);

            if (schema.IsLoaded)
            {
                _cache[key] = schema;
                Logger.Log($"Schema cached for [{key}]: " +
                           $"{schema.Tables.Count} tables, " +
                           $"{schema.Views.Count} views, " +
                           $"{schema.Procedures.Count} procs");

                // Best-effort disk write; don't block notification on it.
                _ = SchemaPersister.TrySaveAsync(key, schema);

                NotifySchemaLoaded(key, schema);
            }
            else
            {
                Logger.Warn($"Schema load returned empty/failed for [{key}]");
            }
        }

        private static void NotifySchemaLoaded(string key, DatabaseSchema schema)
        {
            try
            {
                OnSchemaLoaded?.Invoke(key, schema);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnSchemaLoaded handler", ex);
            }
        }

        /// <summary>
        /// Builds a cache key from server, database, and the effective connection
        /// string. The connection string is hashed so credentials never appear in logs
        /// or on disk, while different active SSMS query-window connections do not
        /// share a schema cache accidentally.
        /// </summary>
        private static string BuildKey(string server, string database, string connectionString)
        {
            return $"{server}|{database}|{BuildConnectionFingerprint(connectionString)}";
        }

        private static string BuildConnectionFingerprint(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return "no-connection";

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);

                // Runtime-only knobs should not split the cache; identity/security knobs should.
                builder.Remove("Application Name");
                builder.Remove("Connect Timeout");
                builder.Remove("Connection Timeout");
                builder.Remove("Pooling");
                builder.Remove("Min Pool Size");
                builder.Remove("Max Pool Size");

                return "cs-" + Hash(builder.ConnectionString);
            }
            catch
            {
                return "cs-" + Hash(connectionString);
            }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
                var sb = new StringBuilder(10);
                for (int i = 0; i < 5; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
