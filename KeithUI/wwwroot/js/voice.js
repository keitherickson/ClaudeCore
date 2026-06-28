// Voice Changer page: record/upload -> stage -> apply ffmpeg effect -> play/download/send.
(function () {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const statusEl = $("vc-status");
    const recordBtn = $("vc-record");
    const stopBtn = $("vc-stop");
    const timerEl = $("vc-timer");
    const fileInput = $("vc-file");
    const sourceWrap = $("vc-source-wrap");
    const sourceAudio = $("vc-source");
    const presetsEl = $("vc-presets");
    const pitchInput = $("vc-pitch");
    const pitchVal = $("vc-pitch-val");
    const applyBtn = $("vc-apply");
    const resultWrap = $("vc-result-wrap");
    const resultAudio = $("vc-result");
    const downloadLink = $("vc-download");
    const sendBtn = $("vc-send");

    // App state.
    let stagedPath = null;     // server path of the staged source clip
    let selectedEffect = null; // chosen preset id
    let resultPath = null;     // server path of the produced clip
    let mediaRecorder = null;
    let chunks = [];
    let timer = null;
    let seconds = 0;

    function setStatus(msg, kind) {
        statusEl.textContent = msg;
        statusEl.className = "alert py-2 alert-" + (kind || "secondary");
    }

    function refreshApply() {
        applyBtn.disabled = !(stagedPath && selectedEffect);
    }

    // --- Recording -----------------------------------------------------------

    async function startRecording() {
        if (!navigator.mediaDevices || !window.MediaRecorder) {
            setStatus("This browser can't record audio (MediaRecorder unavailable).", "danger");
            return;
        }
        let stream;
        try {
            stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        } catch (err) {
            setStatus("Microphone access was denied or unavailable.", "danger");
            return;
        }

        chunks = [];
        mediaRecorder = new MediaRecorder(stream);
        mediaRecorder.ondataavailable = (e) => { if (e.data && e.data.size) chunks.push(e.data); };
        mediaRecorder.onstop = async () => {
            stream.getTracks().forEach((t) => t.stop());
            const type = mediaRecorder.mimeType || "audio/webm";
            const ext = type.includes("ogg") ? "ogg" : type.includes("mp4") ? "mp4" : "webm";
            const blob = new Blob(chunks, { type });
            showSource(URL.createObjectURL(blob));
            await uploadClip(blob, "recording." + ext);
        };
        mediaRecorder.start();

        seconds = 0;
        timerEl.textContent = "0:00";
        timer = setInterval(() => {
            seconds++;
            const m = Math.floor(seconds / 60);
            const s = String(seconds % 60).padStart(2, "0");
            timerEl.textContent = m + ":" + s;
        }, 1000);

        recordBtn.disabled = true;
        stopBtn.disabled = false;
        setStatus("Recording…", "danger");
    }

    function stopRecording() {
        if (mediaRecorder && mediaRecorder.state !== "inactive") mediaRecorder.stop();
        if (timer) { clearInterval(timer); timer = null; }
        recordBtn.disabled = false;
        stopBtn.disabled = true;
    }

    function showSource(url) {
        sourceAudio.src = url;
        sourceWrap.classList.remove("d-none");
    }

    // --- Upload / staging ----------------------------------------------------

    async function uploadClip(blob, filename) {
        setStatus("Uploading clip…", "info");
        stagedPath = null;
        refreshApply();
        const fd = new FormData();
        fd.append("audio", blob, filename);
        try {
            const resp = await fetch("/Voice/Upload", { method: "POST", body: fd });
            const data = await resp.json();
            if (!resp.ok || !data.ok) throw new Error(data.error || "Upload failed.");
            stagedPath = data.path;
            setStatus("Clip ready — pick an effect.", "success");
            refreshApply();
        } catch (err) {
            setStatus("Upload failed: " + err.message, "danger");
        }
    }

    fileInput.addEventListener("change", () => {
        const f = fileInput.files && fileInput.files[0];
        if (!f) return;
        showSource(URL.createObjectURL(f));
        uploadClip(f, f.name);
    });

    // --- Presets -------------------------------------------------------------

    async function loadPresets() {
        try {
            const resp = await fetch("/Voice/Presets");
            const presets = await resp.json();
            presetsEl.innerHTML = "";
            presets.forEach((p) => {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "btn btn-outline-secondary";
                btn.textContent = p.label;
                btn.title = p.description;
                btn.dataset.effect = p.id;
                btn.addEventListener("click", () => selectEffect(p.id));
                presetsEl.appendChild(btn);
            });
        } catch (err) {
            presetsEl.innerHTML = '<span class="text-danger">Could not load effects.</span>';
        }
    }

    function selectEffect(id) {
        selectedEffect = id;
        presetsEl.querySelectorAll("button").forEach((b) => {
            const active = b.dataset.effect === id;
            b.classList.toggle("btn-secondary", active);
            b.classList.toggle("btn-outline-secondary", !active);
        });
        refreshApply();
    }

    pitchInput.addEventListener("input", () => { pitchVal.textContent = pitchInput.value; });

    // --- Apply ---------------------------------------------------------------

    async function applyEffect() {
        if (!stagedPath || !selectedEffect) return;
        applyBtn.disabled = true;
        setStatus("Applying effect…", "info");
        try {
            const resp = await fetch("/Voice/Process", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path: stagedPath, effect: selectedEffect, pitch: Number(pitchInput.value) }),
            });
            const data = await resp.json();
            if (!resp.ok || !data.ok) throw new Error(data.error || "Processing failed.");
            resultPath = data.path;
            resultAudio.src = data.url;
            downloadLink.href = data.url;
            resultWrap.classList.remove("d-none");
            setStatus("Done — " + data.name, "success");
        } catch (err) {
            setStatus("Processing failed: " + err.message, "danger");
        } finally {
            refreshApply();
        }
    }

    // --- Send to studio ------------------------------------------------------

    async function sendToStudio() {
        if (!resultPath) return;
        sendBtn.disabled = true;
        setStatus("Sending to studio…", "info");
        try {
            const resp = await fetch("/Voice/SendToStudio", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path: resultPath }),
            });
            const data = await resp.json();
            if (!resp.ok || !data.ok) throw new Error(data.error || "Send failed.");
            setStatus("Sent to studio as " + data.name + " — use a Load Sound node.", "success");
        } catch (err) {
            setStatus("Send failed: " + err.message, "danger");
        } finally {
            sendBtn.disabled = false;
        }
    }

    // --- Wire up -------------------------------------------------------------

    recordBtn.addEventListener("click", startRecording);
    stopBtn.addEventListener("click", stopRecording);
    applyBtn.addEventListener("click", applyEffect);
    sendBtn.addEventListener("click", sendToStudio);

    loadPresets();
})();
