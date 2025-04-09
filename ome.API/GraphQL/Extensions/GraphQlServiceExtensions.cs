using ome.API.GraphQL.Middlewares;
using ome.API.GraphQL.Mutations;
using ome.API.GraphQL.Queries;
using ome.API.GraphQL.Subscriptions;
using ome.API.GraphQL.Types;
using ome.Infrastructure.Logging.Filters;

namespace ome.API.GraphQL.Extensions;

/// <summary>
/// Erweiterungsmethoden für die GraphQL-Konfiguration
/// </summary>
public static class GraphQlServiceExtensions {
    /// <summary>
    /// Fügt die GraphQL-Dienste zum DI-Container hinzu
    /// </summary>
    public static IServiceCollection
        AddGraphQlServices(this IServiceCollection services, IConfiguration configuration) {
        // Füge HotChocolate-Services hinzu
        services
            .AddGraphQLServer()
            // Middleware
            .AddHttpRequestInterceptor<TenantHttpRequestInterceptor>()
            // Schema-Konfiguration
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddTypeExtension<RefreshTokenMutation>()
            .AddTypeExtension<LogoutMutation>()
            .AddTypeExtension<RefreshTokenMutation>()
            .AddTypeExtension<UserMutations>()
            .AddSubscriptionType<Subscription>()
            // Typen-Registrierung
            .AddType<UserType>()
            .AddType<TenantType>()
            .AddType<UserRoleType>()
            .AddType<RequirePermissionDirectiveType>()
            // Features
            .AddAuthorization()
            .AddFiltering()
            .AddSorting()
            .AddProjections()
            // Subscription-Transport
            .AddInMemorySubscriptions()
            // Custom-Direktiven für Autorisierung
            .AddDirectiveType<RequirePermissionDirectiveType>()
            // Error-Handling
            .AddErrorFilter<GraphQlErrorFilter>();

        return services;
    }
}