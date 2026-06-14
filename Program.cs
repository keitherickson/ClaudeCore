using ClaudeCore.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// UI branding (display name) from the "Branding" config section.
builder.Services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.SectionName));

// Default selections for the Generate Video form ("VideoDefaults" section).
builder.Services.Configure<VideoDefaultsOptions>(builder.Configuration.GetSection(VideoDefaultsOptions.SectionName));

// LTX-2 local video generation: bind options, register the typed HttpClient
// (with a generous timeout since /api/generate blocks until the video is done),
// and the orchestration service.
builder.Services.Configure<LtxVideoOptions>(builder.Configuration.GetSection(LtxVideoOptions.SectionName));
builder.Services.AddHttpClient<LtxVideoClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<LtxVideoOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
});
builder.Services.AddScoped<LtxVideoService>();
// Remembers the last-used starting image across requests/restarts.
builder.Services.AddSingleton<LastImageStore>();

// NVIDIA Maxine upscaling: shells out to the SDK's VideoEffectsApp.exe per job.
builder.Services.Configure<MaxineUpscaleOptions>(builder.Configuration.GetSection(MaxineUpscaleOptions.SectionName));
builder.Services.AddScoped<MaxineUpscaleService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Video}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
