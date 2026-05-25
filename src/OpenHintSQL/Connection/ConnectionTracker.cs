using System;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Connection
{
    /// <summary>
    /// Simple data class holding the active SSMS connection details.
    /// </summary>
    public class ConnectionInfo
    {
        /// <summary>SQL Server instance name (e.g. localhost, MYSERVER\SQL2019).</summary>
        public string Server { get; set; }

        /// <summary>Database name (e.g. master, AdventureWorks).</summary>
        public string Database { get; set; }

        /// <summary>ADO.NET connection string built from the SSMS connection.</summary>
        public string ConnectionString { get; set; }
    }

    /// <summary>
    /// Extracts the active query-window connection from SSMS internal APIs.
    /// Uses reflection so the project can compile without SSMS assemblies installed
    /// on the build machine.
    /// </summary>
    internal static class ConnectionTracker
    {
        /// <summary>
        /// Gets the connection info for the currently active SSMS query window.
        /// Returns null if no connection is available or if SSMS APIs cannot be accessed.
        /// This method is safe to call from any thread but should ideally be called on the UI thread.
        /// </summary>
        /// <returns>A <see cref="ConnectionInfo"/> or null.</returns>
        public static ConnectionInfo GetActiveConnection()
        {
            try
            {
                var serviceConnection = GetConnectionViaSqlEditorService();
                if (serviceConnection != null)
                    return serviceConnection;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SqlEditorService connection retrieval failed: {ex.Message}");
            }

            try
            {
                return GetConnectionViaReflection();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Reflection-based connection retrieval failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Preferred path for SSMS 21/22 and newer SSMS builds. The editor service
        /// knows the active query editor and exposes its live ADO.NET connection.
        /// </summary>
        private static ConnectionInfo GetConnectionViaSqlEditorService()
        {
            if (!ThreadHelper.CheckAccess())
            {
                return ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return GetConnectionViaSqlEditorServiceOnUiThread();
                });
            }

            return GetConnectionViaSqlEditorServiceOnUiThread();
        }

        private static ConnectionInfo GetConnectionViaSqlEditorServiceOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var serviceType = FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.SSqlEditorService");
            if (serviceType == null)
            {
                Logger.Warn("SSqlEditorService type not found");
                return null;
            }

            var interfaceType = FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ISqlEditorService");

            var editorService = ResolveVisualStudioService(serviceType, interfaceType);
            if (editorService == null)
            {
                Logger.Warn("SSqlEditorService returned null");
                return null;
            }

            Logger.Log($"SSqlEditorService resolved as {editorService.GetType().FullName}");

            var activeDetails = Invoke(editorService, interfaceType, "GetActiveEditorDetails");
            string moniker = GetPropertyValue(activeDetails, "Moniker") as string;
            if (string.IsNullOrEmpty(moniker))
                moniker = Invoke(editorService, interfaceType, "GetActiveEditorMoniker") as string;

            Logger.Log($"SSqlEditorService active moniker: {(string.IsNullOrEmpty(moniker) ? "<empty>" : moniker)}");

            object uiConnInfo = null;
            if (!string.IsNullOrEmpty(moniker))
                uiConnInfo = Invoke(editorService, interfaceType, "GetUIConnectionInfoForSpecificQueryEditor", moniker);

            var dbConnection = Invoke(editorService, interfaceType, "GetCurrentConnection") as IDbConnection;
            if (dbConnection == null && !string.IsNullOrEmpty(moniker))
                dbConnection = Invoke(editorService, interfaceType, "GetConnectionForSpecificQueryEditor", moniker) as IDbConnection;

            var info = BuildConnectionInfo(dbConnection, uiConnInfo);
            if (info != null)
            {
                Logger.Log($"Connection obtained (SqlEditorService): {info.Server} / {info.Database}");
                return info;
            }

            Logger.Warn("SqlEditorService did not expose an active connection");
            return null;
        }

        /// <summary>
        /// Uses reflection to access SSMS connection APIs that are loaded inside SSMS
        /// but may not be present on developer or CI machines.
        /// </summary>
        private static ConnectionInfo GetConnectionViaReflection()
        {
            var vsIntegrationAssembly = FindLoadedAssembly(
                "Microsoft.SqlServer.Management.UI.VSIntegration");

            if (vsIntegrationAssembly == null)
            {
                Logger.Warn("VSIntegration assembly not loaded");
                return null;
            }

            var serviceCacheType = vsIntegrationAssembly.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
            if (serviceCacheType == null)
            {
                Logger.Warn("ServiceCache type not found");
                return null;
            }

            var scriptFactoryProp = serviceCacheType.GetProperty(
                "ScriptFactory",
                BindingFlags.Public | BindingFlags.Static);
            var scriptFactory = scriptFactoryProp?.GetValue(null);
            if (scriptFactory == null)
            {
                Logger.Warn("ScriptFactory returned null via reflection");
                return null;
            }

            var connInfoWrapper = GetPropertyValue(scriptFactory, "CurrentlyActiveWndConnectionInfo");
            if (connInfoWrapper == null)
            {
                Logger.Warn("CurrentlyActiveWndConnectionInfo returned null");
                return null;
            }

            var uiConnInfo = GetPropertyValue(connInfoWrapper, "UIConnectionInfo");
            if (uiConnInfo == null)
            {
                Logger.Warn("UIConnectionInfo is null (reflection)");
                return null;
            }

            var info = BuildConnectionInfo(null, uiConnInfo);
            if (info != null)
            {
                Logger.Log($"Connection obtained (reflection): {info.Server} / {info.Database}");
                return info;
            }

            Logger.Warn("UIConnectionInfo could not be converted to a connection");
            return null;
        }

        private static ConnectionInfo BuildConnectionInfo(IDbConnection dbConnection, object uiConnInfo)
        {
            string server = null;
            string database = null;
            string connectionString = null;

            if (dbConnection != null)
            {
                server = GetFirstStringProperty(dbConnection, "DataSource");
                database = GetFirstStringProperty(dbConnection, "Database");
                connectionString = dbConnection.ConnectionString;
            }

            if (uiConnInfo != null)
            {
                server = server ?? GetFirstStringProperty(uiConnInfo, "ServerName", "Server", "DataSource");
                database = database ??
                    GetFirstStringProperty(uiConnInfo, "DatabaseName", "Database", "InitialCatalog") ??
                    GetAdvancedOption(uiConnInfo, "DATABASE", "Database", "Initial Catalog", "InitialCatalog");
            }

            if (string.IsNullOrEmpty(server))
            {
                Logger.Warn("Server name is empty");
                return null;
            }

            if (string.IsNullOrEmpty(database))
                database = "master";

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    var existing = new SqlConnectionStringBuilder(NormalizeConnectionString(connectionString))
                    {
                        DataSource = server,
                        InitialCatalog = database,
                        ApplicationName = "OpenHintSQL",
                        ConnectTimeout = 10
                    };

                    return new ConnectionInfo
                    {
                        Server = server,
                        Database = database,
                        ConnectionString = existing.ConnectionString
                    };
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not reuse active connection string: {ex.Message}");
                }
            }

            var userName = GetPropertyValue(uiConnInfo, "UserName") as string;
            var password = GetPropertyValue(uiConnInfo, "Password") as string;
            var authenticationType = Convert.ToString(GetPropertyValue(uiConnInfo, "AuthenticationType"));
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                ApplicationName = "OpenHintSQL",
                ConnectTimeout = 10
            };

            if (ShouldUseIntegratedSecurity(userName, password, authenticationType))
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = userName;
                builder.Password = password;
            }

            return new ConnectionInfo
            {
                Server = server,
                Database = database,
                ConnectionString = builder.ConnectionString
            };
        }

        private static string NormalizeConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            return connectionString
                .Replace("Multiple Active Result Sets", "MultipleActiveResultSets")
                .Replace("Trust Server Certificate", "TrustServerCertificate");
        }

        private static bool ShouldUseIntegratedSecurity(string userName, string password, string authenticationType)
        {
            if (!string.IsNullOrWhiteSpace(authenticationType))
            {
                if (authenticationType.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    authenticationType.IndexOf("integrated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (authenticationType.IndexOf("sql", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrEmpty(password))
                {
                    return false;
                }
            }

            // SSMS often keeps the Windows account in UserName and leaves Password empty.
            // Treat that as Windows Auth; otherwise System.Data.SqlClient attempts SQL Auth
            // with a domain-style login and schema loading fails.
            if (string.IsNullOrEmpty(password))
                return true;

            return string.IsNullOrEmpty(userName);
        }

        private static object ResolveVisualStudioService(Type serviceType, Type interfaceType)
        {
            var package = OpenHintSQLPackage.Instance;

            object service = null;
            if (package != null)
            {
                service = TryGetService("package marker", () => ((IServiceProvider)package).GetService(serviceType));
                if (service == null && interfaceType != null)
                    service = TryGetService("package interface", () => ((IServiceProvider)package).GetService(interfaceType));
            }
            else
            {
                Logger.Warn("OpenHintSQLPackage.Instance is null");
            }

            if (service == null)
                service = TryGetService("global marker", () => Package.GetGlobalService(serviceType));

            if (service == null && interfaceType != null)
                service = TryGetService("global interface", () => Package.GetGlobalService(interfaceType));

            if (service == null)
                service = TryGetService("global provider marker", () => Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(serviceType));

            if (service == null && interfaceType != null)
                service = TryGetService("global provider interface", () => Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(interfaceType));

            return service;
        }

        private static object TryGetService(string source, Func<object> getService)
        {
            try
            {
                var service = getService();
                Logger.Log($"{source}: {(service == null ? "null" : service.GetType().FullName)}");
                return service;
            }
            catch (Exception ex)
            {
                Logger.Warn($"{source} failed: {ex.Message}");
                return null;
            }
        }

        private static object Invoke(object instance, Type interfaceType, string methodName, params object[] args)
        {
            if (instance == null)
                return null;

            try
            {
                var method = interfaceType?.GetMethod(methodName);
                if (method != null && interfaceType.IsInstanceOfType(instance))
                    return method.Invoke(instance, args);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Interface invoke failed for {methodName}: {ex.Message}");
            }

            return Invoke(instance, methodName, args);
        }

        private static object Invoke(object instance, string methodName, params object[] args)
        {
            return instance?.GetType().GetMethod(methodName)?.Invoke(instance, args);
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            return instance?.GetType().GetProperty(propertyName)?.GetValue(instance);
        }

        private static string GetFirstStringProperty(object instance, params string[] propertyNames)
        {
            if (instance == null || propertyNames == null)
                return null;

            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(instance, propertyName) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static string GetAdvancedOption(object uiConnInfo, params string[] keys)
        {
            var advOptions = GetPropertyValue(uiConnInfo, "AdvancedOptions");
            if (advOptions == null)
                return null;

            var indexer = advOptions.GetType().GetProperty("Item", new[] { typeof(string) });
            if (indexer == null)
                return null;

            foreach (var key in keys)
            {
                try
                {
                    var value = indexer.GetValue(advOptions, new object[] { key }) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch
                {
                    // Some SSMS versions throw for unknown advanced option keys.
                }
            }

            return null;
        }

        /// <summary>
        /// Finds an already-loaded assembly by partial name.
        /// </summary>
        private static Assembly FindLoadedAssembly(string partialName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.FullName.StartsWith(partialName + ",", StringComparison.OrdinalIgnoreCase) ||
                        assembly.GetName().Name.Equals(partialName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch
                {
                    // Skip assemblies that throw on metadata access, such as dynamic assemblies.
                }
            }

            return null;
        }

        private static Type FindLoadedType(string fullTypeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullTypeName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Skip assemblies that throw on metadata access.
                }
            }

            return null;
        }
    }
}
