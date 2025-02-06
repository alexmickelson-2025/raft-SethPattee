using Microsoft.AspNetCore.DataProtection;
using RaftDockerClient.Components;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAntiforgery(options => 
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.SuppressXFrameOptionsHeader = true;
    options.Cookie.HttpOnly = false;
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "CSRF-TOKEN";
});

builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();  

var app = builder.Build();

app.UseAntiforgery();

app.MapGet("/health", () => "healthy");
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery(); 

app.Run();