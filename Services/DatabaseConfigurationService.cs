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
            PasswordConfigured = !string.IsNullOrWhiteSpace(fallback.Password)
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
        catch (Exception exception)
        {
            return new DatabaseConfigurationResult
            {
                Success = false,
                Message = $"No fue posible conectar: {exception.Message}",
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
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(runtimeConfiguration, _jsonOptions));

        return new DatabaseConfigurationResult
        {
            Success = true,
            Message = "Configuracion guardada. Reinicia el contenedor para aplicar el cambio de repositorio.",
            RequiresRestart = true
        };
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
        return new RuntimeDatabaseSetup
        {
            Provider = NormalizeProvider(_currentOptions.Provider),
            Host = "",
            Port = _currentOptions.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase) ? 3306 : 5432,
            DatabaseName = "",
            Username = "",
            SslMode = "Disable",
            AutoInitialize = _currentOptions.AutoInitialize
        };
    }

    private DatabaseConfigurationInput ResolvePassword(DatabaseConfigurationInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            return input;
        }

        var configured = LoadRuntimeSetup();
        if (configured is null || string.IsNullOrWhiteSpace(configured.Password))
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
            return $"Server={input.Host.Trim()};Port={input.Port};Database={input.DatabaseName.Trim()};User ID={input.Username.Trim()};Password={password};";
        }

        var sslMode = string.IsNullOrWhiteSpace(input.SslMode) ? "Disable" : input.SslMode.Trim();
        return $"Host={input.Host.Trim()};Port={input.Port};Database={input.DatabaseName.Trim()};Username={input.Username.Trim()};Password={password};SSL Mode={sslMode};Include Error Detail=true";
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
        public bool AutoInitialize { get; init; } = true;
    }
}
