// KeithUI node studio — composes the EXISTING KeithVision operations as LiteGraph
// nodes. Edges carry files (image / audio / video) between operations; "Run"
// serializes the graph and posts it to /Studio/Run for execution.
(function () {
    "use strict";

    // Port type colors (so the wires read like ComfyUI).
    LiteGraph.IMAGE = "IMAGE"; LiteGraph.AUDIO = "AUDIO"; LiteGraph.VIDEO = "VIDEO";
    if (LGraphCanvas.link_type_colors) {
        LGraphCanvas.link_type_colors.IMAGE = "#7ac";
        LGraphCanvas.link_type_colors.AUDIO = "#caa84a";
        LGraphCanvas.link_type_colors.VIDEO = "#4caf76";
    }

    function define(type, title, color, build) {
        function Node() {
            this.serialize_widgets = true;   // include widget values in graph.serialize()
            build.call(this);
            this.color = color; this.bgcolor = "#2a2a2a";
        }
        Node.title = title;
        LiteGraph.registerNodeType(type, Node);
    }

    // A tall, multi-line text widget. LiteGraph's stock "text" widget is a single
    // 20px line that truncates at 30 chars; we want a big prompt box. Using a custom
    // type routes drawing + clicks to the default path, so we draw a multi-row box
    // with wrapped text and open LiteGraph's built-in multi-line editor on click.
    function wrapText(ctx, text, maxWidth) {
        var out = [];
        var paras = String(text == null ? "" : text).split("\n");
        for (var p = 0; p < paras.length; p++) {
            var words = paras[p].split(" "), line = "";
            for (var i = 0; i < words.length; i++) {
                var test = line ? line + " " + words[i] : words[i];
                if (line && ctx.measureText(test).width > maxWidth) { out.push(line); line = words[i]; }
                else line = test;
            }
            out.push(line);
        }
        return out;
    }
    function addMultilineText(node, name, value, rows) {
        var LINE = 16, LABEL_H = 15, PAD = 8;
        var w = node.addWidget("text", name, value, null, {});
        w.type = "multiline";   // route away from the single-line "text" renderer
        w.computeSize = function () { return [0, LABEL_H + rows * LINE + PAD]; };
        w.draw = function (ctx, n, width, y) {
            var margin = 15, boxW = width - margin * 2, h = LABEL_H + rows * LINE + PAD - 4;
            ctx.save();
            ctx.strokeStyle = LiteGraph.WIDGET_OUTLINE_COLOR;
            ctx.fillStyle = LiteGraph.WIDGET_BGCOLOR;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(margin, y, boxW, h, 6); else ctx.rect(margin, y, boxW, h);
            ctx.fill(); ctx.stroke();
            ctx.beginPath(); ctx.rect(margin, y, boxW, h); ctx.clip();
            ctx.textAlign = "left";
            ctx.font = "11px Arial"; ctx.fillStyle = LiteGraph.WIDGET_SECONDARY_TEXT_COLOR;
            ctx.fillText(this.name, margin + 8, y + 11);
            ctx.font = "12px Arial"; ctx.fillStyle = LiteGraph.WIDGET_TEXT_COLOR;
            var lines = wrapText(ctx, this.value, boxW - 16);
            for (var i = 0; i < lines.length && i < rows; i++)
                ctx.fillText(lines[i], margin + 8, y + LABEL_H + 9 + i * LINE);
            ctx.restore();
        };
        w.mouse = function (event, pos, n) {
            if (event.type === LiteGraph.pointerevents_method + "down") {
                openInlineEditor(n, this);   // type right in the node (not a popup)
                return true;
            }
            return false;
        };
        return w;
    }

    // Inline editing: classic LiteGraph has no DOM widgets, so we float a real
    // <textarea> over the prompt box and keep it aligned with pan/zoom while open.
    // Clicking the widget focuses it; typing edits in place; blur (click away)
    // commits. One shared textarea serves whichever prompt is being edited.
    var _ta = null, _edit = null, _raf = null;
    function placeInlineEditor() {
        if (!_edit) return;
        var node = _edit.node, w = _edit.widget, ds = window.lgcanvas.ds, scale = ds.scale, margin = 15;
        var c = ds.convertOffsetToCanvas([node.pos[0] + margin, node.pos[1] + (w.last_y || 0)]);
        var rect = document.getElementById("graph").getBoundingClientRect();
        var s = _ta.style;
        s.left = (rect.left + window.scrollX + c[0]) + "px";
        s.top = (rect.top + window.scrollY + c[1]) + "px";
        s.width = ((node.size[0] - margin * 2) * scale) + "px";
        s.height = ((w.computeSize()[1] - 4) * scale) + "px";
        s.fontSize = (12 * scale) + "px";
        s.lineHeight = (16 * scale) + "px";
    }
    function loopInlineEditor() {
        if (!_edit) { _raf = null; return; }
        placeInlineEditor();
        _raf = requestAnimationFrame(loopInlineEditor);
    }
    function closeInlineEditor() {
        if (!_edit) return;
        _edit.widget.value = _ta.value;
        if (_edit.widget.callback) _edit.widget.callback(_ta.value);
        _edit.node.setDirtyCanvas(true, true);
        _edit = null;
        _ta.style.display = "none";
    }
    function openInlineEditor(node, widget) {
        if (!document.getElementById("graph") || !window.lgcanvas) return;
        if (!_ta) {
            _ta = document.createElement("textarea");
            var s = _ta.style;
            s.position = "absolute"; s.zIndex = 50; s.resize = "none"; s.display = "none";
            s.background = "#222"; s.color = "#e6e6e6"; s.border = "1px solid #2d6cdf";
            s.borderRadius = "6px"; s.padding = "3px 6px"; s.boxSizing = "border-box";
            s.outline = "none"; s.fontFamily = "Arial"; s.overflow = "auto";
            document.body.appendChild(_ta);
            _ta.addEventListener("input", function () {
                if (_edit) { _edit.widget.value = _ta.value; _edit.node.setDirtyCanvas(true, true); }
            });
            _ta.addEventListener("blur", closeInlineEditor);
            _ta.addEventListener("keydown", function (e) {
                e.stopPropagation();           // don't let LiteGraph grab Delete/arrows/etc.
                if (e.key === "Escape") _ta.blur();
            });
            _ta.addEventListener("wheel", function (e) { e.stopPropagation(); });
        }
        _edit = { node: node, widget: widget };
        _ta.value = widget.value == null ? "" : widget.value;
        _ta.style.display = "block";
        placeInlineEditor();
        setTimeout(function () { _ta.focus(); _ta.select(); }, 0);
        if (_raf == null) loopInlineEditor();
    }

    // Drop LiteGraph's ~176 built-in stock nodes (math/const/logic/events/…) so the
    // "Add Node" menu shows ONLY the KeithVision-backed nodes defined below — every
    // node in the palette maps to a real executor case, nothing decorative.
    LiteGraph.clearRegisteredTypes();

    // --- Sources -----------------------------------------------------------
    define("Image/load_image", "Load Image", "#355", function () {
        var self = this;
        this.addOutput("image", LiteGraph.IMAGE);
        var fileW = this.addWidget("text", "file", "");
        this.addWidget("button", "📁 upload", null, function () {
            var inp = document.createElement("input");
            inp.type = "file"; inp.accept = "image/*";
            inp.onchange = async function () {
                if (!inp.files || !inp.files[0]) return;
                self.title = "Load Image — uploading…"; self.setDirtyCanvas(true, true);
                try {
                    var fd = new FormData(); fd.append("image", inp.files[0]);
                    var d = await (await fetch("/Studio/Upload", { method: "POST", body: fd })).json();
                    if (d.ok) {
                        fileW.value = d.path;
                        self.title = "Load Image";   // keep header clean; filename overflowed the node
                        self._img = new Image();
                        self._img.onload = function () { self.setDirtyCanvas(true, true); };
                        self._img.src = "/Studio/Image?path=" + encodeURIComponent(d.path);
                    } else { self.title = "Load Image — failed"; }
                } catch (e) { self.title = "Load Image — error"; }
                self.setDirtyCanvas(true, true);
            };
            inp.click();
        });
        // Re-attach the thumbnail after a graph reload (value persisted).
        if (fileW.value) {
            this._img = new Image();
            this._img.onload = function () { self.setDirtyCanvas(true, true); };
            this._img.src = "/Studio/Image?path=" + encodeURIComponent(fileW.value);
        }
        // Preview goes ON TOP: reserve a band at the top of the node body and start
        // the file + upload widgets below it (widgets_start_y), so the widget order /
        // serialization is unchanged — only the layout moves.
        var IMG_TOP = 28, IMG_H = 120;
        this.widgets_start_y = IMG_TOP + IMG_H + 8;
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var pad = 8, boxW = this.size[0] - pad * 2;
            ctx.fillStyle = "#1f1f1f"; ctx.strokeStyle = "#000"; ctx.lineWidth = 1;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(pad, IMG_TOP, boxW, IMG_H, 6); else ctx.rect(pad, IMG_TOP, boxW, IMG_H);
            ctx.fill(); ctx.stroke();
            if (this._img && this._img.width) {
                var w = boxW - 4, h = w * (this._img.height / this._img.width);
                if (h > IMG_H - 4) { h = IMG_H - 4; w = h * (this._img.width / this._img.height); }
                ctx.drawImage(this._img, (this.size[0] - w) / 2, IMG_TOP + (IMG_H - h) / 2, w, h);
            } else {
                ctx.fillStyle = "#666"; ctx.font = "11px Arial"; ctx.textAlign = "center";
                ctx.fillText("no image", this.size[0] / 2, IMG_TOP + IMG_H / 2 + 4); ctx.textAlign = "left";
            }
        };
        this.size = [220, 232];
    });

    define("Sound/sound", "Generate Sound", "#553", function () {
        this.addOutput("audio", LiteGraph.AUDIO);
        addMultilineText(this, "prompt", "", 5);   // tall, inline-editable (same as Generate Video)
        this.addWidget("number", "seconds", 5, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.size = this.computeSize();
        if (this.size[0] < 300) this.size[0] = 300;
    });

    // Upload an audio file to use as the Generate Video audio track (audio-to-video).
    define("Sound/load_sound", "Load Sound", "#553", function () {
        var self = this;
        this.addOutput("audio", LiteGraph.AUDIO);
        // Holds the staged path (widget index 0 — the executor reads it) but is not
        // drawn: showing a long absolute path overflowed the node. The "♪ name"
        // label below conveys what's loaded instead.
        var fileW = this.addWidget("text", "file", "");
        fileW.type = "hidden"; fileW.computeSize = function () { return [0, 0]; };
        this.addWidget("button", "📁 upload", null, function () {
            var inp = document.createElement("input");
            inp.type = "file"; inp.accept = "audio/*";
            inp.onchange = async function () {
                if (!inp.files || !inp.files[0]) return;
                self.title = "Load Sound — uploading…"; self.setDirtyCanvas(true, true);
                try {
                    var fd = new FormData(); fd.append("audio", inp.files[0]);
                    var d = await (await fetch("/Studio/UploadAudio", { method: "POST", body: fd })).json();
                    if (d.ok) { fileW.value = d.path; self._sndName = inp.files[0].name; self.title = "Load Sound"; }
                    else { self.title = "Load Sound — failed"; }
                } catch (e) { self.title = "Load Sound — error"; }
                self.setDirtyCanvas(true, true);
            };
            inp.click();
        });
        if (fileW.value) this._sndName = fileW.value.split(/[\\/]/).pop();
        // show the loaded clip's name (no waveform — audio has no visual preview)
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var label = this._sndName ? "♪ " + this._sndName : "no sound";
            if (label.length > 32) label = label.slice(0, 31) + "…";
            ctx.save();
            ctx.beginPath(); ctx.rect(0, 0, this.size[0], this.size[1]); ctx.clip();   // never overflow the node
            ctx.font = "11px Arial"; ctx.textAlign = "center";
            ctx.fillStyle = this._sndName ? "#caa84a" : "#666";
            ctx.fillText(label, this.size[0] / 2, this.size[1] - 8);
            ctx.restore();
            ctx.textAlign = "left";
        };
        this.size = [240, 78];
    });

    // --- Generate ----------------------------------------------------------
    define("Video/generate", "Generate Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional (i2v / Wan)
        this.addInput("audio", LiteGraph.AUDIO);    // optional (audio-to-video)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        addMultilineText(this, "prompt", "", 5);
        this.addWidget("combo", "resolution", "540p", null, { values: ["540p", "720p", "1080p"] });
        this.addWidget("number", "duration", 20, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.addWidget("combo", "aspect", "16:9", null, { values: ["16:9", "9:16"] });
        this.size = this.computeSize();          // size to fit every control inside the border
        if (this.size[0] < 320) this.size[0] = 320;
    });

    // Multi-segment generation past the per-run duration cap: each segment is
    // conditioned on the previous segment's last frame, then all are stitched.
    define("Video/extend", "Extend Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional start frame (i2v / Wan)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        addMultilineText(this, "prompt", "a drone shot flying over a coastline", 5);
        this.addWidget("combo", "resolution", "540p", null, { values: ["540p", "720p", "1080p"] });
        this.addWidget("number", "secPerSeg", 5, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.addWidget("number", "segments", 3, null, { min: 2, max: 8, step: 10, precision: 0 });
        this.addWidget("combo", "aspect", "16:9", null, { values: ["16:9", "9:16"] });
        this.size = this.computeSize();          // size to fit every control inside the border
        if (this.size[0] < 320) this.size[0] = 320;
    });

    // --- Post-processing ---------------------------------------------------
    // AI upscale (ComfyUI / Real-ESRGAN) — any target resolution. The default.
    define("Upscaling/upscale_ai", "Upscale (AI)", "#534", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "targetHeight", "2160", null, { values: ["720", "1080", "1440", "2160"] });
        this.size = [240, 80];
    });

    // Maxine upscale — fast, integer 2×/3×/4× only (the SDK rejects other ratios).
    define("Upscaling/upscale_maxine", "Upscale (MAXINE)", "#534", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "factor", "2", null, { values: ["2", "3", "4"] });
        this.size = [240, 80];
    });

    define("Speed/speed", "Speed Up", "#454", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "factor", "2", null, { values: ["1.5", "2", "3", "4"] });
        this.size = [200, 60];
    });

    // --- Sink --------------------------------------------------------------
    define("Preview Save/save", "Preview Save", "#444", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.size = [200, 40];
    });

    // --- Canvas ------------------------------------------------------------
    var canvasEl = document.getElementById("graph");
    var graph = new LGraph();
    var lgcanvas = new LGraphCanvas(canvasEl, graph);
    window.lgraph = graph; window.lgcanvas = lgcanvas;   // debug/automation hook

    // Per-node run status -> a colored outline + on-node progress bar (ComfyUI-style).
    var nodeStatus = {};
    var nodeProgress = {};   // nodeId -> 0..100 (undefined = indeterminate while running)
    lgcanvas.onDrawForeground = function (ctx) {
        var th = LiteGraph.NODE_TITLE_HEIGHT;
        for (var id in nodeStatus) {
            var node = graph.getNodeById(parseInt(id, 10));
            if (!node) continue;
            var st = nodeStatus[id];
            // status outline
            ctx.lineWidth = 3;
            ctx.strokeStyle = st === "running" ? "#ffd24a" : st === "done" ? "#4caf76" : "#e05555";
            var x = node.pos[0] - 3, y = node.pos[1] - th - 3, w = node.size[0] + 6, h = node.size[1] + th + 6;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(x, y, w, h, 8); else ctx.rect(x, y, w, h);
            ctx.stroke();
            // on-node progress bar (bottom edge of the body)
            if (st === "running") {
                var bx = node.pos[0], by = node.pos[1] + node.size[1] - 4, bw = node.size[0];
                ctx.fillStyle = "rgba(0,0,0,0.45)";
                ctx.fillRect(bx, by, bw, 4);
                var pct = nodeProgress[id];
                if (pct != null) { ctx.fillStyle = "#4caf76"; ctx.fillRect(bx, by, bw * Math.max(0, Math.min(100, pct)) / 100, 4); }
                else { ctx.fillStyle = "#ffd24a"; ctx.fillRect(bx, by, bw, 4); }   // indeterminate
            }
        }
    };

    function resize() {
        canvasEl.width = canvasEl.clientWidth;
        canvasEl.height = canvasEl.clientHeight;
        lgcanvas.resize();
    }
    window.addEventListener("resize", resize);

    function starterGraph() {
        graph.clear();
        var img = LiteGraph.createNode("Image/load_image"); img.pos = [30, 73]; graph.add(img);
        var gen = LiteGraph.createNode("Video/generate");   gen.pos = [298, 73]; graph.add(gen);
        var up = LiteGraph.createNode("Upscaling/upscale_ai");  up.pos = [663, 74]; graph.add(up);
        var save = LiteGraph.createNode("Preview Save/save");      save.pos = [943, 82]; graph.add(save);
        img.connect(0, gen, 0);   // Load Image -> Generate (image input)
        gen.connect(0, up, 0);    // Generate -> Upscale (AI)
        up.connect(0, save, 0);   // Upscale -> Save
        graph.start();
    }
    starterGraph();
    resize();

    // --- Run ---------------------------------------------------------------
    var statusEl = document.getElementById("status");
    var runBtn = document.getElementById("run-btn");
    var resultEl = document.getElementById("result");
    var resultVideo = document.getElementById("result-video");
    var resultLog = document.getElementById("result-log");
    var resultDl = document.getElementById("result-dl");
    document.getElementById("result-close").addEventListener("click", function () { resultEl.classList.remove("show"); });

    function handleEvent(ev) {
        switch (ev.type) {
            case "node-start":
                nodeStatus[ev.node] = "running";
                delete nodeProgress[ev.node];
                statusEl.textContent = "Running node " + ev.node + "…";
                break;
            case "node-progress":
                nodeProgress[ev.node] = ev.pct;
                statusEl.textContent = "Generating… " + (ev.pct || 0) + "%";
                break;
            case "node-done":
                nodeStatus[ev.node] = "done";
                delete nodeProgress[ev.node];
                break;
            case "node-error":
                nodeStatus[ev.node] = "error";
                statusEl.textContent = "Error at node " + ev.node + ": " + ev.error;
                break;
            case "log":
                resultLog.textContent += ev.text + "\n";
                resultEl.classList.add("show");
                break;
            case "done":
                if (ev.ok && ev.finalVideo) {
                    var url = "/Studio/Preview?path=" + encodeURIComponent(ev.finalVideo);
                    resultVideo.src = url; resultDl.href = url; resultEl.classList.add("show");
                    statusEl.textContent = "Done.";
                } else if (ev.ok) {
                    statusEl.textContent = "Finished (no video output).";
                } else {
                    statusEl.textContent = "Error: " + (ev.error || "failed");
                }
                break;
        }
        lgcanvas.setDirty(true, true);
    }

    // Nodes whose named input slot must be wired or the run can't do anything
    // useful (the executor would skip them, or there'd be nothing to save).
    var REQUIRED_INPUT = {
        "Preview Save/save": { slot: "video", label: "Preview Save" },
        "Upscaling/upscale_ai": { slot: "video", label: "Upscale (AI)" },
        "Upscaling/upscale_maxine": { slot: "video", label: "Upscale (MAXINE)" },
        "Speed/speed": { slot: "video", label: "Speed Up" },
    };
    function isLinked(n, slotName) {
        if (!n.inputs) return false;
        for (var i = 0; i < n.inputs.length; i++)
            if (n.inputs[i].name === slotName && n.inputs[i].link != null) return true;
        return false;
    }

    // Client-side checks before a (possibly minutes-long) run.
    function validateGraph() {
        var issues = [];
        graph._nodes.forEach(function (n) {
            if (n.type === "Video/generate" || n.type === "Video/extend") {
                var which = n.type === "Video/extend" ? "Extend" : "Generate";
                var model = n.widgets[0] && n.widgets[0].value;
                var prompt = n.widgets[1] && n.widgets[1].value;
                if (!prompt || !String(prompt).trim())
                    issues.push({ node: n.id, msg: which + " Video needs a prompt — add one before running." });
                if (model === "wan2.2" && !isLinked(n, "image"))
                    issues.push({ node: n.id, msg: "Wan 2.2 is image-to-video — connect a Load Image to the " + which + " node (or pick BF16/NVFP4)." });
                // Audio-to-video only works on the BF16 backend (NVFP4 is text-only, Wan is image-only).
                if (n.type === "Video/generate" && isLinked(n, "audio") && model !== "bf16-2.3")
                    issues.push({ node: n.id, msg: "Audio-to-video needs the BF16 model — set Generate's model to bf16-2.3, or disconnect the audio input." });
            }
            var req = REQUIRED_INPUT[n.type];
            if (req && !isLinked(n, req.slot))
                issues.push({ node: n.id, msg: req.label + " has no " + req.slot + " input connected — wire a video into it." });
            if (n.type === "Image/load_image") {
                var fileW = n.widgets && n.widgets[0];
                if (!fileW || !fileW.value)
                    issues.push({ node: n.id, msg: "Load Image has no file — click 📁 upload to pick an image." });
            }
            if (n.type === "Sound/load_sound") {
                var sndW = n.widgets && n.widgets[0];
                if (!sndW || !sndW.value)
                    issues.push({ node: n.id, msg: "Load Sound has no file — click 📁 upload to pick an audio file." });
            }
            if (n.type === "Sound/sound") {
                var sp = n.widgets && n.widgets[0] && n.widgets[0].value;
                if (!sp || !String(sp).trim())
                    issues.push({ node: n.id, msg: "Generate Sound needs a prompt — add one before running." });
            }
        });
        return issues;
    }

    runBtn.addEventListener("click", async function () {
        var issues = validateGraph();
        if (issues.length) {
            nodeStatus = {}; nodeProgress = {};
            issues.forEach(function (it) { nodeStatus[it.node] = "error"; });
            statusEl.textContent = "⚠ " + issues[0].msg;
            lgcanvas.setDirty(true, true);
            return;
        }
        nodeStatus = {}; nodeProgress = {};
        resultLog.textContent = "";
        statusEl.textContent = "Running… (video generation can take minutes)";
        runBtn.disabled = true;
        lgcanvas.setDirty(true, true);
        try {
            var resp = await fetch("/Studio/Run", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(graph.serialize())
            });
            var reader = resp.body.getReader();
            var dec = new TextDecoder();
            var buf = "";
            while (true) {
                var chunk = await reader.read();
                if (chunk.done) break;
                buf += dec.decode(chunk.value, { stream: true });
                var lines = buf.split("\n");
                buf = lines.pop();
                for (var i = 0; i < lines.length; i++) {
                    if (lines[i].trim()) { try { handleEvent(JSON.parse(lines[i])); } catch (e) { } }
                }
            }
        } catch (e) {
            statusEl.textContent = "Run failed: " + e.message;
        } finally {
            runBtn.disabled = false;
        }
    });
    document.getElementById("reset-btn").addEventListener("click", function () {
        nodeStatus = {}; nodeProgress = {}; starterGraph(); resize(); statusEl.textContent = "";
    });

    // --- Save / load graphs ------------------------------------------------
    // After a graph.configure(), Load Image nodes need their thumbnail re-fetched:
    // the build() constructor runs before widget values are applied, so the
    // in-constructor re-attach sees an empty path. Do it here once values exist.
    function reattachThumbnails() {
        graph._nodes.forEach(function (n) {
            if (n.type !== "Image/load_image") return;
            var fileW = n.widgets && n.widgets[0];
            if (!fileW || !fileW.value) return;
            n._img = new Image();
            n._img.onload = function () { n.setDirtyCanvas(true, true); };
            n._img.src = "/Studio/Image?path=" + encodeURIComponent(fileW.value);
        });
    }

    document.getElementById("save-btn").addEventListener("click", function () {
        var json = JSON.stringify(graph.serialize(), null, 2);
        var a = document.createElement("a");
        a.href = URL.createObjectURL(new Blob([json], { type: "application/json" }));
        a.download = "keithui-graph-" + new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-") + ".json";
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
        URL.revokeObjectURL(a.href);
        statusEl.textContent = "Saved graph to " + a.download;
    });

    var loadFile = document.getElementById("load-file");
    document.getElementById("load-btn").addEventListener("click", function () { loadFile.click(); });
    loadFile.addEventListener("change", function () {
        if (!loadFile.files || !loadFile.files[0]) return;
        var reader = new FileReader();
        reader.onload = function () {
            try {
                var data = JSON.parse(reader.result);
                nodeStatus = {}; nodeProgress = {};
                graph.configure(data);
                graph.start();
                reattachThumbnails();
                resize();
                statusEl.textContent = "Loaded " + graph._nodes.length + " nodes from " + loadFile.files[0].name;
            } catch (e) {
                statusEl.textContent = "Load failed: " + e.message;
            }
            loadFile.value = "";   // allow re-loading the same file
        };
        reader.readAsText(loadFile.files[0]);
    });
})();
