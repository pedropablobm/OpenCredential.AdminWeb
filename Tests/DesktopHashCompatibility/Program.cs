using OpenCredential.AdminWeb.Services;
using OpenCredential.Plugin.DatabaseAuth;

const string password = " synthetic password with spaces ";
foreach (var method in PasswordHashService.SupportedMethods)
{
    var hash = PasswordHashService.HashPassword(password, method);
    var algorithm = Enum.Parse<PasswordHashAlgorithm>(method);
    var entry = new UserEntry("synthetic-user", algorithm, hash);
    if (!entry.VerifyPassword(password) || entry.VerifyPassword(password.Trim()) || entry.VerifyPassword("wrong"))
        throw new Exception($"Desktop no verifica correctamente el formato {method} generado por AdminWeb.");
    Console.WriteLine($"PASS Desktop UserEntry: {method}");
}
