using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace ome.Infrastructure.Persistence.Context;

public class ApplicationDbContextFactory: IDesignTimeDbContextFactory<ApplicationDbContext> {
    public ApplicationDbContext CreateDbContext(string[] args) {
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Configure DbContext options
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'DefaultConnection' is missing or empty.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();

        // Verwende MariaDB anstelle von PostgreSQL
        var serverVersion = new MariaDbServerVersion(new Version(10, 5, 0));
        
        optionsBuilder.UseMySql(connectionString, serverVersion,
            options => options.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));

        // Create a minimal implementation of required services
        return new ApplicationDbContext(
            optionsBuilder.Options,
            null!, // ITenantService
            null!, // ICurrentUserService
            new LoggerFactory().CreateLogger<ApplicationDbContext>(),
            null!, // AuditSaveChangesInterceptor
            null! // TenantSaveChangesInterceptor
        );
    }
}