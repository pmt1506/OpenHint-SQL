using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Non-blocking cache for server-level database name completion.
    /// </summary>
    internal static class DatabaseListCache
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(15);

        private static readonly ConcurrentDictionary<string, DatabaseList> _cache
            = new ConcurrentDictionary<string, DatabaseList>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, Task> _loadingTasks
            = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DatabaseListLoadFailure> _loadFailures
            = new ConcurrentDictionary<string, DatabaseListLoadFailure>(StringComparer.OrdinalIgnoreCase);

        public static event Action<string, DatabaseList> OnDatabasesLoaded;

        public static event Action<string, string> OnDatabaseListLoadFailed;

        private sealed class DatabaseListLoadFailure
        {
            public string Message { get; set; }
            public DateTime FailedAt { get; set; }
        }

        public static DatabaseList GetOrLoad(string server, string connectionString)
        {
            var key = BuildKey(server, connectionString);

            if (_cache.TryGetValue(key, out var cached) && cached.IsLoaded)
            {
                if ((DateTime.UtcNow - cached.LoadedAt) < CacheTtl)
                    return cached;

                Logger.Diagnostic($"Database list stale for [{key}], triggering background refresh");
                StartBackgroundLoad(key, connectionString);
                return cached;
            }

            if (TryGetRecentFailure(key, out _))
                return DatabaseList.Empty;

            Logger.Diagnostic($"Database list cache miss for [{key}], starting background load");
            StartBackgroundLoad(key, connectionString);
            return DatabaseList.Empty;
        }

        public static string GetLastLoadError(string server, string connectionString)
        {
            var key = BuildKey(server, connectionString);
            return TryGetRecentFailure(key, out var failure) ? failure.Message : null;
        }

        public static void Clear()
        {
            _cache.Clear();
            _loadingTasks.Clear();
            _loadFailures.Clear();
            Logger.Log("Database list cache cleared");
        }

        private static void StartBackgroundLoad(string key, string connectionString)
        {
            _loadingTasks.GetOrAdd(key, k =>
            {
                _loadFailures.TryRemove(k, out _);

                return Task.Run(async () =>
                {
                    try
                    {
                        await LoadDatabaseListAsync(k, connectionString).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Background database list load failed", ex);
                        RecordLoadFailure(k, ex.Message);
                    }
                    finally
                    {
                        _loadingTasks.TryRemove(k, out _);
                    }
                });
            });
        }

        private static async Task LoadDatabaseListAsync(string key, string connectionString)
        {
            Logger.Diagnostic($"Loading database list for [{key}] from server...");
            var databases = await AsyncDatabaseLoader.LoadAsync(connectionString).ConfigureAwait(false);

            if (databases.IsLoaded)
            {
                _cache[key] = databases;
                _loadFailures.TryRemove(key, out _);
                NotifyDatabasesLoaded(key, databases);
            }
            else
            {
                Logger.Warn("Database list load returned empty/failed");
                RecordLoadFailure(key, databases.LoadError);
            }
        }

        private static bool TryGetRecentFailure(string key, out DatabaseListLoadFailure failure)
        {
            if (_loadFailures.TryGetValue(key, out failure))
            {
                if ((DateTime.UtcNow - failure.FailedAt) < FailureRetryDelay)
                    return true;

                _loadFailures.TryRemove(key, out _);
            }

            failure = null;
            return false;
        }

        private static void RecordLoadFailure(string key, string message)
        {
            var safeMessage = string.IsNullOrWhiteSpace(message)
                ? "Could not load database list from the active server"
                : message;

            _loadFailures[key] = new DatabaseListLoadFailure
            {
                Message = safeMessage,
                FailedAt = DateTime.UtcNow
            };

            NotifyDatabaseListLoadFailed(key, safeMessage);
        }

        private static void NotifyDatabasesLoaded(string key, DatabaseList databases)
        {
            try
            {
                OnDatabasesLoaded?.Invoke(key, databases);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnDatabasesLoaded handler", ex);
            }
        }

        private static void NotifyDatabaseListLoadFailed(string key, string message)
        {
            try
            {
                OnDatabaseListLoadFailed?.Invoke(key, message);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnDatabaseListLoadFailed handler", ex);
            }
        }

        private static string BuildKey(string server, string connectionString)
        {
            return $"{server}|{BuildConnectionFingerprint(connectionString)}";
        }

        private static string BuildConnectionFingerprint(string connectionString)
        {
            return ConnectionStringFingerprint.Build(
                connectionString,
                "Initial Catalog",
                "Database",
                "AttachDBFilename");
        }
    }
}
