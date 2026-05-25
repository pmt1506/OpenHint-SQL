using System;
using System.Data.SqlClient;
using System.Reflection;
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
                return GetConnectionViaReflection();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Reflection-based connection retrieval failed: {ex.Message}");
                return null;
            }
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

            var server = GetPropertyValue(uiConnInfo, "ServerName") as string;
            if (string.IsNullOrEmpty(server))
            {
                Logger.Warn("Server name is empty (reflection)");
                return null;
            }

            var database = GetAdvancedOption(uiConnInfo, "DATABASE") ?? "master";
            var userName = GetPropertyValue(uiConnInfo, "UserName") as string;
            var password = GetPropertyValue(uiConnInfo, "Password") as string;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                ApplicationName = "OpenHintSQL",
                ConnectTimeout = 10
            };

            if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(userName))
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = userName;
                builder.Password = password;
            }

            Logger.Log($"Connection obtained (reflection): {server} / {database}");
            return new ConnectionInfo
            {
                Server = server,
                Database = database,
                ConnectionString = builder.ConnectionString
            };
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            return instance?.GetType().GetProperty(propertyName)?.GetValue(instance);
        }

        private static string GetAdvancedOption(object uiConnInfo, string key)
        {
            var advOptions = GetPropertyValue(uiConnInfo, "AdvancedOptions");
            if (advOptions == null)
                return null;

            var indexer = advOptions.GetType().GetProperty("Item", new[] { typeof(string) });
            return indexer?.GetValue(advOptions, new object[] { key }) as string;
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
    }
}
