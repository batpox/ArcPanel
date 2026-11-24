using Arc.Components;
using Arc.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ArcAdminAccess>();
builder.Services.AddScoped<ArcSession>();
string uiUrls = builder.Configuration["Arc:UIUrls"] ?? "http://0.0.0.0:5080";
builder.WebHost.UseUrls(uiUrls);
builder.Services.AddHttpClient<ArcApiClient>(client =>
{
    string baseUrl = builder.Configuration["ArcServer:ApiBaseUrl"] ?? "http://127.0.0.1:5081/";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
});

var app = builder.Build();
_ = app.Services.GetRequiredService<ArcAdminAccess>();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
