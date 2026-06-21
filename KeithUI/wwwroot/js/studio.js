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

    // --- Sources -----------------------------------------------------------
    define("keithui/load_image", "Load Image", "#355", function () {
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
                        self.title = "Load Image — " + d.name;
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
        this.onDrawBackground = function (ctx) {
            if (this.flags.collapsed || !this._img || !this._img.width) return;
            var pad = 8, top = 74; // below the two widgets
            var w = this.size[0] - pad * 2;
            var h = w * (this._img.height / this._img.width);
            var maxH = this.size[1] - top - pad;
            if (h > maxH) { h = maxH; w = h * (this._img.width / this._img.height); }
            ctx.drawImage(this._img, (this.size[0] - w) / 2, top, w, h);
        };
        this.size = [220, 210];
    });

    define("keithui/sound", "Generate Sound", "#553", function () {
        this.addOutput("audio", LiteGraph.AUDIO);
        this.addWidget("text", "prompt", "distant thunder");
        this.addWidget("number", "seconds", 5, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.size = [240, 80];
    });

    // --- Generate ----------------------------------------------------------
    define("keithui/generate", "Generate Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional (i2v / Wan)
        this.addInput("audio", LiteGraph.AUDIO);    // optional (audio-to-video)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        this.addWidget("text", "prompt", "a cat playing with a ball of yarn");
        this.addWidget("combo", "resolution", "540p", null, { values: ["540p", "720p", "1080p"] });
        this.addWidget("number", "duration", 5, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.addWidget("combo", "aspect", "16:9", null, { values: ["16:9", "9:16"] });
        this.size = [280, 150];
    });

    // --- Post-processing ---------------------------------------------------
    define("keithui/upscale", "Upscale", "#534", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "engine", "ai", null, { values: ["ai", "maxine"] });
        this.addWidget("combo", "targetHeight", "1080", null, { values: ["720", "1080", "1440", "2160"] });
        this.addWidget("combo", "maxineFactor", "2", null, { values: ["2", "3", "4"] });
        this.size = [240, 110];
    });

    define("keithui/speed", "Speed Up", "#454", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "factor", "2", null, { values: ["1.5", "2", "3", "4"] });
        this.size = [200, 60];
    });

    // --- Sink --------------------------------------------------------------
    define("keithui/save", "Save / Preview", "#444", function () {
        this.addInput("video", LiteGraph.VIDEO);
        this.size = [200, 40];
    });

    // --- Canvas ------------------------------------------------------------
    var canvasEl = document.getElementById("graph");
    var graph = new LGraph();
    var lgcanvas = new LGraphCanvas(canvasEl, graph);
    window.lgraph = graph; window.lgcanvas = lgcanvas;   // debug/automation hook

    // Per-node run status -> a colored outline (running / done / error), ComfyUI-style.
    var nodeStatus = {};
    lgcanvas.onDrawForeground = function (ctx) {
        var th = LiteGraph.NODE_TITLE_HEIGHT;
        for (var id in nodeStatus) {
            var node = graph.getNodeById(parseInt(id, 10));
            if (!node) continue;
            var st = nodeStatus[id];
            ctx.lineWidth = 3;
            ctx.strokeStyle = st === "running" ? "#ffd24a" : st === "done" ? "#4caf76" : "#e05555";
            var x = node.pos[0] - 3, y = node.pos[1] - th - 3, w = node.size[0] + 6, h = node.size[1] + th + 6;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(x, y, w, h, 8); else ctx.rect(x, y, w, h);
            ctx.stroke();
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
        var gen = LiteGraph.createNode("keithui/generate"); gen.pos = [120, 120]; graph.add(gen);
        var up = LiteGraph.createNode("keithui/upscale");  up.pos = [460, 120]; graph.add(up);
        var save = LiteGraph.createNode("keithui/save");    save.pos = [740, 140]; graph.add(save);
        gen.connect(0, up, 0);
        up.connect(0, save, 0);
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
                statusEl.textContent = "Running node " + ev.node + "…";
                break;
            case "node-progress":
                statusEl.textContent = "Generating… " + (ev.pct || 0) + "%";
                break;
            case "node-done":
                nodeStatus[ev.node] = "done";
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

    runBtn.addEventListener("click", async function () {
        nodeStatus = {};
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
        nodeStatus = {}; starterGraph(); resize(); statusEl.textContent = "";
    });
})();
