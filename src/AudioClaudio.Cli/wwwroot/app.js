// Live notation view: renders whatever score LiveNotationServer last published, and
// re-renders on every SSE update. OSMD is a whole-document renderer (no incremental API in
// the vendored build), so each event is a complete MusicXML document, base64-encoded to
// survive SSE's line-oriented framing -- see the live-notation design doc's SSE protocol.
const osmd = new opensheetmusicdisplay.OpenSheetMusicDisplay("osmd-container");
const source = new EventSource("/events");

const statusBadge = document.getElementById("status-badge");
const deviceNameEl = document.getElementById("device-name");
const vuMeterFill = document.getElementById("vu-meter-fill");
const vuMeter = document.getElementById("vu-meter");
const takeOutput = document.getElementById("take-output");
const recreationPlayer = document.getElementById("recreation-player");
const sheetMusicViewport = document.getElementById("sheet-music-viewport");

// The finished-take route (S5.11): GET /files/<name> serves these five whitelisted files
// from the run's out-dir. Downloads point straight at the route; the player streams from it.
const TAKE_FILES = {
  "download-raw": "raw.mid",
  "download-score-mid": "score.mid",
  "download-score-musicxml": "score.musicxml",
  "download-recreation": "recreation.wav",
};

function setStatus(state, label) {
  statusBadge.className = "status status-" + state;
  statusBadge.textContent = label;
}

source.addEventListener("score", (event) => {
  const xml = atob(event.data);
  osmd.load(xml).then(() => {
    osmd.render();
    // Keep the newest system in view as notes appear: scroll the viewport to the bottom after
    // every render, so the user watches the live edge rather than the top of the growing score.
    sheetMusicViewport.scrollTop = sheetMusicViewport.scrollHeight;
  });
});

source.addEventListener("clear", () => {
  osmd.clear();
  sheetMusicViewport.scrollTop = 0; // reset the viewport for the new take's first system
  hideTakeOutput();
});

// The "level" SSE event (published per mic frame): payload is "<rms F4>|<device name>",
// e.g. "0.1234|Built-in Microphone". Drives the VU meter fill and the device-name display.
source.addEventListener("level", (event) => {
  const separatorIndex = event.data.indexOf("|");
  const rms = parseFloat(event.data.slice(0, separatorIndex));
  const device = event.data.slice(separatorIndex + 1);
  updateMeter(rms, device);
});

function updateMeter(rms, device) {
  const fraction = Math.max(0, Math.min(1, rms / 0.35));
  vuMeterFill.style.width = (fraction * 100).toFixed(0) + "%";
  vuMeter.setAttribute("aria-valuenow", fraction.toFixed(2));
  deviceNameEl.textContent = device || "Unknown microphone";
  setStatus("recording", "● Recording");
}

function hideTakeOutput() {
  takeOutput.hidden = true;
  recreationPlayer.removeAttribute("src");
}

async function fileExists(name) {
  try {
    const response = await fetch("/files/" + name);
    return response.ok;
  } catch {
    return false;
  }
}

// After Stop, the take's files land on disk asynchronously (rendering/writing takes a moment).
// Poll /files/score.musicxml until it appears (or we give up), then wire the player + downloads
// and reveal #take-output.
async function revealTakeOutputWhenReady(attempts = 20, delayMs = 300) {
  for (let i = 0; i < attempts; i++) {
    if (await fileExists("score.musicxml")) break;
    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }

  for (const [id, fileName] of Object.entries(TAKE_FILES)) {
    document.getElementById(id).href = "/files/" + fileName;
  }
  if (await fileExists("recreation.wav")) {
    recreationPlayer.src = "/files/recreation.wav";
  }
  takeOutput.hidden = false;
  setStatus("idle", "Idle");
}

const startButton = document.getElementById("start-recording");
const stopButton = document.getElementById("stop-recording");

startButton.addEventListener("click", async () => {
  startButton.disabled = true;
  stopButton.disabled = false;
  hideTakeOutput();
  setStatus("recording", "● Recording");
  const params = new URLSearchParams({
    record: document.getElementById("opt-record").checked,
    skipSilence: document.getElementById("opt-skip-silence").checked,
    noteNames: document.getElementById("opt-note-names").checked,
    title: document.getElementById("score-title").value,
  });
  await fetch("/record/start?" + params.toString(), { method: "POST" });
});

stopButton.addEventListener("click", async () => {
  stopButton.disabled = true;
  setStatus("finishing", "Saving take…");
  await fetch("/record/stop", { method: "POST" });
  startButton.disabled = false;
  await revealTakeOutputWhenReady();
});
