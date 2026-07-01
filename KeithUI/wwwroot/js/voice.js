// Voice page: record/upload -> stage -> RVC convert -> play/download/send.
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
    const resultWrap = $("vc-result-wrap");
    const resultAudio = $("vc-result");
    const downloadLink = $("vc-download");
    const sendBtn = $("vc-send");
    // AI Voice (RVC) card.
    const loadVoicesBtn = $("vc-load-voices");
    const voiceSelect = $("vc-voice");
    const rvcPitch = $("vc-rvc-pitch");
    const rvcPitchVal = $("vc-rvc-pitch-val");
    const convertBtn = $("vc-convert");

    // App state.
    let stagedPath = null;     // server path of the staged source clip
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
        convertBtn.disabled = !(stagedPath && voiceSelect.value);
    }

    function showResult(data) {
        resultPath = data.path;
        resultAudio.src = data.url;
        downloadLink.href = data.url;
        resultWrap.classList.remove("d-none");
        setStatus("Done — " + data.name, "success");
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
            setStatus("Clip ready — load voices and pick a target.", "success");
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

    // --- AI Voice (RVC) ------------------------------------------------------

    rvcPitch.addEventListener("input", () => { rvcPitchVal.textContent = rvcPitch.value; });
    voiceSelect.addEventListener("change", refreshApply);

    async function loadVoices() {
        loadVoicesBtn.disabled = true;
        setStatus("Starting RVC server and loading voices… (first start can take a while)", "info");
        try {
            const resp = await fetch("/Voice/Voices");
            const data = await resp.json();
            if (!data.ok) throw new Error(data.error || "Could not list voices.");
            voiceSelect.innerHTML = "";
            if (!data.voices.length) {
                voiceSelect.innerHTML = '<option value="">(no voice models installed)</option>';
                setStatus("RVC is running but no voice models were found in the models folder.", "warning");
            } else {
                data.voices.forEach((v) => {
                    const o = document.createElement("option");
                    o.value = v; o.textContent = v;
                    voiceSelect.appendChild(o);
                });
                voiceSelect.disabled = false;
                setStatus("Loaded " + data.voices.length + " voice(s). Pick one and convert.", "success");
            }
        } catch (err) {
            setStatus("Loading voices failed: " + err.message, "danger");
        } finally {
            loadVoicesBtn.disabled = false;
            refreshApply();
        }
    }

    async function convertVoice() {
        if (!stagedPath || !voiceSelect.value) return;
        convertBtn.disabled = true;
        setStatus("Converting voice… (first run loads the model)", "info");
        try {
            const resp = await fetch("/Voice/Convert", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path: stagedPath, voice: voiceSelect.value, transpose: Number(rvcPitch.value) }),
            });
            const data = await resp.json();
            if (!resp.ok || !data.ok) throw new Error(data.error || "Conversion failed.");
            showResult(data);
        } catch (err) {
            setStatus("Conversion failed: " + err.message, "danger");
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

    // Mic capture (getUserMedia / MediaRecorder) only works in a SECURE CONTEXT:
    // HTTPS, or a loopback host (localhost / 127.x). The hosted app at
    // http://www.keithui.com is plain HTTP on a custom hostname, so the browser
    // disables navigator.mediaDevices there. Detect that up front, disable Record,
    // and point the user at the loopback URL (uploading a file still works).
    function recordingSupported() {
        return !!(window.isSecureContext &&
            navigator.mediaDevices && navigator.mediaDevices.getUserMedia &&
            window.MediaRecorder);
    }

    function applyRecordingSupport() {
        if (recordingSupported()) return;
        recordBtn.disabled = true;
        stopBtn.disabled = true;
        recordBtn.title = "Recording needs a secure (HTTPS / localhost) page";
        if (!window.isSecureContext) {
            var alt = location.protocol + "//127.0.0.2" +
                (location.port && location.port !== "80" ? ":" + location.port : "") + location.pathname;
            setStatus("Recording is disabled here — the browser only allows the mic on a secure page. " +
                "Open " + alt + " (a loopback address) to record, or just upload a file below.", "warning");
        } else {
            setStatus("This browser can't record audio (MediaRecorder unavailable). Upload a file below instead.", "warning");
        }
    }

    // --- Wire up -------------------------------------------------------------

    recordBtn.addEventListener("click", startRecording);
    stopBtn.addEventListener("click", stopRecording);
    sendBtn.addEventListener("click", sendToStudio);
    loadVoicesBtn.addEventListener("click", loadVoices);
    convertBtn.addEventListener("click", convertVoice);

    applyRecordingSupport();
})();
