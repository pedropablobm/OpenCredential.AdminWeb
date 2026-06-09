using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Net;
using System.IO;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
if (!adminAuthOptions.Enabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("AdminAuth:Enabled cannot be false outside the Development environment.");
}
var trustedProxyAddresses = (builder.Configuration.GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? [])
    .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
    .Where(address => address is not null)
    .Cast<IPAddress>()
    .ToList();
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
builder.Services.AddSingleton<IPortalAuthService, PortalAuthService>();
builder.Services.AddSingleton<IDatabaseConfigurationService, DatabaseConfigurationService>();
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("OpenCredential.AdminWeb");
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Lax;
    options.OnAppendCookie = context => ApplyCookieSecurity(context.Context, context.CookieOptions);
    options.OnDeleteCookie = context => ApplyCookieSecurity(context.Context, context.CookieOptions);
});
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
    })
    .AddCookie(PortalAuthService.AuthenticationScheme, options =>
    {
        options.Cookie.Name = adminAuthOptions.PortalCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, adminAuthOptions.PortalSessionHours));
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
    options.AddPolicy("CanViewReports", policy => policy.RequireRole(AdminRoles.SuperAdmin, AdminRoles.Coordinator, AdminRoles.Operator));
    options.AddPolicy("CanManageSettings", policy => policy.RequireRole(AdminRoles.SuperAdmin));
    options.AddPolicy("PortalAuthenticated", policy =>
    {
        policy.AuthenticationSchemes.Add(PortalAuthService.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});
if (trustedProxyAddresses.Count > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        foreach (var address in trustedProxyAddresses)
        {
            options.KnownProxies.Add(address);
        }
    });
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Demasiados intentos. Espera un momento antes de volver a intentar." },
            cancellationToken);
    };
    options.AddPolicy("admin-login", context => CreateFixedWindowLimiter(context, Math.Max(3, adminAuthOptions.AdminLoginRateLimitPerMinute), TimeSpan.FromMinutes(1)));
    options.AddPolicy("portal-login", context => CreateFixedWindowLimiter(context, Math.Max(3, adminAuthOptions.PortalLoginRateLimitPerMinute), TimeSpan.FromMinutes(1)));
    options.AddPolicy("portal-recover", context => CreateFixedWindowLimiter(context, Math.Max(2, adminAuthOptions.PortalRecoveryRateLimitPerHour), TimeSpan.FromHours(1)));
    options.AddPolicy("portal-reset", context => CreateFixedWindowLimiter(context, Math.Max(3, adminAuthOptions.PortalResetRateLimitPerHour), TimeSpan.FromHours(1)));
});

var app = builder.Build();

if (trustedProxyAddresses.Count > 0)
{
    app.UseForwardedHeaders();
}
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps && !IsLocalRequest(context.Request))
    {
        var secureUrl = UriHelper.BuildAbsolute("https", context.Request.Host, context.Request.PathBase, context.Request.Path, context.Request.QueryString);
        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers.Location = secureUrl;
        return;
    }

    await next();
});
app.UseRateLimiter();
app.UseCookiePolicy();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

static string GetActor(HttpContext context)
{
    return string.IsNullOrWhiteSpace(context.User.Identity?.Name) ? "anon" : context.User.Identity!.Name!;
}

static bool IsLoopbackHost(string? host)
{
    if (string.IsNullOrWhiteSpace(host))
    {
        return false;
    }

    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

static bool IsLocalRequest(HttpRequest request)
{
    return IsLoopbackHost(request.Host.Host);
}

static void ApplyCookieSecurity(HttpContext context, CookieOptions cookieOptions)
{
    if (!IsLocalRequest(context.Request))
    {
        cookieOptions.Secure = true;
    }
}

static string GetClientRateLimitKey(HttpContext context)
{
    var address = context.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrWhiteSpace(address))
    {
        return address;
    }

    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwardedFor))
    {
        return forwardedFor.Split(',')[0].Trim();
    }

    return "unknown";
}

static RateLimitPartition<string> CreateFixedWindowLimiter(HttpContext context, int permitLimit, TimeSpan window)
{
    return RateLimitPartition.GetFixedWindowLimiter(
        GetClientRateLimitKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
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

IResult DataLoadProblem(Exception exception)
{
    app.Logger.LogError(exception, "No fue posible cargar datos desde la base configurada.");
    return Results.Problem(
        title: "No fue posible cargar datos desde la base configurada.",
        detail: builder.Environment.IsDevelopment()
            ? exception.Message
            : "Revisa la configuracion de la base de datos o consulta los logs del servidor para mas detalle.",
        statusCode: StatusCodes.Status500InternalServerError);
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
    claims.AddRange(adminIdentity.Groups.Select(group => new Claim(ClaimTypes.GroupSid, group)));

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
        Groups = adminIdentity.Groups.ToList(),
        AuthenticationEnabled = authService.IsEnabled
    });
}).RequireRateLimiting("admin-login");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    var actor = GetActor(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    TryAudit(context, actor, "Logout", "Security", "admin-console", "Cierre de sesion de la consola administrativa");
    return Results.NoContent();
});

app.MapGet("/api/auth/me", (HttpContext context, IAdminAuthService authService) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var defaultIdentity = authService.GetDefaultIdentity();
        return Results.Ok(new AdminSessionInfo
        {
            Authenticated = true,
            Username = context.User.Identity?.Name ?? defaultIdentity.Username,
            Role = context.User.FindFirstValue(ClaimTypes.Role) ?? defaultIdentity.Role,
            Groups = context.User.FindAll(ClaimTypes.GroupSid).Select(claim => claim.Value).ToList(),
            AuthenticationEnabled = authService.IsEnabled
        });
    }

    if (!authService.IsEnabled && app.Environment.IsDevelopment())
    {
        var defaultIdentity = authService.GetDefaultIdentity();
        return Results.Ok(new AdminSessionInfo
        {
            Authenticated = true,
            Username = defaultIdentity.Username,
            Role = defaultIdentity.Role,
            Groups = new List<string>(),
            AuthenticationEnabled = false
        });
    }

    return Results.Unauthorized();
});

app.MapPost("/api/portal/auth/login", async (PortalLoginInput input, HttpContext context, IPortalAuthService portalAuthService, IOptions<AdminAuthOptions> authOptionsAccessor) =>
{
    var portalIdentity = portalAuthService.ValidateCredentials(input.Username, input.Password);
    if (portalIdentity is null)
    {
        TryAudit(context, string.IsNullOrWhiteSpace(input.Username) ? "anon" : input.Username.Trim(), "PortalLoginFailed", "Security", "portal", "Intento fallido de acceso al portal de usuario");
        return Results.Unauthorized();
    }

    var authOptions = authOptionsAccessor.Value;
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, portalIdentity.Username)
    };
    claims.AddRange(portalIdentity.Groups.Select(group => new Claim(ClaimTypes.GroupSid, group)));

    var principal = new ClaimsPrincipal(
        new ClaimsIdentity(claims, PortalAuthService.AuthenticationScheme));

    await context.SignInAsync(
        PortalAuthService.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Max(1, authOptions.PortalSessionHours))
        });

    TryAudit(context, portalIdentity.Username, "PortalLogin", "Security", "portal", "Inicio de sesion correcto en el portal de usuario");

    return Results.Ok(new PortalSessionInfo
    {
        Authenticated = true,
        Username = portalIdentity.Username,
        FullName = portalIdentity.FullName,
        Groups = portalIdentity.Groups.ToList()
    });
}).RequireRateLimiting("portal-login");

app.MapPost("/api/portal/auth/logout", async (HttpContext context) =>
{
    var authResult = await context.AuthenticateAsync(PortalAuthService.AuthenticationScheme);
    var actor = authResult.Principal?.Identity?.Name ?? "anon";
    await context.SignOutAsync(PortalAuthService.AuthenticationScheme);
    TryAudit(context, actor, "PortalLogout", "Security", "portal", "Cierre de sesion del portal de usuario");
    return Results.NoContent();
});

app.MapGet("/api/portal/auth/me", async (HttpContext context, IAdminRepository repository) =>
{
    var authResult = await context.AuthenticateAsync(PortalAuthService.AuthenticationScheme);
    if (!authResult.Succeeded || authResult.Principal?.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var username = authResult.Principal.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    var profile = repository.GetPortalProfile(username);
    if (profile is null || !profile.Active)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new PortalSessionInfo
    {
        Authenticated = true,
        Username = profile.Username,
        FullName = profile.FullName,
        Groups = profile.Groups.Select(group => group.Name).ToList()
    });
});

app.MapPost("/api/portal/auth/recover", (PortalPasswordRecoveryInput input, HttpContext context, IAdminRepository repository, IOptions<AdminAuthOptions> authOptionsAccessor) =>
{
    var authOptions = authOptionsAccessor.Value;
    var result = repository.RecoverPortalPassword(input, authOptions.PortalRecoveryTokenLifetimeMinutes);
    var response = builder.Environment.IsDevelopment() && authOptions.PortalRevealRecoveryTokenInResponse
        ? result
        : new PortalPasswordRecoveryResult
        {
            Success = result.Success,
            Message = result.Success
                ? "Se genero un token temporal de recuperacion. Entregalo por el canal institucional definido para completar el cambio de clave."
                : result.Message,
            ExpiresAtUtc = result.ExpiresAtUtc,
            DeliveryHint = result.Success
                ? "En produccion el token no se expone en pantalla. Debe entregarse por un canal seguro o integrarse con correo institucional."
                : result.DeliveryHint
        };
    TryAudit(
        context,
        string.IsNullOrWhiteSpace(input.Username) ? "anon" : input.Username.Trim(),
        result.Success ? "PortalPasswordRecovered" : "PortalPasswordRecoveryFailed",
        "Security",
        "portal",
        result.Success
            ? $"Recuperacion asistida de clave para {input.Username.Trim()}."
            : $"Intento fallido de recuperacion para {input.Username.Trim()}.");
    return Results.Ok(response);
}).RequireRateLimiting("portal-recover");

app.MapPost("/api/portal/auth/reset", (PortalPasswordResetWithTokenInput input, HttpContext context, IAdminRepository repository) =>
{
    var success = repository.ResetPortalPasswordWithToken(input, out var message);
    TryAudit(
        context,
        "anon",
        success ? "PortalPasswordResetByToken" : "PortalPasswordResetByTokenFailed",
        "Security",
        "portal",
        success
            ? "Restablecimiento de clave mediante token temporal."
            : $"Intento fallido de restablecimiento por token: {message}");
    return success
        ? Results.Ok(new { success = true, message })
        : Results.BadRequest(new { success = false, message });
}).RequireRateLimiting("portal-reset");

var protectedApi = app.MapGroup("/api").RequireAuthorization();
var portalApi = app.MapGroup("/api/portal").RequireAuthorization("PortalAuthenticated");

protectedApi.MapGet("/summary", (IAdminRepository repository) =>
{
    try
    {
        return Results.Ok(repository.GetSnapshot());
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
});
protectedApi.MapGet("/dashboard", (IAdminRepository repository, int? rangeDays, int? careerId, int? semesterId, string? status) =>
{
    try
    {
        return Results.Ok(repository.GetDashboard(rangeDays ?? 30, careerId, semesterId, status));
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
});
protectedApi.MapGet("/reports", (IAdminRepository repository, DateTime? fromUtc, DateTime? toUtc, int? careerId, int? semesterId, int? groupId, string? username, string? sessionOrigin, string? sessionState, string? operationalStatus) =>
{
    try
    {
        var to = toUtc ?? DateTime.UtcNow;
        var from = fromUtc ?? to.AddDays(-30);
        if (from > to)
        {
            (from, to) = (to, from);
        }

        return Results.Ok(repository.GetReports(from, to, careerId, semesterId, groupId, username, sessionOrigin, sessionState, operationalStatus));
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
}).RequireAuthorization("CanViewReports");
protectedApi.MapGet("/reports/pdf", (IAdminRepository repository, DateTime? fromUtc, DateTime? toUtc, int? careerId, int? semesterId, int? groupId, string? username, string? sessionOrigin, string? sessionState, string? operationalStatus) =>
{
    try
    {
        var to = toUtc ?? DateTime.UtcNow;
        var from = fromUtc ?? to.AddDays(-30);
        if (from > to)
        {
            (from, to) = (to, from);
        }

        var reports = repository.GetReports(from, to, careerId, semesterId, groupId, username, sessionOrigin, sessionState, operationalStatus);
        var filters = new Dictionary<string, string>
        {
            ["Desde"] = from.ToLocalTime().ToString("dd/MM/yyyy"),
            ["Hasta"] = to.ToLocalTime().ToString("dd/MM/yyyy"),
            ["Carrera"] = careerId?.ToString() ?? "Todas",
            ["Semestre"] = semesterId?.ToString() ?? "Todos",
            ["Grupo"] = groupId?.ToString() ?? "Todos",
            ["Usuario"] = string.IsNullOrWhiteSpace(username) ? "Todos" : username.Trim(),
            ["Origen"] = string.IsNullOrWhiteSpace(sessionOrigin) ? "Todos" : RepositorySupport.TranslateSessionOrigin(sessionOrigin),
            ["Sesion"] = string.IsNullOrWhiteSpace(sessionState) ? "Todas" : sessionState.Trim(),
            ["Estado operativo"] = string.IsNullOrWhiteSpace(operationalStatus) ? "Todos" : operationalStatus.Trim()
        };

        var pdf = ReportPdfService.BuildReportPdf(reports, new ReportPdfContext
        {
            GeneratedAtUtc = DateTime.UtcNow,
            FromUtc = from,
            ToUtc = to,
            Filters = filters
        });

        var fileName = $"reporte_opencredential_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf";
        return Results.File(pdf, "application/pdf", fileName);
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
}).RequireAuthorization("CanViewReports");
protectedApi.MapGet("/audit", (IAdminRepository repository, int? take) =>
{
    try
    {
        return Results.Ok(repository.GetAuditEntries(take ?? 50));
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
}).RequireAuthorization("CanViewAudit");

protectedApi.MapGet("/groups", (IAdminRepository repository) =>
{
    try
    {
        return Results.Ok(repository.GetGroups());
    }
    catch (Exception exception)
    {
        return DataLoadProblem(exception);
    }
}).RequireAuthorization("CanManageUsers");

protectedApi.MapGet("/configuration/database", (IDatabaseConfigurationService configurationService) =>
{
    return Results.Ok(configurationService.GetConfiguration());
}).RequireAuthorization("CanManageSettings");

protectedApi.MapPost("/configuration/database/test", async (DatabaseConfigurationInput input, IDatabaseConfigurationService configurationService) =>
{
    return Results.Ok(await configurationService.TestConnectionAsync(input));
}).RequireAuthorization("CanManageSettings");

protectedApi.MapPost("/configuration/database/schema", async (DatabaseConfigurationInput input, HttpContext context, IDatabaseConfigurationService configurationService) =>
{
    var result = await configurationService.ApplySchemaAsync(input);
    if (result.Success)
    {
        TryAudit(context, GetActor(context), "ApplyDatabaseSchema", "Configuration", input.Provider, $"Ajuste manual de tablas auxiliares para {input.Provider} en {input.Host}:{input.Port}.");
    }

    return Results.Ok(result);
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

protectedApi.MapPost("/rooms", (RoomInput input, HttpContext context, IAdminRepository repository) =>
{
    var room = repository.CreateRoom(input);
    Audit(context, repository, "CreateRoom", "Room", room.Id.ToString(), $"Creacion de sala {room.Name}.");
    return Results.Ok(room);
}).RequireAuthorization("CanManageComputers");

protectedApi.MapPut("/rooms/{id:int}", (int id, RoomInput input, HttpContext context, IAdminRepository repository) =>
{
    if (repository.UpdateRoom(id, input) is { } room)
    {
        Audit(context, repository, "UpdateRoom", "Room", id.ToString(), $"Actualizacion de sala {room.Name}.");
        return Results.Ok(room);
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageComputers");

protectedApi.MapDelete("/rooms/{id:int}", (int id, HttpContext context, IAdminRepository repository) =>
{
    if (repository.DeleteRoom(id))
    {
        Audit(context, repository, "DeleteRoom", "Room", id.ToString(), $"Eliminacion de sala {id}.");
        return Results.NoContent();
    }

    return Results.NotFound();
}).RequireAuthorization("CanManageComputers");

protectedApi.MapPut("/rooms/{id:int}/layout", (int id, RoomLayoutInput input, HttpContext context, IAdminRepository repository) =>
{
    try
    {
        var positions = repository.SaveRoomLayout(id, input);
        Audit(context, repository, "UpdateRoomLayout", "Room", id.ToString(), $"Actualizacion del mapa visual de la sala {id} con {positions.Count} puestos.");
        return Results.Ok(positions);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
}).RequireAuthorization("CanManageComputers");

protectedApi.MapPost("/users", (UserInput input, HttpContext context, IAdminRepository repository) =>
{
    try
    {
        var user = repository.CreateUser(input);
        Audit(context, repository, "CreateUser", "User", user.Id.ToString(), $"Creacion de usuario {user.Username}.");
        return Results.Ok(user);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
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

portalApi.MapGet("/me", (HttpContext context, IAdminRepository repository) =>
{
    var username = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    var profile = repository.GetPortalProfile(username);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

portalApi.MapPut("/me", (PortalProfileUpdateInput input, HttpContext context, IAdminRepository repository) =>
{
    var username = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    var profile = repository.UpdatePortalProfile(username, input);
    if (profile is null)
    {
        return Results.NotFound();
    }

    Audit(context, repository, "PortalUpdateProfile", "User", profile.UserId.ToString(), $"Actualizacion de perfil propia para {profile.Username}.");
    return Results.Ok(profile);
});

portalApi.MapPost("/me/password", (PortalPasswordChangeInput input, HttpContext context, IAdminRepository repository) =>
{
    var username = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(input.CurrentPassword) || string.IsNullOrWhiteSpace(input.NewPassword) || string.IsNullOrWhiteSpace(input.ConfirmPassword))
    {
        return Results.BadRequest(new { message = "Completa la clave actual, la nueva clave y la confirmacion." });
    }

    if (!string.Equals(input.NewPassword, input.ConfirmPassword, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { message = "La confirmacion no coincide con la nueva clave." });
    }

    var user = repository.FindUserByUsername(username);
    if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash) || !PasswordHashService.VerifyPassword(input.CurrentPassword, user.PasswordHash, user.HashMethod))
    {
        return Results.BadRequest(new { message = "La clave actual no es valida." });
    }

    var result = repository.UpdatePasswordByUsername(username, input.NewPassword, input.HashMethod);
    if (result is null)
    {
        return Results.NotFound();
    }

    Audit(context, repository, "PortalChangePassword", "User", result.UserId.ToString(), $"Cambio de clave realizado por {result.Username} desde el portal.");
    return Results.Ok(new { message = "Clave actualizada correctamente." });
});

portalApi.MapGet("/me/sessions", (HttpContext context, IAdminRepository repository, int? take) =>
{
    var username = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(repository.GetPortalSessions(username, take ?? 25));
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
