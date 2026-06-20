using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using ReactiveBlazor.Demo.Services;

namespace ReactiveBlazor.Demo.Extensions;

/// <summary>
/// Service-registration extensions that keep <c>Program.cs</c> focused on composition.
/// </summary>
public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Registers Razor components, ReactiveBlazor, persistent Data Protection keys, and the demo's
    /// in-memory application services.
    /// </summary>
    public static IServiceCollection AddReactiveBlazorDemo(this IServiceCollection services, IWebHostEnvironment env)
    {
        services.AddRazorComponents();

        // Persist keys so encrypted state tokens survive app restarts during development.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(env.ContentRootPath, "keys")));

        // Bind each state token to the user it was issued to, so a token copied from another
        // session (shared/kiosk machine, screen share, support attachment) can't be replayed to
        // load that user's component state. Showcases ReactiveOptions.BindStateToUser.
        services.AddReactiveComponents(
            options => options.BindStateToUser = true,
            assemblies: typeof(Program).Assembly);

        services.AddSingleton<ProductService>();
        services.AddSingleton<CartService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<SystemMetricsService>();
        services.AddSingleton<AuditLogService>();

        return services;
    }

    /// <summary>
    /// Configures cookie authentication and authorization used by the declarative-authorization demo.
    /// A short, non-sliding cookie lifetime makes the "session expired → 401 → reload to login" flow
    /// easy to observe. Cascading authentication state lets <c>&lt;AuthorizeView&gt;</c> work in static
    /// SSR — ReactiveBlazor seeds the same state during reactive dispatches.
    /// </summary>
    public static IServiceCollection AddDemoAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/authorization";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(2);
                options.SlidingExpiration = false;
            });

        services.AddAuthorization(options =>
        {
            // Named policy used by the demo's billing component.
            options.AddPolicy("CanViewBilling", policy => policy.RequireClaim("department", "finance"));
        });

        services.AddCascadingAuthenticationState();

        return services;
    }
}
