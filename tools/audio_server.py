"""
Local text-to-audio server for ClaudeCore (self-hosted alternative to ElevenLabs).

Loads Stable Audio Open via diffusers (pure PyTorch — no flash-attn / Apex, so it
runs on Blackwell / sm_120 with a torch cu128 build) and serves sound-effect
generation over HTTP. ClaudeCore's SoundGenService calls:

    GET  /health                    -> {status, model_loaded, model, device, sample_rate, error}
    POST /generate {text, seconds}  -> audio/wav bytes

Config via environment variables (set by tools/run-audio-server.ps1):
    AUDIO_PORT         listen port                 (default 8770)
    AUDIO_MODEL        HF model id                 (default stabilityai/stable-audio-open-1.0)
    AUDIO_STEPS        diffusion steps             (default 100)
    AUDIO_MAX_SECONDS  clamp for requested length  (default 30)

NOTE: the default is stable-audio-open-1.0 because it ships in diffusers format
(model_index.json). The "small" checkpoint is faster/lighter but ships in
stable-audio-tools format, which StableAudioPipeline can't load directly.
"""
import io
import os
import threading

import soundfile as sf
import torch
import uvicorn
from fastapi import FastAPI
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel

PORT = int(os.environ.get("AUDIO_PORT", "8770"))
MODEL_ID = os.environ.get("AUDIO_MODEL", "stabilityai/stable-audio-open-1.0")
STEPS = int(os.environ.get("AUDIO_STEPS", "100"))
MAX_SECONDS = float(os.environ.get("AUDIO_MAX_SECONDS", "30"))
NEGATIVE_PROMPT = os.environ.get("AUDIO_NEGATIVE_PROMPT", "Low quality.")

# Generation runs on the GPU when available, but the model is parked in CPU RAM
# between requests and only moved onto the GPU for the duration of a generation,
# so it doesn't hold VRAM the video models need on a shared single-GPU box.
GEN_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

app = FastAPI()
state = {"pipe": None, "device": GEN_DEVICE, "sample_rate": 44100, "error": None}
_gen_lock = threading.Lock()   # serialize generations (one GPU residency at a time)


class GenerateRequest(BaseModel):
    text: str
    seconds: float | None = None


@app.on_event("startup")
def load_model():
    try:
        from diffusers import StableAudioPipeline

        dtype = torch.float16 if GEN_DEVICE == "cuda" else torch.float32
        # Load to CPU RAM; moved onto the GPU only during a generation (see /generate).
        pipe = StableAudioPipeline.from_pretrained(MODEL_ID, torch_dtype=dtype)
        pipe.to("cpu")

        state["pipe"] = pipe
        state["sample_rate"] = int(pipe.vae.sampling_rate)
        print(f"[audio_server] loaded {MODEL_ID} (parked on CPU, generates on {GEN_DEVICE}) @ {state['sample_rate']} Hz", flush=True)
    except Exception as e:  # surfaced via /health rather than crashing the server
        state["error"] = repr(e)
        print(f"[audio_server] model load failed: {e!r}", flush=True)


@app.get("/health")
def health():
    return {
        "status": "ok" if state["pipe"] is not None else "error",
        "model_loaded": state["pipe"] is not None,
        "model": MODEL_ID,
        "device": state["device"],
        "sample_rate": state["sample_rate"],
        "error": state["error"],
    }


@app.post("/generate")
def generate(req: GenerateRequest):
    if state["pipe"] is None:
        return JSONResponse(status_code=503, content={"error": state["error"] or "model not loaded"})

    text = (req.text or "").strip()
    if not text:
        return JSONResponse(status_code=400, content={"error": "text is required"})

    seconds = req.seconds if (req.seconds and req.seconds > 0) else 10.0
    seconds = max(1.0, min(MAX_SECONDS, float(seconds)))

    pipe = state["pipe"]
    # Move onto the GPU just for this generation, then off again so the VRAM is
    # released for the video models. Serialized so only one residency happens at a time.
    with _gen_lock:
        try:
            if GEN_DEVICE == "cuda":
                pipe.to("cuda")
            # Fresh random seed each call so re-generating the same prompt gives variety.
            seed = int.from_bytes(os.urandom(4), "little")
            generator = torch.Generator(GEN_DEVICE).manual_seed(seed)
            result = pipe(
                prompt=text,
                negative_prompt=NEGATIVE_PROMPT,
                num_inference_steps=STEPS,
                audio_end_in_s=seconds,
                num_waveforms_per_prompt=1,
                generator=generator,
            ).audios
            # diffusers returns (channels, samples) float; soundfile wants (samples, channels).
            waveform = result[0].T.float().cpu().numpy()
        finally:
            if GEN_DEVICE == "cuda":
                pipe.to("cpu")
                torch.cuda.empty_cache()

    buf = io.BytesIO()
    sf.write(buf, waveform, state["sample_rate"], format="WAV")
    return Response(content=buf.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    print(f"[audio_server] starting on http://127.0.0.1:{PORT} (model={MODEL_ID}, steps={STEPS})", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="info")
