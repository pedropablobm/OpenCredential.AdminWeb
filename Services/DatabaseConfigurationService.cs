using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;

namespace OpenCredential.AdminWeb.Services;

public interface IDatabaseConfigurationService
{
    DatabaseConfigurationResponse GetConfiguration();
    Task<DatabaseConfigurationResult> TestConnectionAsync(DatabaseConfigurationInput input);
    Task<DatabaseConfigurationResult> SaveConfigurationAsync(DatabaseConfigurationInput input);
    Task<DatabaseConfigurationResult> ApplySchemaAsync(DatabaseConfigurationInput input);
}

public sealed class DatabaseConfigurationService : IDatabaseConfigurationService
{
    public const string RuntimeConfigurationFileName = "adminweb-runtime.json";

    private readonly DatabaseOptions _currentOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DatabaseConfigurationService(IOptions<DatabaseOptions> options, IWebHostEnvironment environment)
    {
        _currentOptions = options.Value;
        _environment = environment;
    }

    public DatabaseConfigurationResponse GetConfiguration()
    {
        var configured = LoadRuntimeSetup();
        var fallback = configured ?? ReadFromCurrentOptions();

        return new DatabaseConfigurationResponse
        {
            SqlEnabled = _currentOptions.Mode.Equals("sql", StringComparison.OrdinalIgnoreCase),
            Provider = fallback.Provider,
            Host = fallback.Host,
            Port = fallback.Port,
            DatabaseName = fallback.DatabaseName,
            Username = fallback.Username,
            SslMode = fallback.SslMode,
            AutoInitialize = fallback.AutoInitialize,
            RuntimeConfigurationExists = configured is not null,
            RequiresRestart = false,
            PasswordConfigured = configured is not null && !string.IsNullOrEmpty(configured.Password),
            ConnectionDetailsAvailable = configured is not null || fallback.ConnectionDetailsAvailable,
            RootCertificate = fallback.RootCertificate
        };
    }

    public async Task<DatabaseConfigurationResult> TestConnectionAsync(DatabaseConfigurationInput input)
    {
        try
        {
            var effectiveInput = ResolvePassword(input);
            await using var connection = CreateConnection(effectiveInput);
            await connection.OpenAsync();
            return new DatabaseConfigurationResult
            {
                Success = true,
                Message = "Conexion exitosa.",
                RequiresRestart = false
            };
        }
        catch (Exception)
        {
            return new DatabaseConfigurationResult
            {
                Success = false,
                Message = "No fue posible conectar. Revisa servidor, puerto, credenciales y TLS.",
                RequiresRestart = false
            };
        }
    }

    public async Task<DatabaseConfigurationResult> SaveConfigurationAsync(DatabaseConfigurationInput input)
    {
        var effectiveInput = ResolvePassword(input);
        var test = await TestConnectionAsync(effectiveInput);
        if (!test.Success)
        {
            return test;
        }

        var runtimeConfiguration = new RuntimeConfigurationDocument
        {
            Database = new DatabaseOptions
            {
                Mode = "Sql",
                Provider = NormalizeProvider(effectiveInput.Provider),
                ConnectionString = BuildConnectionString(effectiveInput),
                AutoInitialize = effectiveInput.AutoInitialize
            },
            DatabaseSetup = NormalizeInput(effectiveInput)
        };

        var path = GetRuntimeConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(runtimeConfiguration, _jsonOptions));
        File.Move(temporaryPath, path, overwrite: true);

        return new DatabaseConfigurationResult
        {
            Success = true,
            Message = "Configuracion guardada. Reinicia el contenedor para aplicar el cambio de repositorio.",
            RequiresRestart = true
        };
    }

    public async Task<DatabaseConfigurationResult> ApplySchemaAsync(DatabaseConfigurationInput input)
    {
        try
        {
            var effectiveInput = ResolvePassword(input);
            await using var connection = CreateConnection(effectiveInput);
            await connection.OpenAsync();
            DatabaseSchemaManager.ApplySchema(connection, NormalizeProvider(effectiveInput.Provider).Equals("PostgreSql", StringComparison.OrdinalIgnoreCase));

            return new DatabaseConfigurationResult
            {
                Success = true,
                Message = "Tablas y columnas adicionales de OpenCredential AdminWeb verificadas y ajustadas correctamente.",
                RequiresRestart = false
            };
        }
        catch (Exception)
        {
            return new DatabaseConfigurationResult
            {
                Success = false,
                Message = "No fue posible ajustar el esquema. Revisa permisos y compatibilidad.",
                RequiresRestart = false
            };
        }
    }

    private RuntimeDatabaseSetup? LoadRuntimeSetup()
    {
        var path = GetRuntimeConfigurationPath();
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<RuntimeConfigurationDocument>(json, _jsonOptions);
        return document?.DatabaseSetup;
    }

    private RuntimeDatabaseSetup ReadFromCurrentOptions()
    {
        if (_currentOptions.Mode.Equals("sql", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_currentOptions.ConnectionString))
        {
            try
            {
                if (NormalizeProvider(_currentOptions.Provider) == "MySql")
                {
                    var mysql = new MySqlConnectionStringBuilder(_currentOptions.ConnectionString);
                    return new RuntimeDatabaseSetup
                    {
                        Provider = "MySql", Host = mysql.Server, Port = checked((int)mysql.Port),
                        DatabaseName = mysql.Database, Username = mysql.UserID, SslMode = mysql.SslMode.ToString(),
                        AutoInitialize = _currentOptions.AutoInitialize, ConnectionDetailsAvailable = true
                    };
                }
                var postgres = new NpgsqlConnectionStringBuilder(_currentOptions.ConnectionString);
                return new RuntimeDatabaseSetup
                {
                    Provider = "PostgreSql", Host = postgres.Host ?? "", Port = postgres.Port,
                    DatabaseName = postgres.Database ?? "", Username = postgres.Username ?? "",
                    SslMode = postgres.SslMode.ToString(), RootCertificate = postgres.RootCertificate,
                    AutoInitialize = _currentOptions.AutoInitialize, ConnectionDetailsAvailable = true
                };
            }
            catch (ArgumentException) { }
        }
        return new RuntimeDatabaseSetup
        {
            Provider = NormalizeProvider(_currentOptions.Provider),
            Host = "",
            Port = _currentOptions.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase) ? 3306 : 5432,
            DatabaseName = "",
            Username = "",
            SslMode = "",
            AutoInitialize = _currentOptions.AutoInitialize
        };
    }

    private DatabaseConfigurationInput ResolvePassword(DatabaseConfigurationInput input)
    {
        if (!string.IsNullOrEmpty(input.Password))
        {
            return input;
        }

        var configured = LoadRuntimeSetup();
        if (configured is null || string.IsNullOrEmpty(configured.Password) ||
            !string.Equals(NormalizeProvider(input.Provider), NormalizeProvider(configured.Provider), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(input.Host.Trim(), configured.Host.Trim(), StringComparison.OrdinalIgnoreCase) ||
            input.Port != configured.Port ||
            !string.Equals(input.DatabaseName.Trim(), configured.DatabaseName.Trim(), StringComparison.Ordinal) ||
            !string.Equals(input.Username.Trim(), configured.Username.Trim(), StringComparison.Ordinal))
        {
            return input;
        }

        return new DatabaseConfigurationInput
        {
            Provider = input.Provider,
            Host = input.Host,
            Port = input.Port,
            DatabaseName = input.DatabaseName,
            Username = input.Username,
            Password = configured.Password,
            SslMode = input.SslMode,
            RootCertificate = input.RootCertificate,
            AutoInitialize = input.AutoInitialize
        };
    }

    private DbConnection CreateConnection(DatabaseConfigurationInput input)
    {
        var provider = NormalizeProvider(input.Provider);
        DbConnection connection = provider.Equals("MySql", StringComparison.OrdinalIgnoreCase)
            ? new MySqlConnection(BuildConnectionString(input))
            : new NpgsqlConnection(BuildConnectionString(input));
        return connection;
    }

    private RuntimeDatabaseSetup NormalizeInput(DatabaseConfigurationInput input)
    {
        return new RuntimeDatabaseSetup
        {
            Provider = NormalizeProvider(input.Provider),
            Host = input.Host.Trim(),
            Port = input.Port,
            DatabaseName = input.DatabaseName.Trim(),
            Username = input.Username.Trim(),
            SslMode = string.IsNullOrWhiteSpace(input.SslMode) ? "Disable" : input.SslMode.Trim(),
            RootCertificate = string.IsNullOrWhiteSpace(input.RootCertificate) ? null : input.RootCertificate.Trim(),
            AutoInitialize = input.AutoInitialize,
            Password = input.Password ?? string.Empty
        };
    }

    private static string BuildConnectionString(DatabaseConfigurationInput input)
    {
        var provider = NormalizeProvider(input.Provider);
        var password = input.Password ?? string.Empty;
        if (provider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
        {
            return new MySqlConnectionStringBuilder
            {
                Server = input.Host.Trim(), Port = checked((uint)input.Port), Database = input.DatabaseName.Trim(),
                UserID = input.Username.Trim(), Password = password
            }.ConnectionString;
        }

        var sslMode = string.IsNullOrWhiteSpace(input.SslMode) ? "Disable" : input.SslMode.Trim();
        if (!Enum.TryParse<SslMode>(sslMode, true, out var parsedSslMode) || !Enum.IsDefined(parsedSslMode))
            throw new ArgumentException("Modo TLS de PostgreSQL no valido.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = input.Host.Trim(), Port = input.Port, Database = input.DatabaseName.Trim(),
            Username = input.Username.Trim(), Password = password, SslMode = parsedSslMode
        };
        if (!string.IsNullOrWhiteSpace(input.RootCertificate)) builder.RootCertificate = input.RootCertificate.Trim();
        return builder.ConnectionString;
    }

    private static string NormalizeProvider(string? provider)
    {
        return provider?.Equals("MySql", StringComparison.OrdinalIgnoreCase) == true ? "MySql" : "PostgreSql";
    }

    private string GetRuntimeConfigurationPath()
    {
        return Path.Combine(RepositorySupport.ResolveDataDirectory(_environment), RuntimeConfigurationFileName);
    }

    private sealed class RuntimeConfigurationDocument
    {
        public DatabaseOptions Database { get; init; } = new();
        public RuntimeDatabaseSetup DatabaseSetup { get; init; } = new();
    }

    private sealed class RuntimeDatabaseSetup
    {
        public string Provider { get; init; } = "PostgreSql";
        public string Host { get; init; } = "";
        public int Port { get; init; } = 5432;
        public string DatabaseName { get; init; } = "";
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";
        public string SslMode { get; init; } = "Disable";
        public string? RootCertificate { get; init; }
        public bool ConnectionDetailsAvailable { get; init; }
        public bool AutoInitialize { get; init; } = true;
    }
}
