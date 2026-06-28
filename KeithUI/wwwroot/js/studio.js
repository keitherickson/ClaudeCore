// KeithUI node studio — composes the EXISTING KeithVision operations as LiteGraph
// nodes. Edges carry files (image / audio / video) between operations; "Run"
// serializes the graph and posts it to /Studio/Run for execution.
(function () {
    "use strict";

    // Where the Preview Save node's inline video area starts (below its filename +
    // download widgets). Shared by the node's draw and the video-overlay placement.
    var SAVE_VIDEO_TOP = 80;

    // Port type colors (so the wires read like ComfyUI).
    LiteGraph.IMAGE = "IMAGE"; LiteGraph.AUDIO = "AUDIO"; LiteGraph.VIDEO = "VIDEO"; LiteGraph.TEXT = "TEXT";
    if (LGraphCanvas.link_type_colors) {
        LGraphCanvas.link_type_colors.IMAGE = "#7ac";
        LGraphCanvas.link_type_colors.AUDIO = "#caa84a";
        LGraphCanvas.link_type_colors.VIDEO = "#4caf76";
        LGraphCanvas.link_type_colors.TEXT = "#b07ad0";
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

    // Poster-frame thumbnail for the Load Video node: decode one frame of the
    // staged clip into an off-screen <video> and hand it to the node to draw
    // (drawImage accepts a video element). Only commit the element once a frame
    // has actually decoded (the "seeked" event), so onDrawBackground never calls
    // drawImage on an empty video. Shared by the node's upload path and the
    // post-load rehydration (reattachThumbnails).
    function attachVideoThumb(node, path) {
        node._vid = null;
        var v = document.createElement("video");
        v.muted = true; v.preload = "auto"; v.playsInline = true;
        v.src = "/Studio/InputVideo?path=" + encodeURIComponent(path);
        v.addEventListener("loadeddata", function () {
            try { v.currentTime = Math.min(0.1, (v.duration || 2) / 2); } catch (e) { /* ignore */ }
        });
        v.addEventListener("seeked", function () { node._vid = v; node.setDirtyCanvas(true, true); });
        node._vidLoading = v;   // keep a ref so it isn't GC'd before it decodes
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
        this.addWidget("number", "seconds", 21, null, { min: 1, max: 30, step: 10, precision: 0 });
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
        this.size = [220, 78];
    });

    // Load an existing video clip (skip generation) to feed Upscale / Speed Up / etc.
    define("Video/load_video", "Load Video", "#454", function () {
        var self = this;
        this.addOutput("video", LiteGraph.VIDEO);
        var fileW = this.addWidget("text", "file", "");
        fileW.type = "hidden"; fileW.computeSize = function () { return [0, 0]; };

        this.addWidget("button", "📁 upload", null, function () {
            var inp = document.createElement("input");
            inp.type = "file"; inp.accept = "video/*";
            inp.onchange = async function () {
                if (!inp.files || !inp.files[0]) return;
                self._vid = null; self.title = "Load Video — uploading…"; self.setDirtyCanvas(true, true);
                try {
                    var fd = new FormData(); fd.append("video", inp.files[0]);
                    var d = await (await fetch("/Studio/UploadVideo", { method: "POST", body: fd })).json();
                    if (d.ok) {
                        fileW.value = d.path; self._vidName = inp.files[0].name;
                        self.title = "Load Video";
                        attachVideoThumb(self, d.path);
                    } else { self.title = "Load Video — failed"; }
                } catch (e) { self.title = "Load Video — error"; }
                self.setDirtyCanvas(true, true);
            };
            inp.click();
        });
        // Re-attach the thumbnail after a graph reload (value persisted).
        if (fileW.value) { this._vidName = fileW.value.split(/[\\/]/).pop(); attachVideoThumb(this, fileW.value); }

        // Preview band on top, widgets below it (same layout as Load Image).
        var VID_TOP = 28, VID_H = 120;
        this.widgets_start_y = VID_TOP + VID_H + 8;
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var pad = 8, boxW = this.size[0] - pad * 2;
            ctx.fillStyle = "#1f1f1f"; ctx.strokeStyle = "#000"; ctx.lineWidth = 1;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(pad, VID_TOP, boxW, VID_H, 6); else ctx.rect(pad, VID_TOP, boxW, VID_H);
            ctx.fill(); ctx.stroke();
            var v = this._vid;
            if (v && v.videoWidth) {
                var w = boxW - 4, h = w * (v.videoHeight / v.videoWidth);
                if (h > VID_H - 4) { h = VID_H - 4; w = h * (v.videoWidth / v.videoHeight); }
                var dx = (this.size[0] - w) / 2, dy = VID_TOP + (VID_H - h) / 2;
                try { ctx.drawImage(v, dx, dy, w, h); } catch (e) { /* frame not ready */ }
                // play-triangle badge so it reads as a video, not a still
                ctx.fillStyle = "rgba(0,0,0,0.45)";
                ctx.beginPath(); ctx.arc(this.size[0] / 2, dy + h / 2, 14, 0, Math.PI * 2); ctx.fill();
                ctx.fillStyle = "#fff"; ctx.beginPath();
                ctx.moveTo(this.size[0] / 2 - 5, dy + h / 2 - 7);
                ctx.lineTo(this.size[0] / 2 - 5, dy + h / 2 + 7);
                ctx.lineTo(this.size[0] / 2 + 8, dy + h / 2); ctx.closePath(); ctx.fill();
            } else {
                ctx.fillStyle = "#666"; ctx.font = "11px Arial"; ctx.textAlign = "center";
                ctx.fillText(this._vidName || "no video", this.size[0] / 2, VID_TOP + VID_H / 2 + 4); ctx.textAlign = "left";
            }
        };
        this.size = [220, 232];
    });

    // --- Generate ----------------------------------------------------------
    // Local LLM (on the 4090) rewrites a short idea into a vivid prompt. Wire its TEXT
    // output into a Generate/Extend Video "prompt" input to override that node's prompt box.
    define("Prompts/enhance", "Enhance Prompt", "#556", function () {
        this.addOutput("prompt", LiteGraph.TEXT);
        addMultilineText(this, "idea", "", 4);   // the short idea to expand
        this.addWidget("combo", "style", "cinematic", null, { values: ["cinematic", "photoreal", "anime", "vivid", "none"] });
        // Which local model to use. "(default)" keeps the server's configured/loaded model;
        // any other id makes the server swap to it (the first use of a new id pays the load cost).
        this.addWidget("combo", "model", "(default)", null, { values: [
            "(default)",
            "Qwen/Qwen2.5-7B-Instruct",
            "Qwen/Qwen2.5-3B-Instruct",
            "meta-llama/Llama-3.1-8B-Instruct",
            "mistralai/Mistral-7B-Instruct-v0.3"
        ] });
        this.size = this.computeSize();
        if (this.size[0] < 300) this.size[0] = 300;
    });

    define("Video/generate", "Generate Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional (i2v / Wan)
        this.addInput("audio", LiteGraph.AUDIO);    // optional (audio-to-video)
        this.addInput("prompt", LiteGraph.TEXT);    // optional (Enhance Prompt overrides the widget)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        addMultilineText(this, "prompt", "", 5);
        this.addWidget("combo", "resolution", "540p", null, { values: ["540p", "720p", "1080p"] });
        this.addWidget("combo", "duration", 20, null, { values: [5, 6, 8, 10, 12, 14, 16, 18, 20] });  // LTX-allowed seconds
        this.addWidget("combo", "aspect", "16:9", null, { values: ["16:9", "9:16"] });
        this.size = this.computeSize();          // size to fit every control inside the border
        if (this.size[0] < 320) this.size[0] = 320;
    });

    // Multi-segment generation past the per-run duration cap: each segment is
    // conditioned on the previous segment's last frame, then all are stitched.
    define("Video/extend", "Extend Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional start frame (i2v / Wan)
        this.addInput("prompt", LiteGraph.TEXT);    // optional (Enhance Prompt overrides the widget)
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

    // Chained continuation LOOP: generate a clip, trim the last N seconds off it, take the
    // trimmed clip's final frame, generate the next clip from it — repeated "iterations" times —
    // then stitch them into one continuous video. Like Extend Video, but cutting each segment's
    // tail before conditioning the next. The latest conditioning frame previews in the node.
    define("Video/trim_continue", "Trim & Continue ×N", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional start frame
        this.addInput("prompt", LiteGraph.TEXT);    // optional (Enhance Prompt overrides the widget)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        // Raw idea (widget 1): enhanced into the "enhanced" field on the first run, and
        // overridable by a wired prompt input. The "enhanced" field (widget 2) is what actually
        // drives generation — filled by the enhancer on run, and editable at a pause (used as-is
        // from then on, no re-enhancement).
        addMultilineText(this, "prompt", "a drone shot flying over a coastline", 3);
        addMultilineText(this, "enhanced", "", 4);
        this.addWidget("combo", "resolution", "720p", null, { values: ["540p", "720p", "1080p"] });
        this.addWidget("number", "secPerSeg", 10, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.addWidget("number", "iterations", 3, null, { min: 2, max: 8, step: 10, precision: 0 });   // X
        this.addWidget("number", "trimSeconds", 1, null, { min: 0, max: 30, step: 10, precision: 1 });
        this.addWidget("combo", "aspect", "16:9", null, { values: ["16:9", "9:16"] });
        // When on, the run pauses after each iteration so you can review the frame and edit the
        // enhanced prompt for the next one (or finish early). Index 8 — read as Bool(8) by the executor.
        this.addWidget("toggle", "pauseEachStep", false, null, { on: "yes", off: "no" });
        // Preview band on top: the latest conditioning frame (emitted as "node-image" each
        // iteration) draws here so you can watch the chain progress. Widgets sit below it.
        var IMG_TOP = 28, IMG_H = 110;
        this.widgets_start_y = IMG_TOP + IMG_H + 8;
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var pad = 8, boxW = this.size[0] - pad * 2;
            ctx.fillStyle = "#1f1f1f"; ctx.strokeStyle = "#000"; ctx.lineWidth = 1;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(pad, IMG_TOP, boxW, IMG_H, 6); else ctx.rect(pad, IMG_TOP, boxW, IMG_H);
            ctx.fill(); ctx.stroke();
            if (this._frameImg && this._frameImg.width) {
                var w = boxW - 4, h = w * (this._frameImg.height / this._frameImg.width);
                if (h > IMG_H - 4) { h = IMG_H - 4; w = h * (this._frameImg.width / this._frameImg.height); }
                ctx.drawImage(this._frameImg, (this.size[0] - w) / 2, IMG_TOP + (IMG_H - h) / 2, w, h);
            } else {
                ctx.fillStyle = "#666"; ctx.font = "11px Arial"; ctx.textAlign = "center";
                ctx.fillText("frames appear here on run", this.size[0] / 2, IMG_TOP + IMG_H / 2 + 4); ctx.textAlign = "left";
            }
        };
        this.size = this.computeSize();
        this.size[1] += IMG_H + 12;              // room for the preview band above the widgets
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

    // Trim the last N seconds off a clip and emit the final remaining frame as an
    // image (the frame at duration − seconds). VIDEO in, IMAGE out — wire the output
    // into a Generate/Extend Video "image" input to continue from just before a clip
    // ends, or save it. The produced frame previews inside the node once the run hits it.
    define("Video/trim_tail_frame", "Trim Tail → Frame", "#454", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("image", LiteGraph.IMAGE);
        this.addWidget("number", "trimSeconds", 1, null, { min: 0, max: 600, step: 10, precision: 1 });
        // Preview band on top (filled in by the "node-image" run event), widgets below it.
        var IMG_TOP = 28, IMG_H = 120;
        this.widgets_start_y = IMG_TOP + IMG_H + 8;
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var pad = 8, boxW = this.size[0] - pad * 2;
            ctx.fillStyle = "#1f1f1f"; ctx.strokeStyle = "#000"; ctx.lineWidth = 1;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(pad, IMG_TOP, boxW, IMG_H, 6); else ctx.rect(pad, IMG_TOP, boxW, IMG_H);
            ctx.fill(); ctx.stroke();
            if (this._frameImg && this._frameImg.width) {
                var w = boxW - 4, h = w * (this._frameImg.height / this._frameImg.width);
                if (h > IMG_H - 4) { h = IMG_H - 4; w = h * (this._frameImg.width / this._frameImg.height); }
                ctx.drawImage(this._frameImg, (this.size[0] - w) / 2, IMG_TOP + (IMG_H - h) / 2, w, h);
            } else {
                ctx.fillStyle = "#666"; ctx.font = "11px Arial"; ctx.textAlign = "center";
                ctx.fillText("frame appears here on run", this.size[0] / 2, IMG_TOP + IMG_H / 2 + 4); ctx.textAlign = "left";
            }
        };
        this.size = [220, 200];
    });

    // Lay a sound track over a finished clip: copies the video through and muxes
    // the audio onto it (capped to the video's length). Unlike Generate Video's
    // audio input — which conditions generation — this just dubs an existing clip.
    define("Sound/add_audio", "Add Audio", "#553", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addInput("audio", LiteGraph.AUDIO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.size = [200, 60];
    });

    // --- Groups ------------------------------------------------------------
    // Run a saved layout (its on-disk JSON is the "config file") as a self-contained
    // group of nodes, emitting that sub-pipeline's result as this node's VIDEO output.
    // Pick which layout from the combo; ↻ refreshes the list from the server.
    define("Groups/run_group", "Run Group", "#446", function () {
        var self = this;
        this.addOutput("video", LiteGraph.VIDEO);
        var layoutW = this.addWidget("combo", "layout", "", null, { values: [""] });
        function loadNames() {
            fetch("/Studio/Layouts").then(function (r) { return r.json(); }).then(function (list) {
                var names = (list || []).map(function (l) { return l.name; });
                layoutW.options.values = names.length ? names : [""];
                if ((!layoutW.value || names.indexOf(layoutW.value) < 0) && names.length) layoutW.value = names[0];
                self.setDirtyCanvas(true, true);
            }).catch(function () { /* leave the combo as-is if the list can't be fetched */ });
        }
        loadNames();
        this.addWidget("button", "↻ refresh layouts", null, loadNames);
        this.size = [240, 90];
    });

    // --- Sink --------------------------------------------------------------
    // The result pane: a "filename" box + a download button, with the result clip
    // playing inline (an HTML <video> floated over the black preview area once a
    // run finishes — see the in-node video player below). The video area starts
    // at SAVE_VIDEO_TOP, below the two widgets; that value must match VIDEO_TOP.
    define("Preview Save/save", "Preview Save", "#444", function () {
        var self = this;
        this.addInput("video", LiteGraph.VIDEO);
        this.addWidget("text", "filename", "output");                 // name for the downloaded file
        this.addWidget("button", "⭳ download", null, function () {
            if (!self._videoUrl) return;
            var name = String((self.widgets[0] && self.widgets[0].value) || "output").trim() || "output";
            name = name.replace(/[\\/:*?"<>|]/g, "_");                // strip illegal filename chars
            if (!/\.[a-z0-9]{2,4}$/i.test(name)) name += ".mp4";
            var a = document.createElement("a");
            a.href = self._videoUrl; a.download = name;
            document.body.appendChild(a); a.click(); a.remove();
        });
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed) return;
            var pad = 8, top = SAVE_VIDEO_TOP, w = this.size[0] - pad * 2, h = this.size[1] - top - pad;
            ctx.fillStyle = "#000";
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(pad, top, w, h, 4); else ctx.rect(pad, top, w, h);
            ctx.fill();
            if (!this._hasVideo) {
                ctx.fillStyle = "#555"; ctx.font = "11px Arial"; ctx.textAlign = "center";
                ctx.fillText("preview", this.size[0] / 2, top + h / 2 + 4); ctx.textAlign = "left";
            }
        };
        this.size = [340, 320];
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
        // Minimal default: a start image feeds the retry loop, which saves straight to preview.
        // (No sound nodes — add Sound/Add Audio manually when you want a track.) The loop does
        // its own prompt enhancement, so there's no separate Enhance Prompt node here either.
        var img = LiteGraph.createNode("Image/load_image"); graph.add(img);
        // The loop: generate → trim tail → take frame → generate next, repeated "iterations"
        // times, stitched into one continuous video, then saved.
        var loop = LiteGraph.createNode("Video/trim_continue"); graph.add(loop);
        var save = LiteGraph.createNode("Preview Save/save");   save.size = [420, 360]; graph.add(save);
        // Seed widgets so the default graph passes validation and runs as-is: a raw idea for the
        // loop (it enhances this into its "enhanced" field on the first run). Default to the retry
        // loop: 3 iterations, pausing after each segment so you can review the conditioning frame
        // and adjust/retry the enhanced prompt before continuing.
        var setW = function (node, name, val) {
            var wgt = (node.widgets || []).find(function (x) { return x.name === name; });
            if (wgt) wgt.value = val;
        };
        setW(loop, "prompt", "a serene mountain lake at sunrise, mist rising off the water");
        setW(loop, "iterations", 3);
        setW(loop, "pauseEachStep", true);
        // Lay the graph out in left→right columns using each node's ACTUAL size, so nothing
        // overlaps regardless of how tall the loop node computes.
        (function layout() {
            var X0 = 40, Y0 = 60, COL_GAP = 90;
            img.pos  = [X0, Y0];
            loop.pos = [X0 + img.size[0] + COL_GAP, Y0];
            save.pos = [loop.pos[0] + loop.size[0] + COL_GAP, Y0];
        })();
        img.connect(0, loop, 0);   // Load Image -> Trim & Continue (start frame, slot 0)
        loop.connect(0, save, 0);  // Trim & Continue -> Save (video, slot 0)
        graph.start();
    }
    starterGraph();
    resize();

    // --- In-node video player (Preview Save) -------------------------------
    // LiteGraph nodes are canvas-drawn, so the <video> is a real DOM element
    // floated over the node's preview area, clipped to the canvas (a container
    // with overflow:hidden) and kept aligned with pan/zoom by a rAF loop. One
    // <video> per Preview Save node that has a result.
    var VIDEO_TOP = SAVE_VIDEO_TOP, VIDEO_PAD = 8;
    var videoLayer = document.createElement("div");
    videoLayer.style.cssText = "position:fixed;overflow:hidden;z-index:25;pointer-events:none;";
    document.body.appendChild(videoLayer);
    var videoEls = {};   // nodeId -> <video>
    var videoRAF = null;
    function positionVideoLayer() {
        var r = canvasEl.getBoundingClientRect();
        videoLayer.style.left = r.left + "px"; videoLayer.style.top = r.top + "px";
        videoLayer.style.width = r.width + "px"; videoLayer.style.height = r.height + "px";
    }
    function placeVideo(id) {
        var v = videoEls[id], node = graph.getNodeById(parseInt(id, 10));
        if (!v) return;
        if (!node || node.flags.collapsed) { v.style.display = "none"; return; }
        var s = lgcanvas.ds.scale;
        var c = lgcanvas.ds.convertOffsetToCanvas([node.pos[0] + VIDEO_PAD, node.pos[1] + VIDEO_TOP]);
        v.style.display = "block";
        v.style.left = c[0] + "px"; v.style.top = c[1] + "px";
        v.style.width = ((node.size[0] - VIDEO_PAD * 2) * s) + "px";
        v.style.height = ((node.size[1] - VIDEO_TOP - VIDEO_PAD) * s) + "px";
    }
    function videoLoop() {
        positionVideoLayer();
        var any = false;
        for (var id in videoEls) { any = true; placeVideo(id); }
        videoRAF = any ? requestAnimationFrame(videoLoop) : null;
    }
    function setNodeVideo(nodeId, url) {
        var v = videoEls[nodeId];
        if (!v) {
            v = document.createElement("video");
            v.controls = true; v.preload = "metadata";
            v.style.cssText = "position:absolute;background:#000;border-radius:4px;object-fit:contain;pointer-events:auto;";
            videoLayer.appendChild(v);
            videoEls[nodeId] = v;
        }
        v.src = url;
        var node = graph.getNodeById(parseInt(nodeId, 10));
        if (node) { node._hasVideo = true; node._videoUrl = url; }   // url for the node's download button
        placeVideo(nodeId);
        if (videoRAF == null) videoLoop();
        lgcanvas.setDirty(true, true);
    }
    function clearVideoOverlays() {
        for (var id in videoEls) {
            try { videoEls[id].pause(); } catch (e) { }
            videoEls[id].remove();
            var n = graph.getNodeById(parseInt(id, 10)); if (n) n._hasVideo = false;
            delete videoEls[id];
        }
    }
    // Drop a node's overlay when the node itself is deleted.
    graph.onNodeRemoved = function (node) {
        if (videoEls[node.id]) { videoEls[node.id].remove(); delete videoEls[node.id]; }
    };
    window.addEventListener("resize", positionVideoLayer);

    // --- Run ---------------------------------------------------------------
    var statusEl = document.getElementById("status");
    var runBtn = document.getElementById("run-btn");
    var stopBtn = document.getElementById("stop-btn");
    var runAbort = null;        // AbortController for the in-flight run (Stop button)
    var currentRunId = null;    // server-assigned id of the active run (from the "run" event)
    var resultEl = document.getElementById("result");   // the "Run log" panel
    var resultLog = document.getElementById("result-log");
    document.getElementById("result-close").addEventListener("click", function () { resultEl.classList.remove("show"); });

    function handleEvent(ev) {
        switch (ev.type) {
            case "run":   // server assigned this run an id (used for cancellation)
                currentRunId = ev.id;
                break;
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
            case "node-result":   // a Preview Save node received a clip -> play it in the node
                setNodeVideo(ev.node, "/Studio/Preview?path=" + encodeURIComponent(ev.video));
                break;
            case "node-image": {   // a Trim Tail → Frame node produced a frame -> show it in the node
                var imgNode = graph.getNodeById(parseInt(ev.node, 10));
                if (imgNode) {
                    imgNode._frameImg = new Image();
                    imgNode._frameImg.onload = function () { imgNode.setDirtyCanvas(true, true); };
                    imgNode._frameImg.src = "/Studio/Image?path=" + encodeURIComponent(ev.image);
                }
                break;
            }
            case "node-prompt": {   // executor reports the active/enhanced prompt -> show it in the node's "enhanced" field
                var promptNode = graph.getNodeById(parseInt(ev.node, 10));
                if (promptNode) {
                    var ew = (promptNode.widgets || []).find(function (x) { return x.name === "enhanced"; });
                    if (ew) ew.value = ev.enhanced || "";
                }
                break;
            }
            case "iteration-paused": {   // loop paused — review the frame, edit the enhanced prompt, or finish
                nodeProgress[ev.node] = (ev.iteration / ev.total) * 100;
                statusEl.textContent = "Paused after iteration " + ev.iteration + "/" + ev.total + " — set the next prompt…";
                lgcanvas.setDirty(true, true);
                // The conditioning frame is already showing in the node (via node-image). Defer the
                // blocking prompt one tick so the canvas repaints that frame first; OK = continue
                // with the typed prompt, Cancel = finish now and stitch what's been produced.
                var pausedEv = ev;
                setTimeout(function () {
                    var np = window.prompt(
                        "Paused after iteration " + pausedEv.iteration + " of " + pausedEv.total + ".\n\n" +
                        "Edit the ENHANCED prompt for the NEXT segment and press OK to continue (used as-is),\n" +
                        "or press Cancel to finish now and stitch what's done.", pausedEv.prompt || "");
                    if (np !== null) {   // reflect the edit straight into the node's "enhanced" field
                        var pNode = graph.getNodeById(parseInt(pausedEv.node, 10));
                        if (pNode) {
                            var pew = (pNode.widgets || []).find(function (x) { return x.name === "enhanced"; });
                            if (pew) pew.value = np;
                        }
                    }
                    var body = (np === null)
                        ? { id: currentRunId, prompt: null, stop: true }
                        : { id: currentRunId, prompt: np, stop: false };
                    statusEl.textContent = (np === null) ? "Finishing…" : "Continuing iteration " + (pausedEv.iteration + 1) + "/" + pausedEv.total + "…";
                    fetch("/Studio/Continue", {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify(body)
                    }).catch(function () { statusEl.textContent = "Couldn't reach the server to continue."; });
                }, 60);
                break;
            }
            case "node-error":
                nodeStatus[ev.node] = "error";
                statusEl.textContent = "Error at node " + ev.node + ": " + ev.error;
                break;
            case "log":
                resultLog.textContent += ev.text + "\n";
                resultEl.classList.add("show");
                break;
            case "done":
                // The clip plays in the Preview Save node (via node-result); the panel is just the log now.
                if (ev.ok && ev.finalVideo) statusEl.textContent = "Done.";
                else if (ev.ok) statusEl.textContent = "Finished (no video output).";
                else statusEl.textContent = "Error: " + (ev.error || "failed");
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
        "Video/trim_tail_frame": { slot: "video", label: "Trim Tail → Frame" },
        "Sound/add_audio": { slot: "video", label: "Add Audio" },
    };
    function isLinked(n, slotName) {
        if (!n.inputs) return false;
        for (var i = 0; i < n.inputs.length; i++)
            if (n.inputs[i].name === slotName && n.inputs[i].link != null) return true;
        return false;
    }
    // "Hooked up" = wired into the graph via any input or output. Floating/unused
    // nodes are ignored by validation (they won't affect the run).
    function isHookedUp(n) {
        if (n.inputs) for (var i = 0; i < n.inputs.length; i++) if (n.inputs[i].link != null) return true;
        if (n.outputs) for (var j = 0; j < n.outputs.length; j++) if (n.outputs[j].links && n.outputs[j].links.length) return true;
        return false;
    }

    // Client-side checks before a (possibly minutes-long) run.
    function validateGraph() {
        var issues = [];
        graph._nodes.forEach(function (n) {
            if (!isHookedUp(n)) return;   // only validate nodes wired into the graph

            if (n.type === "Video/generate" || n.type === "Video/extend" || n.type === "Video/trim_continue") {
                var which = n.type === "Video/extend" ? "Extend" : n.type === "Video/trim_continue" ? "Trim & Continue" : "Generate";
                var model = n.widgets[0] && n.widgets[0].value;
                var prompt = n.widgets[1] && n.widgets[1].value;
                // A wired Enhance Prompt supplies the prompt at run time, so the box can be empty then.
                if ((!prompt || !String(prompt).trim()) && !isLinked(n, "prompt"))
                    issues.push({ node: n.id, msg: which + " Video needs a prompt — type one, or wire an Enhance Prompt node into its prompt input." });
                if (model === "wan2.2" && !isLinked(n, "image"))
                    issues.push({ node: n.id, msg: "Wan 2.2 is image-to-video — connect a Load Image to the " + which + " node (or pick BF16/NVFP4)." });
                // Audio-to-video only works on the BF16 backend (NVFP4 is text-only, Wan is image-only).
                if (n.type === "Video/generate" && isLinked(n, "audio") && model !== "bf16-2.3")
                    issues.push({ node: n.id, msg: "Audio-to-video needs the BF16 model — set Generate's model to bf16-2.3, or disconnect the audio input." });
            }
            var req = REQUIRED_INPUT[n.type];
            if (req && !isLinked(n, req.slot))
                issues.push({ node: n.id, msg: req.label + " has no " + req.slot + " input connected — wire a video into it." });
            if (n.type === "Sound/add_audio" && !isLinked(n, "audio"))
                issues.push({ node: n.id, msg: "Add Audio has no audio input — wire a sound (Generate/Load Sound) into it." });
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
            if (n.type === "Video/load_video") {
                var vidW = n.widgets && n.widgets[0];
                if (!vidW || !vidW.value)
                    issues.push({ node: n.id, msg: "Load Video has no file — click 📁 upload to pick a video." });
            }
            if (n.type === "Sound/sound") {
                var sp = n.widgets && n.widgets[0] && n.widgets[0].value;
                if (!sp || !String(sp).trim())
                    issues.push({ node: n.id, msg: "Generate Sound needs a prompt — add one before running." });
            }
            if (n.type === "Groups/run_group") {
                var lay = n.widgets && n.widgets[0] && n.widgets[0].value;
                if (!lay || !String(lay).trim())
                    issues.push({ node: n.id, msg: "Run Group has no layout selected — pick a saved layout (or save one first)." });
            }
            if (n.type === "Prompts/enhance") {
                var idea = n.widgets && n.widgets[0] && n.widgets[0].value;
                if (!idea || !String(idea).trim())
                    issues.push({ node: n.id, msg: "Enhance Prompt needs an idea — type a short description to expand." });
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
        clearVideoOverlays();
        resultLog.textContent = "";
        statusEl.textContent = "Running… (video generation can take minutes)";
        currentRunId = null;
        runAbort = new AbortController();
        runBtn.disabled = true; stopBtn.disabled = false;
        lgcanvas.setDirty(true, true);
        try {
            var resp = await fetch("/Studio/Run", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(graph.serialize()),
                signal: runAbort.signal
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
            // Aborting the fetch (Stop) surfaces as an AbortError — not a real failure.
            statusEl.textContent = e.name === "AbortError" ? "Run cancelled." : "Run failed: " + e.message;
        } finally {
            runBtn.disabled = false; stopBtn.disabled = true;
            runAbort = null; currentRunId = null;
        }
    });

    // Stop: cancel server-side via the run id (works even if the stream stalls), and
    // abort the fetch so the request token trips too. Either path unwinds the run.
    stopBtn.addEventListener("click", async function () {
        if (stopBtn.disabled) return;
        stopBtn.disabled = true;
        statusEl.textContent = "Cancelling…";
        if (currentRunId) {
            try {
                await fetch("/Admin/CancelRun", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ id: currentRunId })
                });
            } catch (e) { /* fall back to the fetch abort below */ }
        }
        if (runAbort) { try { runAbort.abort(); } catch (e) { /* ignore */ } }
    });
    document.getElementById("reset-btn").addEventListener("click", function () {
        nodeStatus = {}; nodeProgress = {}; clearVideoOverlays(); starterGraph(); resize(); statusEl.textContent = "";
    });

    // --- Save / load graphs ------------------------------------------------
    // After a graph.configure(), Load Image nodes need their thumbnail re-fetched:
    // the build() constructor runs before widget values are applied, so the
    // in-constructor re-attach sees an empty path. Do it here once values exist.
    function reattachThumbnails() {
        graph._nodes.forEach(function (n) {
            var fileW = n.widgets && n.widgets[0];
            if (!fileW || !fileW.value) return;
            if (n.type === "Image/load_image") {
                n._img = new Image();
                n._img.onload = function () { n.setDirtyCanvas(true, true); };
                n._img.src = "/Studio/Image?path=" + encodeURIComponent(fileW.value);
            } else if (n.type === "Video/load_video") {
                n._vidName = String(fileW.value).split(/[\\/]/).pop();
                attachVideoThumb(n, fileW.value);
            }
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

    // --- Named layouts (server-persisted, selectable from the dropdown) -----
    // Save the current graph under a name and reload it later from the dropdown.
    // Unlike Save/Load (which round-trip a JSON file on the user's disk), these
    // live on the server via /Studio/{Layouts,SaveLayout,Layout,DeleteLayout}.
    var layoutSelect = document.getElementById("layout-select");
    var layoutSaveBtn = document.getElementById("layout-save-btn");
    var layoutDeleteBtn = document.getElementById("layout-delete-btn");
    var savedLayouts = [];   // last-fetched [{ name, savedUtc }] — used for the overwrite check

    // Mirror of LayoutStore.Slug on the server: lowercase, keep [a-z0-9], map
    // space/dash/underscore to '-', drop the rest, trim '-'. Two names with the
    // same slug map to the same file, so saving one overwrites the other — this
    // lets the Save button warn before that happens.
    function layoutSlug(name) {
        var out = "", s = (name || "").trim().toLowerCase();
        for (var i = 0; i < s.length; i++) {
            var ch = s[i];
            if (ch >= "a" && ch <= "z" || ch >= "0" && ch <= "9") out += ch;
            else if (ch === " " || ch === "-" || ch === "_") out += "-";
        }
        return out.replace(/^-+|-+$/g, "");
    }
    // The display name of an existing layout that the given name would overwrite, or null.
    function existingLayoutFor(name) {
        var slug = layoutSlug(name);
        if (!slug) return null;
        for (var i = 0; i < savedLayouts.length; i++)
            if (layoutSlug(savedLayouts[i].name) === slug) return savedLayouts[i].name;
        return null;
    }

    // "2026-06-23T14:12:…Z" -> " (saved Jun 23, 2:14 PM)" in the viewer's locale.
    // Today's saves show the time; older ones just the date. Blank if unparseable.
    function savedSuffix(utc) {
        if (!utc) return "";
        var d = new Date(utc);
        if (isNaN(d)) return "";
        var now = new Date();
        var sameDay = d.toDateString() === now.toDateString();
        var when = sameDay
            ? d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
            : d.toLocaleDateString([], { month: "short", day: "numeric" });
        return "  (saved " + when + ")";
    }

    // Refresh the dropdown from the server, keeping the placeholder option and
    // optionally re-selecting a name (e.g. the one just saved).
    async function refreshLayouts(selectName) {
        try {
            var list = await (await fetch("/Studio/Layouts")).json();
            savedLayouts = list;               // cache for the overwrite check
            layoutSelect.options.length = 1;   // keep the "— saved layouts —" placeholder
            list.forEach(function (l) {
                var o = document.createElement("option");
                o.value = l.name;                              // value stays the bare name (used to load)
                o.textContent = l.name + savedSuffix(l.savedUtc);   // label adds the save time
                layoutSelect.appendChild(o);
            });
            layoutSelect.value = selectName || "";
        } catch (e) { /* leave the dropdown as-is if the list can't be fetched */ }
    }

    function applyLoadedGraph(data, label) {
        nodeStatus = {}; nodeProgress = {}; clearVideoOverlays();
        graph.configure(data);
        graph.start();
        reattachThumbnails();
        resize();
        statusEl.textContent = "Loaded " + label + " (" + graph._nodes.length + " nodes)";
    }

    async function loadLayout(name) {
        if (!name) return;
        statusEl.textContent = "Loading layout “" + name + "”…";
        try {
            var d = await (await fetch("/Studio/Layout?name=" + encodeURIComponent(name))).json();
            if (!d.ok || !d.graph) { statusEl.textContent = "Layout “" + name + "” not found."; return; }
            applyLoadedGraph(d.graph, "layout “" + name + "”");
        } catch (e) {
            statusEl.textContent = "Load failed: " + e.message;
        }
    }
    layoutSelect.addEventListener("change", function () { loadLayout(layoutSelect.value); });

    layoutSaveBtn.addEventListener("click", async function () {
        var name = (window.prompt("Save layout as:", layoutSelect.value || "") || "").trim();
        if (!name) return;
        var clash = existingLayoutFor(name);   // warn before clobbering an existing layout
        if (clash && !window.confirm("A layout named “" + clash + "” already exists. Overwrite it?")) return;
        try {
            var resp = await fetch("/Studio/SaveLayout", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: name, graph: graph.serialize() })
            });
            var d = await resp.json();
            if (!d.ok) { statusEl.textContent = "Save failed: " + (d.error || "unknown error"); return; }
            await refreshLayouts(d.name);
            statusEl.textContent = "Saved layout “" + d.name + "”";
        } catch (e) {
            statusEl.textContent = "Save failed: " + e.message;
        }
    });

    layoutDeleteBtn.addEventListener("click", async function () {
        var name = layoutSelect.value;
        if (!name) { statusEl.textContent = "Pick a layout to delete first."; return; }
        if (!window.confirm("Delete layout “" + name + "”?")) return;
        try {
            var resp = await fetch("/Studio/DeleteLayout", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: name })
            });
            var d = await resp.json();
            if (d.ok) { await refreshLayouts(); statusEl.textContent = "Deleted layout “" + name + "”"; }
            else { statusEl.textContent = "Delete failed."; }
        } catch (e) { statusEl.textContent = "Delete failed: " + e.message; }
    });

    refreshLayouts();   // populate the dropdown on load
})();
