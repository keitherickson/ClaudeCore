var builder = WebApplication.CreateBuilder(args);

// GPU profiles: when KEITHVISION_PROFILE=<name>, layer appsettings.<name>.json on top.
//   4090     -> LTX BF16 on the 4090, prompt LLM on the 5090 (game on the 5090).
//   game4090 -> game on the 4090; LTX on the 5090, prompt LLM on CPU.
// Off by default (no env var), so the normal 5090 setup is unchanged. See tools/run-*.ps1.
var kvProfile = Environment.GetEnvironmentVariable("KEITHVISION_PROFILE");
if (!string.IsNullOrWhiteSpace(kvProfile))
    builder.Configuration.AddJsonFile($"appsettings.{kvProfile}.json", optional: true, reloadOnChange: true);

builder.Services.AddControllersWithViews();

// KeithUI is a thin node-graph UI over the EXISTING service layer — reuse it wholesale.
builder.Services.AddKeithVisionServices(builder.Configuration);

// Walks a serialized node graph and runs it via the Core services.
builder.Services.AddScoped<KeithUI.Services.GraphExecutor>();

// Persists named studio layouts (saved graphs) for the toolbar dropdown.
builder.Services.AddSingleton<KeithUI.Services.LayoutStore>();

// Tracks in-flight graph runs so the admin page / Stop button can cancel them.
builder.Services.AddSingleton<KeithUI.Services.RunRegistry>();

var app = builder.Build();

// Localhost-only, HTTP only (same trust model as KeithVision) — no HSTS/HTTPS redirect.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Studio}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
