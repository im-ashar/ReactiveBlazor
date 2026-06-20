using ReactiveBlazor;
using ReactiveBlazor.Demo.Components;
using ReactiveBlazor.Demo.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReactiveBlazorDemo(builder.Environment)
    .AddDemoAuthentication();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();
app.MapReactiveComponents();
app.MapDemoAuthEndpoints();

app.Run();
