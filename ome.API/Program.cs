using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ome.API.Extensions;
using ome.API.GraphQL.Extensions;
using ome.API.GraphQL.Middlewares;
using ome.Core.Features.Auth;
using ome.Core.Features.Notifications;
using ome.Core.Features.Users;
using ome.Core.Interfaces.Messaging;
using ome.Core.Interfaces.Services;
using ome.Infrastructure.Identity.Extensions;
using ome.Infrastructure.Identity.Keycloak;
using ome.Infrastructure.Identity.Services;
using ome.Infrastructure.Logging;
using ome.Infrastructure.Modules;
using ome.Infrastructure.Persistence.Context;
using ome.Infrastructure.Persistence.Interceptors;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace ome.API;

public class Program {
    private static void ConfigureLogging(WebApplicationBuilder builder) {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: AnsiConsoleTheme.Code
            )
            .WriteTo.File(
                path: "logs/application-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName()
            .CreateLogger();

        builder.Host.UseSerilog();
    }

  private static void ConfigureDatabaseOptions(IServiceProvider sp, DbContextOptionsBuilder options) {
    var configuration = sp.GetRequiredService<IConfiguration>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    try {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        connectionString = connectionString?
            .Replace("${DB_HOST}", Environment.GetEnvironmentVariable("DB_HOST"))
            .Replace("${DB_PORT}", Environment.GetEnvironmentVariable("DB_PORT"))
            .Replace("${DB_NAME}", Environment.GetEnvironmentVariable("DB_NAME"))
            .Replace("${DB_USER}", Environment.GetEnvironmentVariable("DB_USER"))
            .Replace("${DB_PASSWORD}", Environment.GetEnvironmentVariable("DB_PASSWORD"));

        // SSL-Konfiguration
        var sslEnabled = Environment.GetEnvironmentVariable("DB_SSL_ENABLED")?.ToLower() == "true";
        var rootCaCertPath = Environment.GetEnvironmentVariable("DB_SSL_CA_PATH");

        if (sslEnabled && !string.IsNullOrEmpty(rootCaCertPath)) {
            if (File.Exists(rootCaCertPath)) {
                connectionString += $";SslMode=VerifyCA;SslCa={rootCaCertPath}";
                logger.LogInformation("SSL-Konfiguration mit Root-CA: {CertPath}", rootCaCertPath);
            }
            else {
                logger.LogWarning("SSL-Zertifikat nicht gefunden: {CertPath}", rootCaCertPath);
            }
        }

        // Dynamische Serverversion basierend auf Umgebungsvariable
        var serverVersionStr = Environment.GetEnvironmentVariable("DB_SERVER_VERSION") ?? "10.5.0";
        var serverVersion = new MariaDbServerVersion(Version.Parse(serverVersionStr));

        logger.LogInformation("Verbinde zu MariaDB Version: {Version}", serverVersion);

        options.UseMySql(
            connectionString,
            serverVersion,
            mysqlOptions => {
                // Migrations-Assembly
                mysqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);

                // Robuster Retry-Mechanismus
                mysqlOptions.EnableRetryOnFailure(
                    maxRetryCount: environment.IsDevelopment() ? 3 : 10,
                    maxRetryDelay: TimeSpan.FromSeconds(environment.IsDevelopment() ? 15 : 60),
                    errorNumbersToAdd: [
                        1042,   // Connection timeout
                        1044,   // Access denied to database
                        1045,   // Access denied for user
                        1129,   // Host blocked 
                        -2      // Timeout errors
                    ]
                );

                // Performance & Stability Optimierungen
                mysqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                mysqlOptions.CommandTimeout(120);  // 2 Minuten Timeout
            }
        );

        // Entwicklungsumgebung spezifische Konfiguration
        if (environment.IsDevelopment()) {
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
            logger.LogInformation("Entwicklungs-Datenbankoptionen aktiviert");
        }
        else {
            // Produktions-Logging-Optimierungen
            options.EnableDetailedErrors(false);
        }

        logger.LogInformation("Datenbankverbindung erfolgreich konfiguriert");
    }
    catch (Exception ex) {
        logger.LogCritical(ex, "Kritischer Fehler bei Datenbankverbindungskonfiguration");
        throw;
    }
  }
  private static void AddCustomInterceptors(IServiceProvider sp, DbContextOptionsBuilder options) {
      try {
          var auditInterceptor = sp.GetRequiredService<AuditSaveChangesInterceptor>();
          var tenantInterceptor = sp.GetRequiredService<TenantSaveChangesInterceptor>();

          options.AddInterceptors(auditInterceptor, tenantInterceptor);

          Log.Information("Interceptors erfolgreich konfiguriert");
      }
      catch (Exception ex) {
          Log.Error(ex, "Fehler bei der Konfiguration der Interceptors");
          throw;
      }
  }

  public static async Task Main(string[] args) {
      var builder = WebApplication.CreateBuilder(args);
      Log.Information("Starte MultiTenant Backend");

      try {
          ConfigureLogging(builder);

          var moduleSettings = builder.Configuration.GetSection("Modules").Get<Dictionary<string, bool>>()
                               ?? new Dictionary<string, bool>();

          Log.Information("Starte MultiTenant Backend mit {ModuleCount} Modulen", moduleSettings.Count);

          // Services registrieren
          builder.Services.AddDistributedMemoryCache();
          builder.Services.AddControllers();
          builder.Services.AddHttpContextAccessor();
          builder.Services.AddMemoryCache();

          builder.Services.AddSession(options => {
              options.IdleTimeout = TimeSpan.FromMinutes(30);
              options.Cookie.HttpOnly = true;
              options.Cookie.IsEssential = true;
              options.Cookie.SameSite = SameSiteMode.Lax;
              options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
          });
          builder.Services.AddLoggingServices(builder.Configuration);

          // Scoped Services
          builder.Services.AddScoped<ITenantService, TenantService>();
          builder.Services.AddSingleton<TenantHttpRequestInterceptor>();
          builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
          builder.Services.AddScoped<IKeycloakService, KeycloakService>();

          // Interceptors als Scoped registrieren
          builder.Services.AddScoped<AuditSaveChangesInterceptor>();
          builder.Services.AddScoped<TenantSaveChangesInterceptor>();

          // DbContext Konfiguration
          builder.Services.AddDbContext<ApplicationDbContext>((sp, options) => {
              ConfigureDatabaseOptions(sp, options);
              AddCustomInterceptors(sp, options);
          });

          // Identität und Authentifizierung
          builder.Services.AddIdentityServices(builder.Configuration);

          // GraphQL
          builder.Services.AddGraphQlServices(builder.Configuration);

          // Swagger
          builder.Services.AddEndpointsApiExplorer();
          builder.Services.AddSwaggerWithAuth(builder.Configuration);

          // Messaging und Events
          builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();

          // Modulregistrierung
          var moduleManager = new ModuleManager();
          moduleManager.RegisterModule(new AuthModule(), moduleSettings.GetValueOrDefault("Auth", true));
          moduleManager.RegisterModule(new UsersModule(), moduleSettings.GetValueOrDefault("Users", true));

          moduleManager.RegisterModule(new NotificationsModule(),
              moduleSettings.GetValueOrDefault("Notifications", true));
          moduleManager.ConfigureServices(builder.Services, builder.Configuration);

          // CORS
          builder.Services.AddCors(options => {
              options.AddPolicy("AllowSpecificOrigins",
                  policy => policy
                      .WithOrigins(
                          "http://localhost:3000"
                      )
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials());
          });

          var app = builder.Build();
          Log.Information("Anwendung erfolgreich gebaut");

          // Datenbank-Initialisierung
          using (var scope = app.Services.CreateScope()) {
              var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
              var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

              try {
                  logger.LogInformation("Migriere Datenbank...");
                  await dbContext.Database.MigrateAsync();
                  logger.LogInformation("Datenbank erfolgreich migriert");
              }
              catch (Exception ex) {
                  logger.LogError(ex, "Ein Fehler ist bei der Datenbankinitialisierung aufgetreten");
                  throw;
              }
          }

          // HTTP-Pipeline-Konfiguration
          if (app.Environment.IsDevelopment()) {
              app.UseDeveloperExceptionPage();

              app.UseSwagger();

              app.UseSwaggerUI(c => {
                  c.SwaggerEndpoint("/swagger/v1/swagger.json", "MultiTenant API v1");
                  c.RoutePrefix = "swagger";
              });

              app.MapControllers(); // Optional hier, kann auch global danach kommen
          }
          else if (app.Environment.IsProduction()) {
              app.UseExceptionHandler("/Error");
              app.UseHsts();

              // HTTPS-Weiterleitung aktivieren
              app.UseHttpsRedirection();

              // CORS-Regel für das erlaubte Frontend
              app.UseCors("AllowDevelopment");

              // Weitergeleitete Header verarbeiten (z.B. hinter einem Reverse Proxy)
              app.UseForwardedHeaders(new ForwardedHeadersOptions {
                  ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
              });
          }

          app.UseSerilogRequestLogging(options => {
              options.MessageTemplate =
                  "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

              options.EnrichDiagnosticContext = (diagnosticContext, httpContext) => {
                  if (httpContext.Request.Host.Value != null) {
                      diagnosticContext.Set("Host", httpContext.Request.Host.Value);
                  }

                  diagnosticContext.Set("UserAgent",
                      httpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? string.Empty);
              };
          });

          app.UseHttpsRedirection();
          app.UseStaticFiles();

          app.UseCors("AllowSpecificOrigins");
          app.UseSession();
          app.UseAuthentication();
          app.UseAuthorization();

          app.MapGraphQL();

          app.UseWebSockets(new WebSocketOptions {
              KeepAliveInterval = TimeSpan.FromMinutes(2)
          });

          Log.Information("Starte Anwendung...");
          await app.RunAsync();
      }
      catch (Exception ex) {
          Log.Fatal(ex, "Host wurde unerwartet beendet");
          throw;
      }
      finally {
          await Log.CloseAndFlushAsync();
      }
  }
}