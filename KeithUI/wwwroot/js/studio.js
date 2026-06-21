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
        function Node() { build.call(this); this.color = color; this.bgcolor = "#2a2a2a"; }
        Node.title = title;
        LiteGraph.registerNodeType(type, Node);
    }

    // --- Sources -----------------------------------------------------------
    define("keithui/load_image", "Load Image", "#355", function () {
        this.addOutput("image", LiteGraph.IMAGE);
        this.addWidget("text", "file", "", null, { property: "file" });
        this.size = [220, 60];
    });

    define("keithui/sound", "Generate Sound", "#553", function () {
        this.addOutput("audio", LiteGraph.AUDIO);
        this.addWidget("text", "prompt", "distant thunder", null, { property: "prompt" });
        this.addWidget("number", "seconds", 5, null, { min: 1, max: 30, step: 10, precision: 0 });
        this.size = [240, 80];
    });

    // --- Generate ----------------------------------------------------------
    define("keithui/generate", "Generate Video", "#345", function () {
        this.addInput("image", LiteGraph.IMAGE);   // optional (i2v / Wan)
        this.addInput("audio", LiteGraph.AUDIO);    // optional (audio-to-video)
        this.addOutput("video", LiteGraph.VIDEO);
        this.addWidget("combo", "model", "bf16-2.3", null, { values: ["bf16-2.3", "nvfp4-2.3", "wan2.2"] });
        this.addWidget("text", "prompt", "a cat playing with a ball of yarn", null, { property: "prompt" });
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

    runBtn.addEventListener("click", async function () {
        statusEl.textContent = "Running… (video generation can take minutes)";
        runBtn.disabled = true;
        try {
            var r = await fetch("/Studio/Run", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(graph.serialize())
            });
            var d = await r.json();
            if (d.log) { resultLog.textContent = d.log.join("\n"); }
            if (d.ok && d.videoUrl) {
                resultVideo.src = d.videoUrl;
                resultDl.href = d.videoUrl;
                resultEl.classList.add("show");
                statusEl.textContent = "Done — " + (d.fileName || "");
            } else if (d.ok) {
                resultEl.classList.add("show");
                statusEl.textContent = "Finished (no video output).";
            } else {
                resultEl.classList.add("show");
                statusEl.textContent = "Error: " + (d.error || ("HTTP " + r.status));
            }
        } catch (e) {
            statusEl.textContent = "Run failed: " + e.message;
        } finally {
            runBtn.disabled = false;
        }
    });
    document.getElementById("reset-btn").addEventListener("click", function () {
        starterGraph(); resize(); statusEl.textContent = "";
    });
})();
