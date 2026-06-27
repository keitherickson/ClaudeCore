var builder = WebApplication.CreateBuilder(args);

// "run on 4090" profile: when KEITHVISION_PROFILE=4090, layer appsettings.4090.json on
// top (moves LTX BF16 onto the 4090 + triggers the prompt-LLM VRAM yield). Off by default,
// so the normal 5090 setup is unchanged. Launched via tools/run-on-4090.ps1.
if (string.Equals(Environment.GetEnvironmentVariable("KEITHVISION_PROFILE"), "4090", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.4090.json", optional: true, reloadOnChange: true);

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
