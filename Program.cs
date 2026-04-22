using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.IO;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenCredential.AdminWeb;
using OpenCredential.AdminWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
var adminAuthOptions = builder.Configuration.GetSection("AdminAuth").Get<AdminAuthOptions>() ?? new AdminAuthOptions();
var dataDirectory = RepositorySupport.ResolveDataDirectory(builder.Environment);
builder.Configuration.AddJsonFile(Path.Combine(dataDirectory, DatabaseConfigurationService.RuntimeConfigurationFileName), optional: true, reloadOnChange: false);
var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
if (databaseOptions.Mode.Equals("sql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAdminRepository, SqlAdminRepository>();
}
else
{
    builder.Services.AddSingleton<IAdminRepository, JsonAdminRepository>();
}
builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
builder.Services.AddSingleton<IDatabaseConfigurationService, DatabaseConfigurationService>();
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("OpenCredential.AdminWeb");
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = adminAuthOptions.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, adminAuthOptions.SessionHours));
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanViewAudit", policy => policy.RequireRole(AdminRoles.SuperAdmin));
    options.AddPolicy("CanManageAcademics", policy => policy.RequireRole(AdminRoles.SuperAdmin, AdminRoles.Coordinator));
    options.AddPolicy("CanManageUsers", policy => policy.RequireRole(AdminRoles.SuperAdmin, AdminRoles.Coordinator));
    options.AddPolicy("CanManageComputers", policy => policy.RequireRole(AdminRoles.SuperAdmin, AdminRoles.Operator));
    options.AddPolicy("CanManageUsage", policy => policy.RequireRole(AdminRoles.SuperAdmin, AdminRoles.Operator));
    options.AddPolicy("CanManageSettings", policy => policy.RequireRole(AdminRoles.SuperAdmin));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

static string GetActor(HttpContext context)
{
    return string.IsNullOrWhiteSpace(context.User.Identity?.Name) ? "anon" : context.User.Identity!.Name!;
}

static string? GetRemoteIp(HttpContext context)
{
    return context.Connection.RemoteIpAddress?.ToString();
}

static void Audit(HttpContext context, IAdminRepository repository, string action, string entityType, string entityKey, string summary)
{
    try
    {
        repository.RecordAudit(CreateAuditEntry(context, GetActor(context), action, entityType, entityKey, summary));
    }
    catch
    {
        // La auditoria no debe bloquear una operacion administrativa.
    }
}

static void TryAudit(HttpContext context, string actor, string action, string entityType, string entityKey, string summary)
{
    try
    {
        var repository = context.RequestServices.GetService<IAdminRepository>();
        repository?.RecordAudit(CreateAuditEntry(context, actor, action, entityType, entityKey, summary));
    }
    catch
    {
        // Permite recuperar configuraciones aunque la base configurada este caida.
    }
}

static AuditEntryInput CreateAuditEntry(HttpContext context, string actor, string action, string entityType, string entityKey, string summary)
{
    return new AuditEntryInput
    {
        ActorUsername = actor,
        Action = action,
        EntityType = entityType,
        EntityKey = entityKey,
        Summary = summary,
        RemoteIp = GetRemoteIp(context)
    };
}

app.MapPost("/api/auth/login", async (AdminLoginInput input, HttpContext context, IAdminAuthService authService, IOptions<AdminAuthOptions> authOptionsAccessor) =>
{
    var adminIdentity = authService.ValidateCredentials(input.Username, input.Password);
    if (adminIdentity is null)
    {
        TryAudit(context, string.IsNullOrWhiteSpace(input.Username) ? "anon" : input.Username.Trim(), "LoginFailed", "Security", "admin-console", "Intento fallido de acceso a la consola administrativa");
        return Results.Unauthorized();
    }

    var authOptions = authOptionsAccessor.Value;
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, adminIdentity.Username),
        new(ClaimTypes.Role, adminIdentity.Role)
    };

    var principal = new ClaimsPrincipal(
        new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Max(1, authOptions.SessionHours))
        });

    TryAudit(context, adminIdentity.Username, "Login", "Security", "admin-console", $"Inicio de sesion correcto en la consola administrativa con rol {adminIdentity.Role}");

    return Results.Ok(new AdminSessionInfo
    {
        Authenticated = true,
        Username = adminIdentity.Username,
        Role = adminIdentity.Role,
        AuthenticationEnabled = authService.IsEnabled
    });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    var actor = GetActor(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    TryAudit(context, actor, "Logout", "Security", "admin-console", "Cierre de sesion de la consola administrativa");
    return Results.NoContent();
});

app.MapGet("/api/auth/me", (HttpContext context, IAdminAuthService authService) =>
{
    if (context.User.Identity?.IsAuthenticated == true || !authService.IsEnabled)
    {
        var defaultIdentity = authService.GetDefaultIdentity();
        return Results.Ok(new AdminSessionInfo
        {
            Authenticated = true,
            Username = context.User.Identity?.Name ?? defaultIdentity.Username,
            Role = context.User.FindFirstValue(ClaimTypes.Role) ?? defaultIdentity.Role,
            AuthenticationEnabled = authService.IsEnabled
        });
    }

    return Results.Unauthorized();
});

var protectedApi = app.MapGroup("/api").RequireAuthorization();

protectedApi.MapGet("/summary", (IAdminRepository repository) => Results.Ok(repository.GetSnapshot()));
protectedApi.MapGet("/dashboard", (IAdminRepository repository, int? rangeDays, int? careerId, int? semesterId, string? status) =>
{
    return Results.Ok(repository.GetDashboard(rangeDays ?? 30, careerId, semesterId, status));
});
protectedApi.MapGet("/audit", (IAdminRepository repository, int? take) =>
{
    return Results.Ok(repository.GetAuditEntries(take ?? 50));
}).RequireAuthorization("CanViewAudit");

protectedApi.MapGet("/configuration/database", (IDatabaseConfigurationService configurationService) =>
{
    return Results.Ok(configurationService.GetConfiguration());
}).RequireAuthorization("CanManageSettings");

protectedApi.MapPost("/configuration/database/test", async (DatabaseConfigurationInput input, IDatabaseConfigurationService configurationService) =>
{
    return Results.Ok(await configurationService.TestConnectionAsync(input));
}).RequireAuthorization("CanManageSettings");

protectedApi.MapPut("/configuration/database", async (DatabaseConfigurationInput input, HttpContext context, IDatabaseConfigurationService configurationService) =>
{
    var result = await configurationService.SaveConfigurationAsync(input);
    if (result.Success)
    {
        TryAudit(context, GetActor(context), "UpdateDatabaseConfiguration", "Configuration", input.Provider, $"Configuracion de base de datos guardada para {input.Provider} en {input.Host}:{input.Port}.");
    }

    return Results.Ok(result);
}).RequireAuthorization("CanManageSettings");

protectedApi.MapPost("/import/users", async (HttpRequest request, HttpContext context, IAdminRepository repository) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Debe enviar un formulario multipart con el archivo plano." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "No se recibio ningun archivo." });
    }

    var result = await repository.ImportUsersAsync(file);
    Audit(context, repository, "ImportUsers", "Users", file.FileName, $"Importacion masiva. Importados: {result.Imported}. Actualizados: {result.Updated}.");
    return Results.Ok(result);
}).RequireAuthorization("CanManageUsers");

protectedApi.MapPost("/careers", (CareerInput input, HttpContext context, IAdminRepository repository) =>
{
    var career = repository.CreateCareer(input);
    Audit(context, repository, "CreateCareer", "Career", career.Id.ToString(), $"Creacion de carrera {career.Name}.");
    return Results.Ok(career);
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapPut("/careers/{id:int}", (int id, CareerInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.UpdateCareer(id, input) is { } career)
    {
        Audit(context, repository, "UpdateCareer", "Career", id.ToString(), $"Actualizacion de carrera {career.Name}.");
        return Results.Ok(career);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapDelete("/careers/{id:int}", (int id, HttpContext context, IAdminRepository repository) =>
{
    if (repository.DeleteCareer(id))
    {
        Audit(context, repository, "DeleteCareer", "Career", id.ToString(), $"Eliminacion de carrera {id}.");
        return Results.NoContent();
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapPost("/semesters", (SemesterInput input, HttpContext context, IAdminRepository repository) =>
{
    var semester = repository.CreateSemester(input);
    Audit(context, repository, "CreateSemester", "Semester", semester.Id.ToString(), $"Creacion de semestre {semester.Name}.");
    return Results.Ok(semester);
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapPut("/semesters/{id:int}", (int id, SemesterInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.UpdateSemester(id, input) is { } semester)
    {
        Audit(context, repository, "UpdateSemester", "Semester", id.ToString(), $"Actualizacion de semestre {semester.Name}.");
        return Results.Ok(semester);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapDelete("/semesters/{id:int}", (int id, HttpContext context, IAdminRepository repository) =>
{
    if (repository.DeleteSemester(id))
    {
        Audit(context, repository, "DeleteSemester", "Semester", id.ToString(), $"Eliminacion de semestre {id}.");
        return Results.NoContent();
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageAcademics");

protectedApi.MapPost("/computers", (ComputerInput input, HttpContext context, IAdminRepository repository) =>
{
    var computer = repository.CreateComputer(input);
    Audit(context, repository, "CreateComputer", "Computer", computer.Id.ToString(), $"Creacion de equipo {computer.Name}.");
    return Results.Ok(computer);
}).RequireAuthorization("CanManageComputers");

protectedApi.MapPut("/computers/{id:int}", (int id, ComputerInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.UpdateComputer(id, input) is { } computer)
    {
        Audit(context, repository, "UpdateComputer", "Computer", id.ToString(), $"Actualizacion de equipo {computer.Name}.");
        return Results.Ok(computer);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageComputers");

protectedApi.MapDelete("/computers/{id:int}", (int id, HttpContext context, IAdminRepository repository) =>
{
    if (repository.DeleteComputer(id))
    {
        Audit(context, repository, "DeleteComputer", "Computer", id.ToString(), $"Eliminacion de equipo {id}.");
        return Results.NoContent();
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageComputers");

protectedApi.MapPost("/users", (UserInput input, HttpContext context, IAdminRepository repository) =>
{
    var user = repository.CreateUser(input);
    Audit(context, repository, "CreateUser", "User", user.Id.ToString(), $"Creacion de usuario {user.Username}.");
    return Results.Ok(user);
}).RequireAuthorization("CanManageUsers");

protectedApi.MapPut("/users/{id:int}", (int id, UserInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.UpdateUser(id, input) is { } user)
    {
        Audit(context, repository, "UpdateUser", "User", id.ToString(), $"Actualizacion de usuario {user.Username}.");
        return Results.Ok(user);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageUsers");

protectedApi.MapDelete("/users/{id:int}", (int id, HttpContext context, IAdminRepository repository) =>
{
    if (repository.DeleteUser(id))
    {
        Audit(context, repository, "DeleteUser", "User", id.ToString(), $"Eliminacion de usuario {id}.");
        return Results.NoContent();
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageUsers");
protectedApi.MapPost("/users/{id:int}/password", (int id, PasswordResetInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.ResetUserPassword(id, input) is { } result)
    {
        Audit(context, repository, "ResetPassword", "User", id.ToString(), $"Restablecimiento de clave para {result.Username} con metodo {result.HashMethod}.");
        return Results.Ok(result);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageUsers");

protectedApi.MapPost("/usage", (UsageRecordInput input, HttpContext context, IAdminRepository repository) =>
{
    var record = repository.CreateUsageRecord(input);
    Audit(context, repository, "CreateUsageRecord", "UsageRecord", record.Id.ToString(), $"Registro manual de uso para usuario {record.UserId} en equipo {record.ComputerId}.");
    return Results.Ok(record);
}).RequireAuthorization("CanManageUsage");
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
