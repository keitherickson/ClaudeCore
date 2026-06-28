"""
Local text-to-music server for ClaudeCore (self-hosted, no API / no per-call cost).

Loads Meta's MusicGen via Transformers (pure PyTorch — no audiocraft / xformers, so
it runs on Blackwell / sm_120 with a torch cu128 build) and serves instrumental
music generation over HTTP. ClaudeCore's MusicGenService calls:

    GET  /health                    -> {status, model_loaded, model, device, sample_rate, error}
    POST /generate {text, seconds}  -> audio/wav bytes

Config via environment variables (set by tools/run-music-server.ps1):
    MUSIC_PORT         listen port                 (default 8772)
    MUSIC_MODEL        HF model id                 (default facebook/musicgen-medium)
    MUSIC_MAX_SECONDS  clamp for requested length  (default 30)
    MUSIC_GUIDANCE     classifier-free guidance    (default 3.0)

MODEL NOTES: facebook/musicgen-{small,medium,large} trade quality for VRAM/speed
(~0.3B / 1.5B / 3.3B). MusicGen is trained on 30s clips, so MUSIC_MAX_SECONDS=30 by
default — longer requests still work but quality drifts. Output is instrumental
(MusicGen does not sing lyrics); for vocals use a different model (ACE-Step / YuE).
The deps are a subset of the audio venv, so run-music-server.ps1 reuses it.
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

PORT = int(os.environ.get("MUSIC_PORT", "8772"))
MODEL_ID = os.environ.get("MUSIC_MODEL", "facebook/musicgen-medium")
MAX_SECONDS = float(os.environ.get("MUSIC_MAX_SECONDS", "30"))
GUIDANCE = float(os.environ.get("MUSIC_GUIDANCE", "3.0"))

# MusicGen's audio codec emits ~50 frames (tokens) per second; max_new_tokens is
# therefore seconds * 50. Used to translate a requested duration into a token budget.
TOKENS_PER_SECOND = 50

# The model loads onto the GPU and STAYS resident. On this box the music server is
# pinned to its own card (the 4090 via CUDA_VISIBLE_DEVICES), so there's no VRAM
# contention with the video models on the 5090 — keeping it resident avoids the
# host<->device reload each generation would otherwise pay.
GEN_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

app = FastAPI()
state = {"model": None, "processor": None, "device": GEN_DEVICE, "sample_rate": 32000, "error": None}
_gen_lock = threading.Lock()   # serialize generations (one decode on the model at a time)


class GenerateRequest(BaseModel):
    text: str
    seconds: float | None = None


@app.on_event("startup")
def load_model():
    try:
        from transformers import AutoProcessor, MusicgenForConditionalGeneration

        dtype = torch.float16 if GEN_DEVICE == "cuda" else torch.float32
        processor = AutoProcessor.from_pretrained(MODEL_ID)
        model = MusicgenForConditionalGeneration.from_pretrained(MODEL_ID, torch_dtype=dtype).to(GEN_DEVICE)

        state["processor"] = processor
        state["model"] = model
        state["sample_rate"] = int(model.config.audio_encoder.sampling_rate)
        print(f"[music_server] loaded {MODEL_ID} on {GEN_DEVICE} (resident) @ {state['sample_rate']} Hz", flush=True)
    except Exception as e:  # surfaced via /health rather than crashing the server
        state["error"] = repr(e)
        print(f"[music_server] model load failed: {e!r}", flush=True)


@app.get("/health")
def health():
    return {
        "status": "ok" if state["model"] is not None else "error",
        "model_loaded": state["model"] is not None,
        "model": MODEL_ID,
        "device": state["device"],
        "sample_rate": state["sample_rate"],
        "error": state["error"],
    }


@app.post("/generate")
def generate(req: GenerateRequest):
    if state["model"] is None:
        return JSONResponse(status_code=503, content={"error": state["error"] or "model not loaded"})

    text = (req.text or "").strip()
    if not text:
        return JSONResponse(status_code=400, content={"error": "text is required"})

    seconds = req.seconds if (req.seconds and req.seconds > 0) else 10.0
    seconds = max(1.0, min(MAX_SECONDS, float(seconds)))
    max_new_tokens = int(seconds * TOKENS_PER_SECOND)

    model, processor = state["model"], state["processor"]
    # The model stays resident on GEN_DEVICE; serialize so two requests don't decode at once.
    with _gen_lock:
        inputs = processor(text=[text], padding=True, return_tensors="pt").to(GEN_DEVICE)
        with torch.inference_mode():
            audio = model.generate(
                **inputs,
                do_sample=True,
                guidance_scale=GUIDANCE,
                max_new_tokens=max_new_tokens,
            )
        # MusicGen returns (batch, channels, samples); take the first, mono clip.
        waveform = audio[0, 0].float().cpu().numpy()

    buf = io.BytesIO()
    sf.write(buf, waveform, state["sample_rate"], format="WAV")
    return Response(content=buf.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    print(f"[music_server] starting on http://127.0.0.1:{PORT} (model={MODEL_ID})", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="info")
