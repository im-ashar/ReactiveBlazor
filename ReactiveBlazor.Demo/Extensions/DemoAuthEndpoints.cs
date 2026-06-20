using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ReactiveBlazor.Demo.Extensions;

/// <summary>
/// Minimal sign-in / sign-out endpoints for the demo. There is no real user store — the
/// <c>/login</c> page (Login.razor) shows persona buttons that link to <c>/auth/signin</c>, which
/// issues a cookie carrying that persona's roles/claims. The cookie middleware redirects
/// unauthenticated users to <c>/login?ReturnUrl=...</c>, so <c>/login</c> itself stays anonymous.
/// </summary>
public static class DemoAuthEndpoints
{
    private const string DefaultReturnUrl = "/authorization";

    /// <summary>A selectable demo identity.</summary>
    private sealed record Persona(string Name, string[] Roles, string? Department);

    private static readonly IReadOnlyDictionary<string, Persona> Personas = new Dictionary<string, Persona>
    {
        ["admin"]   = new("Alice Admin",   ["Admin"], "finance"),
        ["finance"] = new("Fiona Finance", ["User"],  "finance"),
        ["user"]    = new("Uri User",      ["User"],  "sales"),
        ["guest"]   = new("Guest",         [],        null),
    };

    /// <summary>Maps the demo's <c>/auth/signin</c> and <c>/auth/signout</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapDemoAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/signin", async (HttpContext ctx, string persona, string? returnUrl) =>
        {
            var principal = CreatePrincipal(persona);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.LocalRedirect(Safe(returnUrl));
        });

        endpoints.MapGet("/auth/signout", async (HttpContext ctx, string? returnUrl) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(Safe(returnUrl));
        });

        return endpoints;
    }

    private static ClaimsPrincipal CreatePrincipal(string personaKey)
    {
        var persona = Personas.GetValueOrDefault(personaKey) ?? Personas["guest"];

        var claims = new List<Claim> { new(ClaimTypes.Name, persona.Name) };
        claims.AddRange(persona.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (!string.IsNullOrEmpty(persona.Department))
            claims.Add(new Claim("department", persona.Department));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static string Safe(string? returnUrl) =>
        string.IsNullOrEmpty(returnUrl) ? DefaultReturnUrl : returnUrl;
}
