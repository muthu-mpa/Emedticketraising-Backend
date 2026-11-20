using Npgsql;

namespace TeamTrack
{
    public static class DatabaseTarget
    {
        /// <summary>"host/database" for log lines. Never includes the user name or the password.</summary>
        public static string Describe(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return "(no connection string configured)";
            try
            {
                var b = new NpgsqlConnectionStringBuilder(connectionString);
                return $"{(string.IsNullOrEmpty(b.Host) ? "?" : b.Host)}/{(string.IsNullOrEmpty(b.Database) ? "?" : b.Database)}";
            }
            catch (ArgumentException)
            {
                return "(connection string could not be read)";
            }
        }
    }
}
