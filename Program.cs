var builder = WebApplication.CreateBuilder(args);

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
