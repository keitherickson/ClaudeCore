"""
Local LLM "prompt enhancer" server for ClaudeCore / KeithUI.

Turns a short idea into ONE vivid text-to-video prompt. Runs a local instruct
model (default Qwen2.5-7B-Instruct) on the 4090 — pinned via CUDA_VISIBLE_DEVICES
by tools/run-prompt-server.ps1 — so it never competes with the video models on the
5090. No API key, no per-call cost.

Set up the venv + weights first (a dedicated prompt-venv with transformers + torch +
fastapi + uvicorn). The model downloads from Hugging Face on first run.

Endpoints:
  GET  /health                              -> {status, model_loaded, model, device, error}
  POST /enhance  {text, style?, maxTokens?} -> {prompt}
"""
import os
import threading

import torch
import uvicorn
from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

MODEL_ID    = os.environ.get("PROMPT_MODEL", "Qwen/Qwen2.5-7B-Instruct")
PORT        = int(os.environ.get("PROMPT_PORT", "8771"))
MAX_TOKENS  = int(os.environ.get("PROMPT_MAX_TOKENS", "220"))
TEMPERATURE = float(os.environ.get("PROMPT_TEMPERATURE", "0.8"))

# Resident on whatever GPU the launcher made visible (the 4090). Pinned there, so
# unlike a shared box there's no contention with the video models on the 5090.
GEN_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

app = FastAPI()
state = {"model": None, "tokenizer": None, "device": GEN_DEVICE, "error": None}
_gen_lock = threading.Lock()   # serialize generations (one decode on the model at a time)

# Short style hints appended to the user's idea (kept terse so the model leads, not the preset).
STYLE_GUIDANCE = {
    "cinematic": "cinematic, filmic lighting, shallow depth of field, dynamic camera movement",
    "photoreal": "photorealistic, natural lighting, lifelike detail",
    "anime": "anime style, vibrant cel shading, expressive",
    "vivid": "vivid saturated colors, high energy, dramatic",
    "none": "",
}

SYSTEM = (
    "You are a prompt engineer for a text-to-video model. Rewrite the user's idea into "
    "ONE concise, vivid prompt of 1-3 sentences. Describe the subject, setting, lighting, "
    "and camera motion. Output ONLY the prompt text — no preamble, no quotes, no lists, "
    "no explanation."
)


class EnhanceRequest(BaseModel):
    text: str
    style: str | None = None
    maxTokens: int | None = None


@app.on_event("startup")
def load_model():
    try:
        from transformers import AutoModelForCausalLM, AutoTokenizer

        tok = AutoTokenizer.from_pretrained(MODEL_ID)
        dtype = torch.float16 if GEN_DEVICE == "cuda" else torch.float32
        model = AutoModelForCausalLM.from_pretrained(MODEL_ID, torch_dtype=dtype).to(GEN_DEVICE)
        model.eval()

        state["tokenizer"] = tok
        state["model"] = model
        print(f"[prompt_server] loaded {MODEL_ID} on {GEN_DEVICE} (resident)", flush=True)
    except Exception as e:  # surfaced via /health rather than crashing the server
        state["error"] = repr(e)
        print(f"[prompt_server] model load failed: {e!r}", flush=True)


@app.get("/health")
def health():
    return {
        "status": "ok" if state["model"] is not None else "error",
        "model_loaded": state["model"] is not None,
        "model": MODEL_ID,
        "device": state["device"],
        "error": state["error"],
    }


@app.post("/enhance")
def enhance(req: EnhanceRequest):
    if state["model"] is None:
        return JSONResponse(status_code=503, content={"error": state["error"] or "model not loaded"})

    text = (req.text or "").strip()
    if not text:
        return JSONResponse(status_code=400, content={"error": "text is required"})

    style = (req.style or "cinematic").strip().lower()
    extra = STYLE_GUIDANCE.get(style, "")
    user = text if not extra else f"{text}\n\nStyle: {extra}"
    max_new = req.maxTokens if (req.maxTokens and req.maxTokens > 0) else MAX_TOKENS

    tok = state["tokenizer"]
    model = state["model"]
    with _gen_lock:
        messages = [{"role": "system", "content": SYSTEM}, {"role": "user", "content": user}]
        inputs = tok.apply_chat_template(messages, add_generation_prompt=True, return_tensors="pt").to(GEN_DEVICE)
        with torch.no_grad():
            out = model.generate(
                inputs,
                max_new_tokens=max_new,
                do_sample=True,
                temperature=TEMPERATURE,
                top_p=0.9,
                pad_token_id=(tok.eos_token_id if tok.eos_token_id is not None else tok.pad_token_id),
            )
        # Only the newly generated tokens (drop the prompt the model echoes back).
        gen = out[0][inputs.shape[-1]:]
        result = tok.decode(gen, skip_special_tokens=True).strip()

    # Models sometimes wrap the line in quotes — strip a single matching pair.
    if len(result) >= 2 and result[0] in "\"'" and result[-1] == result[0]:
        result = result[1:-1].strip()
    return {"prompt": result or text}


if __name__ == "__main__":
    print(f"[prompt_server] starting on http://127.0.0.1:{PORT} (model={MODEL_ID})", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="info")
