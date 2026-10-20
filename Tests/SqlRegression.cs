using Microsoft.Extensions.Options;
using Npgsql;
using MySqlConnector;
using System.Data.Common;
using OpenCredential.AdminWeb;
using OpenCredential.AdminWeb.Services;

internal static class SqlRegression
{
    public static async Task RunAsync(int port, bool mysql = false)
    {
        // Only run against an explicitly selected loopback disposable PostgreSQL instance.
        var database = "oc_regression_" + Guid.NewGuid().ToString("N");
        var connectionString = mysql ? $"Server=127.0.0.1;Port={port};User ID=root;SslMode=None;AllowPublicKeyRetrieval=true"
            : $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;Timezone=UTC";
        DbConnection Connect(string value) => mysql ? new MySqlConnection(value) : new NpgsqlConnection(value);
        using (var connection = Connect(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}";
            command.ExecuteNonQuery();
        }
        connectionString = mysql ? new MySqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString
            : new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
        var options = Options.Create(new DatabaseOptions { Mode = "Sql", Provider = mysql ? "MySql" : "PostgreSql", ConnectionString = connectionString });
        var repository = new SqlAdminRepository(options);
        Assert(repository.GetSnapshot().Users.Count == 0, "No debe crear cuentas demo.");
        repository = new SqlAdminRepository(options); // Idempotent migration.
        UserInput Input(string name, List<int>? groups = null) => new() { Username = name, FirstName = "Test", LastName = "User", Email = "test@example.invalid", DocumentId = "123", Active = true, Password = "Test passphrase 123!", GroupIds = groups };
        var user = repository.CreateUser(Input("student", [1]));
        using (var connection = Connect(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE users SET hash_method='MD5', password_hash=@hash WHERE id=@id";
            Add(command, "@hash", PasswordHashService.HashPassword("Legacy test", "MD5"));
            Add(command, "@id", user.Id);
            command.ExecuteNonQuery();
        }
        repository.UpdateUser(user.Id, new UserInput { Username = "student", FirstName = "Changed", LastName = "User", Email = "test@example.invalid", DocumentId = "123", Active = true });
        var stored = repository.FindUserByUsername("student")!;
        Assert(stored.HashMethod == "MD5" && PasswordHashService.VerifyPassword("Legacy test", stored.PasswordHash, stored.HashMethod), "Edicion preserva clave.");
        Assert(stored.Groups.Count == 1, "Edicion preserva grupos.");
        try { repository.CreateUser(Input("must-rollback", [99999])); throw new Exception("Debio rechazar grupo inexistente."); }
        catch (InvalidOperationException) { }
        Assert(repository.FindUserByUsername("must-rollback") is null, "Creacion debe revertirse al fallar grupos.");
        var users = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => repository.CreateUser(Input("parallel" + index)))));
        Assert(users.Select(item => item.Id).Distinct().Count() == users.Length, "IDs concurrentes deben ser unicos.");
        var recovery = repository.RecoverPortalPassword(new PortalPasswordRecoveryInput { Username = "student", Email = "test@example.invalid", DocumentId = "123" }, 20);
        var reset = new PortalPasswordResetWithTokenInput { Token = recovery.ResetToken!, NewPassword = "New passphrase 123!", ConfirmPassword = "New passphrase 123!" };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() => repository.ResetPortalPasswordWithToken(reset, out _))));
        Assert(results.Count(success => success) == 1, "Un token solo puede consumirse una vez incluso concurrentemente.");
        using (var connection = Connect(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = mysql
                ? "CREATE TABLE login_sessions (dbid BIGINT AUTO_INCREMENT PRIMARY KEY, username VARCHAR(128), machine VARCHAR(128), ipaddress VARCHAR(45), loginstamp DATETIME NOT NULL, logoutstamp DATETIME NULL); INSERT INTO login_sessions(username,machine,ipaddress,loginstamp,logoutstamp) VALUES ('student','pc-test','127.0.0.2',DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 HOUR),UTC_TIMESTAMP()), ('other-student','pc-test','127.0.0.2',DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 HOUR),UTC_TIMESTAMP())"
                : "CREATE TABLE login_sessions (dbid BIGSERIAL PRIMARY KEY, username VARCHAR(128), machine VARCHAR(128), ipaddress VARCHAR(45), loginstamp TIMESTAMP NOT NULL, logoutstamp TIMESTAMP NULL); INSERT INTO login_sessions(username,machine,ipaddress,loginstamp,logoutstamp) VALUES ('student','pc-test','127.0.0.2',CURRENT_TIMESTAMP - INTERVAL '1 hour',CURRENT_TIMESTAMP), ('other-student','pc-test','127.0.0.2',CURRENT_TIMESTAMP - INTERVAL '1 hour',CURRENT_TIMESTAMP)";
            command.ExecuteNonQuery();
        }
        repository = new SqlAdminRepository(options);
        Assert(repository.GetPortalSessions("student", 25).Count == 1, "Portal no debe devolver sesiones de nombres parecidos.");
        var dashboard = repository.GetDashboard(30, null, null, null);
        var reports = repository.GetReports(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, null, null, null, null, null, null);
        Assert(dashboard.Kpis.HoursInRange == reports.Kpis.TotalHours && dashboard.Kpis.HoursInRange == 2, "Dashboard e informes deben usar las mismas sesiones UTC.");
    }
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
