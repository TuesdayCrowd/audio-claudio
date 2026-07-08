// Live notation view: renders whatever score LiveNotationServer last published, and
// re-renders on every SSE update. OSMD is a whole-document renderer (no incremental API in
// the vendored build), so each event is a complete MusicXML document, base64-encoded to
// survive SSE's line-oriented framing -- see the live-notation design doc's SSE protocol.
const osmd = new opensheetmusicdisplay.OpenSheetMusicDisplay("osmd-container");
const source = new EventSource("/events");

source.addEventListener("score", (event) => {
  const xml = atob(event.data);
  osmd.load(xml).then(() => osmd.render());
});

source.addEventListener("clear", () => {
  osmd.clear();
});

const startButton = document.getElementById("start-recording");
const stopButton = document.getElementById("stop-recording");

startButton.addEventListener("click", async () => {
  startButton.disabled = true;
  stopButton.disabled = false;
  await fetch("/record/start", { method: "POST" });
});

stopButton.addEventListener("click", async () => {
  stopButton.disabled = true;
  startButton.disabled = false;
  await fetch("/record/stop", { method: "POST" });
});
