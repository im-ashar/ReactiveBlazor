using ReactiveBlazor.Demo.Components;
using Microsoft.AspNetCore.DataProtection;
using ReactiveBlazor;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.AddReactiveComponents(assemblies: typeof(Program).Assembly);
builder.Services.AddSingleton<ReactiveBlazor.Demo.Services.ProductService>();
builder.Services.AddSingleton<ReactiveBlazor.Demo.Services.CartService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();
app.MapReactiveComponents();
app.Run();
