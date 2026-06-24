using KeithVision.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the entire KeithVision service layer (options, typed HTTP clients,
/// backends, process controls, hosted services). The web host — and any future host
/// such as an MCP server — wires up the services with a single call.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKeithVisionServices(this IServiceCollection services, IConfiguration configuration)
    {
        // UI branding (display name) from the "Branding" config section.
        services.Configure<BrandingOptions>(configuration.GetSection(BrandingOptions.SectionName));

        // Default selections for the Generate Video form ("VideoDefaults" section).
        services.Configure<VideoDefaultsOptions>(configuration.GetSection(VideoDefaultsOptions.SectionName));

        // Live GPU footer readout ("Gpu" section).
        services.Configure<GpuOptions>(configuration.GetSection(GpuOptions.SectionName));
        // Samples system CPU utilization for the footer readout.
        services.AddSingleton<SystemStatsService>();

        // LTX-2 local video generation: bind options, register the typed HttpClient
        // (with a generous timeout since /api/generate blocks until the video is done),
        // and the orchestration service.
        services.Configure<LtxVideoOptions>(configuration.GetSection(LtxVideoOptions.SectionName));
        services.AddHttpClient<LtxVideoClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<LtxVideoOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
        });

        // ComfyUI NVFP4 backend (the "fast" model). Typed HttpClient with a generous
        // timeout since /prompt is polled to completion.
        services.Configure<ComfyUiOptions>(configuration.GetSection(ComfyUiOptions.SectionName));
        services.AddHttpClient<ComfyUiVideoBackend>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ComfyUiOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
        });

        // Wan 2.2 14B image-to-video backend (the "quality" model). Runs on the SAME ComfyUI
        // server as the NVFP4 backend, so its typed HttpClient points at the shared ComfyUI URL.
        services.Configure<WanOptions>(configuration.GetSection(WanOptions.SectionName));
        services.AddHttpClient<WanVideoBackend>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<WanOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.GenerationTimeoutMinutes);
        });

        // Model switch: the registry of selectable models + the persisted active selection,
        // and all backends exposed as ILtxVideoBackend so LtxVideoService can route by key.
        services.Configure<VideoModelsOptions>(configuration.GetSection(VideoModelsOptions.SectionName));
        services.AddSingleton<ActiveModelStore>();
        services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<LtxVideoClient>());
        services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<ComfyUiVideoBackend>());
        services.AddScoped<ILtxVideoBackend>(sp => sp.GetRequiredService<WanVideoBackend>());

        services.AddScoped<LtxVideoService>();
        // Process control for the LTX server (admin page: status + restart + stop).
        services.AddSingleton<LtxServerControl>();
        // Process control for the ComfyUI NVFP4 server (started/stopped by the model switch).
        services.AddSingleton<ComfyUiServerControl>();
        // Topology-aware backend lifecycle: switching models stops co-resident (same-GPU)
        // backends and starts the selected one. Reconciler re-applies it on startup.
        services.AddSingleton<VideoBackendCoordinator>();
        services.AddHostedService<VideoBackendReconciler>();
        // Remembers the last-used starting image across requests/restarts.
        services.AddSingleton<LastImageStore>();

        // NVIDIA Maxine upscaling: shells out to the SDK's VideoEffectsApp.exe per job.
        services.Configure<MaxineUpscaleOptions>(configuration.GetSection(MaxineUpscaleOptions.SectionName));
        services.AddScoped<MaxineUpscaleService>();

        // AI upscaling via ComfyUI (arbitrary target resolution) — alternative engine to Maxine,
        // on the shared ComfyUI server. Typed HttpClient with a generous timeout (frame-by-frame).
        services.Configure<ComfyUiUpscaleOptions>(configuration.GetSection(ComfyUiUpscaleOptions.SectionName));
        services.AddHttpClient<ComfyUiUpscaleService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ComfyUiUpscaleOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes + 5);
        });

        // Optional "play faster" step: re-times a clip with ffmpeg after upscaling.
        services.Configure<VideoSpeedOptions>(configuration.GetSection(VideoSpeedOptions.SectionName));
        services.AddScoped<VideoSpeedService>();

        // Self-hosted Stable Audio Open generative sound effects: a local Python server
        // (tools/run-audio-server.ps1) that KeithVision calls over HTTP — no API key and no
        // per-call cost, runs on the local GPU. Generous timeout since diffusion can take
        // a while.
        services.Configure<LocalAudioOptions>(configuration.GetSection(LocalAudioOptions.SectionName));
        services.AddHttpClient<LocalAudioClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalAudioOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes);
        });
        services.AddScoped<SoundGenService>();
        // Start/stop control for the audio server (Admin page); not an auto-start service.
        services.AddSingleton<AudioServerControl>();

        // Self-hosted prompt-enhancer LLM: a local Python server (tools/run-prompt-server.ps1)
        // on the 4090 that rewrites a short idea into a vivid text-to-video prompt. Generous
        // timeout since the first call can include the model load.
        services.Configure<LocalLlmOptions>(configuration.GetSection(LocalLlmOptions.SectionName));
        services.AddHttpClient<LocalLlmClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalLlmOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes);
        });
        services.AddScoped<PromptEnhanceService>();
        // Start/stop + auto-start control for the prompt server (Admin page + on-demand).
        services.AddSingleton<PromptServerControl>();

        return services;
    }
}
