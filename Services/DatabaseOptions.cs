namespace OpenCredential.AdminWeb.Services;

public sealed class DatabaseOptions
{
    public string Mode { get; set; } = "Json";
    public string Provider { get; set; } = "PostgreSql";
    public string ConnectionString { get; set; } = string.Empty;
    public bool AutoInitialize { get; set; } = true;
}
