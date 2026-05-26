using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Builds stable cache fingerprints from connection strings without retaining
    /// password material in memory cache keys, logs, or disk cache filenames.
    /// </summary>
    internal static class ConnectionStringFingerprint
    {
        private static readonly string[] RuntimeOnlyKeys =
        {
            "Application Name",
            "Connect Timeout",
            "Connection Timeout",
            "Pooling",
            "Min Pool Size",
            "Max Pool Size"
        };

        private static readonly HashSet<string> SecretKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Password",
                "PWD",
                "Persist Security Info"
            };

        public static string Build(string connectionString, params string[] additionalKeysToRemove)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return "no-connection";

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                RemoveKeys(builder, RuntimeOnlyKeys);
                RemoveKeys(builder, additionalKeysToRemove);
                RemoveSecrets(builder);

                return "cs-" + Hash(builder.ConnectionString);
            }
            catch
            {
                return "cs-" + Hash(RemoveSecretTokens(connectionString));
            }
        }

        private static void RemoveKeys(SqlConnectionStringBuilder builder, IEnumerable<string> keys)
        {
            if (builder == null || keys == null)
                return;

            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    builder.Remove(key);
            }
        }

        private static void RemoveSecrets(SqlConnectionStringBuilder builder)
        {
            RemoveKeys(builder, SecretKeys);
        }

        private static string RemoveSecretTokens(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var parts = connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();

            foreach (var part in parts)
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    kept.Add(part);
                    continue;
                }

                var key = part.Substring(0, separatorIndex).Trim();
                if (!SecretKeys.Contains(key))
                    kept.Add(part);
            }

            return string.Join(";", kept);
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
