using ClaudeCore.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// UI branding (display name) from the "Branding" config section.
builder.Services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.SectionName));

// Default selections for the Generate Video form ("VideoDefaults" section).
builder.Services.Configure<VideoDefaultsOptions>(builder.Configuration.GetSection(VideoDefaultsOptions.SectionName));

// Live GPU footer readout ("Gpu" section).
builder.Services.Configure<GpuOptions>(builder.Configuration.GetSection(GpuOptions.SectionName));
// Samples system CPU utilization for the footer readout.
builder.Services.AddSingleton<SystemStatsService>();

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

// ComfyUI NVFP4 backend (the "fast" model). Typed HttpClient with a generous
// timeout since /prompt is polled to completion.
builder.Services.Configure<ComfyUiOptions>(builder.Configuration.GetSection(ComfyUiOptions.SectionName));
builder.Services.AddHttpClient<ComfyUiVideoBackend>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<ComfyUiOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
});

// Wan 2.2 14B image-to-video backend (the "quality" model). Runs on the SAME ComfyUI
// server as the NVFP4 backend, so its typed HttpClient points at the shared ComfyUI URL.
builder.Services.Configure<WanOptions>(builder.Configuration.GetSection(WanOptions.SectionName));
builder.Services.AddHttpClient<WanVideoBackend>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<WanOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
});

// Model switch: the registry of selectable models + the persisted active selection,
// and all backends exposed as ILtxVideoBackend so LtxVideoService can route by key.
builder.Services.Configure<VideoModelsOptions>(builder.Configuration.GetSection(VideoModelsOptions.SectionName));
builder.Services.AddSingleton<ActiveModelStore>();
builder.Services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<LtxVideoClient>());
builder.Services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<ComfyUiVideoBackend>());
builder.Services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<WanVideoBackend>());

builder.Services.AddScoped<LtxVideoService>();
// Process control for the LTX server (admin page: status + restart + stop).
builder.Services.AddSingleton<LtxServerControl>();
// Process control for the ComfyUI NVFP4 server (started/stopped by the model switch).
builder.Services.AddSingleton<ComfyUiServerControl>();
// Topology-aware backend lifecycle: switching models stops co-resident (same-GPU)
// backends and starts the selected one. Reconciler re-applies it on startup.
builder.Services.AddSingleton<VideoBackendCoordinator>();
builder.Services.AddHostedService<VideoBackendReconciler>();
// Remembers the last-used starting image across requests/restarts.
builder.Services.AddSingleton<LastImageStore>();

// NVIDIA Maxine upscaling: shells out to the SDK's VideoEffectsApp.exe per job.
builder.Services.Configure<MaxineUpscaleOptions>(builder.Configuration.GetSection(MaxineUpscaleOptions.SectionName));
builder.Services.AddScoped<MaxineUpscaleService>();

// Optional "play faster" step: re-times a clip with ffmpeg after upscaling.
builder.Services.Configure<VideoSpeedOptions>(builder.Configuration.GetSection(VideoSpeedOptions.SectionName));
builder.Services.AddScoped<VideoSpeedService>();

// Self-hosted Stable Audio Open generative sound effects: a local Python server
// (tools/run-audio-server.ps1) that ClaudeCore calls over HTTP — no API key and no
// per-call cost, runs on the local GPU. Generous timeout since diffusion can take
// a while.
builder.Services.Configure<LocalAudioOptions>(builder.Configuration.GetSection(LocalAudioOptions.SectionName));
builder.Services.AddHttpClient<LocalAudioClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<LocalAudioOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes);
});
builder.Services.AddScoped<SoundGenService>();
// Start/stop control for the audio server (Admin page); not an auto-start service.
builder.Services.AddSingleton<AudioServerControl>();

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
