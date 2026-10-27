
using Microsoft.Data.SqlClient;

namespace DashBoard

{
    public class SqlConnectionFactory
    {

        private readonly string _connectionString;

        public SqlConnectionFactory(IConfiguration config) =>
            _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' not found.");

        public async Task<SqlConnection> OpenAsync()
        {
            var connection = new SqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;



            }

            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }
}
