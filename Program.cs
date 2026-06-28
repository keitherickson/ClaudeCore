var builder = WebApplication.CreateBuilder(args);

// GPU profiles: when KEITHVISION_PROFILE=<name>, layer appsettings.<name>.json on top.
//   4090     -> LTX BF16 on the 4090, prompt LLM on the 5090 (game on the 5090).
//   game4090 -> game on the 4090; LTX on the 5090, prompt LLM on CPU.
// Off by default (no env var), so the normal 5090 setup is unchanged. See tools/run-*.ps1.
var kvProfile = Environment.GetEnvironmentVariable("KEITHVISION_PROFILE");
if (!string.IsNullOrWhiteSpace(kvProfile))
    builder.Configuration.AddJsonFile($"appsettings.{kvProfile}.json", optional: true, reloadOnChange: true);

// UI host.
builder.Services.AddControllersWithViews();

// The entire service layer lives in KeithVision.Core; one call wires it all up.
builder.Services.AddKeithVisionServices(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline. This app is localhost-only and binds HTTP
// endpoints only (see Kestrel config), so there's no HSTS/HTTPS-redirect to do.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Video}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
