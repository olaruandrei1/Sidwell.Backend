using Prometheus;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.API.Common;
using Sidwell.Backend.Application;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.BackgroundServices;
using Sidwell.Backend.Infrastructure;
using Sidwell.Backend.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Sidwell")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Sidwell in configuration.");

builder.Services
    .AddBackendApplication()
    .AddBackendPersistence(connectionString)
    .AddBackendInfrastructure(builder.Configuration)
    .AddBackendBackgroundServices();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new TolerantStringConverter());
        options.JsonSerializerOptions.NumberHandling =
            System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString |
            System.Text.Json.Serialization.JsonNumberHandling.WriteAsString;
    });

builder.Services
    .AddAuthentication(SessionTokenDefaults.AuthenticationScheme)
    .AddScheme<SessionTokenAuthenticationOptions, SessionTokenAuthenticationHandler>(
        SessionTokenDefaults.AuthenticationScheme, _ => { }
    );

builder.Services.AddAuthorization();

string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials())
);

WebApplication app = builder.Build();

app.UsePathBase("/api");
app.UseHttpMetrics();
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
    {
        var start = DateTimeOffset.UtcNow;
        await next();
        app.Logger.LogInformation("{Method} {Path} {Status} {Ms}ms",
            ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode,
            (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds);
        return;
    }
    await next();
});
app.UseCors();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapMetrics("/metrics");

app.Run();
