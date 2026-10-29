using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Restaurant.Api.Auth;
using Restaurant.Api.Live;
using Restaurant.Api.Middleware;
using Restaurant.Application;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Models;
using Restaurant.Contracts.Live;
using Restaurant.Domain.Entities;
using Restaurant.Infrastructure;
using Restaurant.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();

builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddSingleton<Restaurant.Api.Services.AppReleases>();

// --- Identity: users, password hashing, lockout, reset tokens (PLAN.md D2) ---
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.AllowedForNewUsers = true;
        // Uniqueness of email and mobile number is enforced by unique indexes (staff may have no email).
        options.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<RestaurantDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddDataProtection();

builder.Services.AddOptions<AuthSettings>().BindConfiguration("Auth");

// --- JWT: bound lazily from options so a test host's configuration overrides apply ---
var fallbackSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
builder.Services.AddOptions<JwtSettings>()
    .BindConfiguration("Jwt")
    .PostConfigure<IHostEnvironment>((jwt, environment) =>
    {
        if (string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException("Jwt:SigningKey must be set in production.");
            }
            // Development and tests: a throwaway key, so tokens just don't survive a restart.
            jwt.SigningKey = fallbackSigningKey;
        }
        else if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }
    })
    .ValidateOnStart();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaims.UserId
        };
        options.Events = new JwtBearerEvents
        {
            // SignalR's WebSocket transport can't send headers, so the kitchen hub gets the token in the query.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// --- Rate limiting: sign-in endpoints are brute-force targets. Per client IP, so one restaurant's
// attempts (or one attacker) can't lock everyone else out. ---
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimiting:AuthPermitLimit", 10),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Everything else: per signed-in user (a restaurant's counters share one IP behind its router), or per IP
    // when anonymous. Generous enough for a busy counter; it only stops runaway clients and scraping.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        if (http.Request.Path.StartsWithSegments("/health") || http.Request.Path.StartsWithSegments("/" + LiveEvents.HubPath))
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        var user = http.User.Identity?.Name;
        return user is null
            ? RateLimitPartition.GetFixedWindowLimiter("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("RateLimiting:AnonymousPermitLimit", 120),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            })
            : RateLimitPartition.GetFixedWindowLimiter("user:" + user, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("RateLimiting:UserPermitLimit", 600),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

// Logos are the largest uploads (6 MB, BrandingController); Nginx Proxy Manager allows 8 MB.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

// --- CORS: only the web app's origin in production; any origin in development. The desktop app
// doesn't send an Origin header, so CORS never applies to it. ---
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?
        .Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.TrimEnd('/')).ToArray() ?? [];
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins);
    }
    else
    {
        policy.AllowAnyOrigin();
    }
    policy.AllowAnyHeader().AllowAnyMethod();
}));

// Behind Nginx Proxy Manager (and rp-web): trust X-Forwarded-* only when the deployment says the API
// is unreachable except through the proxy (per-IP rate limits depend on it).
var trustForwardedHeaders = builder.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false);
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 2; // NPM + rp-web
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddHealthChecks().AddDbContextCheck<RestaurantDbContext>("database");

var app = builder.Build();

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
// Logs the path only, never the query string (the hub's access_token travels there).
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // Sent only over HTTPS (X-Forwarded-Proto from the proxy).
    app.UseHsts();
}

// Uploaded logos and menu images, at FileStorage:PublicBaseUrl ("/uploads").
var uploadsRoot = Path.GetFullPath(app.Configuration["FileStorage:RootPath"] ?? "uploads");
Directory.CreateDirectory(uploadsRoot);
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uploadsRoot), RequestPath = "/uploads" });

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication, so the global limit can be per user
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<LiveHub>("/" + LiveEvents.HubPath);

// API + database. The deploy smoke tests look for "healthy".
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) =>
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status == HealthStatus.Healthy ? "healthy" : "unhealthy",
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString().ToLowerInvariant())
        }));
    }
});

// Production runs migrations as a release step (scripts/deploy-production.sh); development and the
// tests opt in. Seeding is idempotent and touches the database only when a platform admin is configured.
using (var scope = app.Services.CreateScope())
{
    if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        await scope.ServiceProvider.GetRequiredService<RestaurantDbContext>().Database.MigrateAsync();
    }
    await DbInitializer.SeedAsync(scope.ServiceProvider);
}

app.Run();

// For WebApplicationFactory<Program> in the tests.
public partial class Program;
