var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// KeithUI is a thin node-graph UI over the EXISTING service layer — reuse it wholesale.
builder.Services.AddKeithVisionServices(builder.Configuration);

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
