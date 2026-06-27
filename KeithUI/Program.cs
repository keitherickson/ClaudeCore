var builder = WebApplication.CreateBuilder(args);

// "run on 4090" profile: when KEITHVISION_PROFILE=4090, layer appsettings.4090.json on
// top (moves LTX BF16 onto the 4090 + triggers the prompt-LLM VRAM yield). Off by default,
// so the normal 5090 setup is unchanged. Launched via tools/run-on-4090.ps1.
if (string.Equals(Environment.GetEnvironmentVariable("KEITHVISION_PROFILE"), "4090", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.4090.json", optional: true, reloadOnChange: true);

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
