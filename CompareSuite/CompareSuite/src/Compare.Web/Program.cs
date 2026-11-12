using Compare.Contracts;
using Compare.Web.Components;
using Compare.Web.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

builder.Services.AddHttpClient<GatewayClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Gateway:Url"]
        ?? throw new InvalidOperationException("Gateway:Url is not configured."));
    client.DefaultRequestHeaders.Add(ApiKeyAuth.Header, builder.Configuration["Gateway:ApiKey"] ?? "");
});

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
