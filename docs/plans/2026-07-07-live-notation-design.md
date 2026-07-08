# Live Incremental Notation via OpenSheetMusicDisplay — Design

`claudio listen --view` opens the user's browser to a localhost page that
renders the growing score with OpenSheetMusicDisplay (OSMD) as the microphone
is played, note by note — approximate while live, exact the instant the
session stops.

This document is the design record; `2026-07-07-live-notation-plan.md` is the
TDD implementation plan that executes it. Nothing here is re-opened for
debate — Cornelius has made the calls recorded below (§1 rule 2); this is the
write-up.

---

## Where this fits

CLAUDE.md §8 (Phase 2) lists, as item 3, "Live incremental notation — VexFlow
in a webview or Manufaktura, rendering the score as it grows." This design
supersedes that sketch with a concrete, decided architecture: OpenSheetMusicDisplay,
in a plain browser tab, not a webview; not VexFlow; not Manufaktura. It is
Phase-2 work — v0.1.0 (Steps 0–12) is complete, tagged, and green on `main`
(313 tests) — layered on top of the shipped MVP without reopening any of it.

Per §1 rule 3 ("one step at a time... no speculative scaffolding for future
steps"), the analogous discipline applies here: this plan is worked
task-by-task, each *Verify* green and committed before the next begins.

Explicitly **not** covered by this feature (see *Out of scope* below): the
other Phase-2 items (polyphony, tempo estimation, pYIN, treble/bass split) and
a MIDI-keyboard input path considered and rejected during design.

---

## Architecture

```
                              microphone
                                  │
                                  ▼
                       PortAudioAudioSource (Infra)
                                  │  Frame (pull, ONE consumer — a live
                                  │  channel drains exactly once)
                                  ▼
                    TranscriptionPipeline.StreamNotes (App)
                                  │  IEnumerable<NoteEvent>, lazy, causal
                                  ▼
                    LiveTranscriptionSession.Run (App — UNCHANGED)
                                  │  onNote: NoteEvent  (the session's one
                                  │  existing callback now fans out to two
                                  │  listeners inside ListenCommand)
                    ┌─────────────┴──────────────┐
                    ▼                             ▼
         ListenCommand._print              ListenCommand._onLiveNote
         (existing: console line)          (NEW, optional, default null)
                                                   │
                                                   ▼
                                     LiveScoreProjector.Add(note)  (App, NEW)
                                       events.Add(note);
                                       return Quantizer.Quantize(events, grid)   [Domain, unchanged]
                                                   │  Score (growing, approximate)
                                                   ▼
                                     LiveNotationServer.PublishScore(score)  (Infra, NEW)
                                       MusicXmlScoreWriter.WriteToString(score) [Infra, unchanged]
                                       → base64 → SSE broadcast to every /events client
                                                   │  HTTP, localhost only
                                                   ▼
                                          browser tab: new EventSource("/events")
                                            atob(event.data) → osmd.load(xml).then(() => osmd.render())

  ── on Ctrl+C ──────────────────────────────────────────────────────────────
  ListenCommand.Run already computes the ACCURATE batch result.Score
  (TranscriptionPipeline.Transcribe over the buffered session audio — the
  same computation that already produces raw.mid/score.mid/score.musicxml,
  R10.3). A second new optional hook, _onFinalScore, publishes THAT score one
  more time — the browser's sheet corrects itself from "approximate, growing"
  to "exact, final" the same moment the MIDI/MusicXML trio hits disk.
```

### Layer mapping

| Layer | Change | Type(s) | I/O? |
|---|---|---|---|
| Domain | none | — | — |
| Application | new | `AudioClaudio.Application.UseCases.LiveScoreProjector` | none — pure |
| Infrastructure | new | `AudioClaudio.Infrastructure.LiveView.LiveNotationServer`, `.FreeTcpPort` | HTTP listener, file reads |
| Cli | changed (additive) | `Commands/ListenCommand.cs` — two new **optional** constructor parameters | none added here |
| Cli | changed | `Program.cs` — `--view` flag, composition, browser-open | process start, console |
| Cli (assets) | new | `wwwroot/index.html`, `wwwroot/app.js`, `wwwroot/osmd/opensheetmusicdisplay.min.js` (+ license) | static files, no C# |

**Dependency rule.** Domain is untouched — still BCL-only. `LiveScoreProjector`
lives in Application and imports only `AudioClaudio.Domain` (`NoteEvent`,
`Score`, `Quantizer`, `QuantizationGrid`); it does not reference Infrastructure,
does not touch HTTP, does not read a clock. `LiveNotationServer` lives in
Infrastructure, which is permitted to depend on Application + Domain and does
so (`Score` in, `MusicXmlScoreWriter` — a sibling Infrastructure type in the
same project — for serialization); nothing here points inward. The Cli
composition root remains the *only* place a `LiveNotationServer` is
constructed and the only place `LiveScoreProjector` is wired to
`ListenCommand`'s new hook — unchanged from §7's existing rule ("The CLI is
the only place adapters are constructed and wired to ports").

### Note on the projector's shape — a deliberate divergence from the initial sketch

The feature was originally sketched with the signature
`IEnumerable<Score> LiveScores(IAudioSource source)`. That shape does not
compose with the real, shipped Step 10 architecture: `LiveTranscriptionSession.Run`
already performs the *one* allowed enumeration of the live `IAudioSource`'s
note feed — its own doc comment states "The live device's Frames is
enumerated EXACTLY ONCE (a live channel drains only once)," and
`PortAudioAudioSource` backs `Frames` with a bounded `Channel<Frame>`, which
supports exactly one drain. A second, independent call to
`pipeline.StreamNotes(micSource)` would race the existing one over the same
channel and silently split frames between two consumers — a real bug, not a
style nit.

So `LiveScoreProjector` is instead a small stateful accumulator: a
push-shaped `Add(NoteEvent) : Score` primitive, fed from *inside*
`LiveTranscriptionSession`'s existing single `onNote` callback (via a new
optional hook on `ListenCommand` — see below) — plus a pull-shaped
`LiveScores(IEnumerable<NoteEvent> notes)` convenience wrapper over that same
primitive, for exactly the "fake note stream in, assert the i-th Score"
unit test this document's Testing section describes. This keeps the
Application layer pure and I/O-free and requires **zero** changes to
`TranscriptionPipeline`, `LiveTranscriptionSession`, or the already-proven
single-enumeration contract those types establish.

### `ListenCommand`'s two new hooks

`ListenCommand` gains two new **optional** constructor parameters, both
defaulting to `null`:

```csharp
Action<NoteEvent>? onLiveNote = null,   // fires once per live note, alongside the existing console print
Action<Score>? onFinalScore = null      // fires once, with the ACCURATE batch Score, after the session ends
```

This is the minimal, additive extension point: `ListenCommand.Run` already
builds the one `onNote` lambda it hands to `LiveTranscriptionSession.Run`
(`n => _print(FormatNote(n))`); it now also invokes `_onLiveNote?.Invoke(n)`
there, and `_onFinalScore?.Invoke(result.Score)` once, right after
`_session.Run` returns. Every existing call site (`Program.cs`'s prior
`listen` wiring, and both existing `ListenCommandTests` cases) compiles and
behaves identically unchanged, because the new parameters are optional and
trail the existing ones. `ListenCommand` itself stays decoupled from
`LiveNotationServer`/`LiveScoreProjector` — it only knows about `Action<T>`
delegates, exactly the way it already only knows `Action<string> print`, not
`Console`.

---

## The SSE protocol

**Endpoints**

| Method + path | Serves | Content-Type |
|---|---|---|
| `GET /` | `wwwroot/index.html` | `text/html; charset=utf-8` |
| `GET /app.js` | `wwwroot/app.js` | `application/javascript; charset=utf-8` |
| `GET /osmd/opensheetmusicdisplay.min.js` | the vendored OSMD bundle | `application/javascript; charset=utf-8` |
| `GET /events` | the SSE stream | `text/event-stream` |
| any other path | 404 | — |

`/events` is `Cache-Control: no-cache`, chunked, and held open indefinitely —
one HTTP response per connected browser tab, never closed by the server
except on shutdown or a dead client.

**Event shape.** Every update — live or final — is one complete,
self-contained event:

```
event: score
data: <base64 of UTF-8 MusicXML>

```

(a single `data:` line, blank-line terminated, standard SSE wire format).
The payload is the *entire* `Score`, re-serialized from scratch each time
(see "Full re-render per update" below), base64-encoded because MusicXML is
inherently multi-line text and SSE's `data:` field is line-oriented — a bare
newline inside it would be parsed as a second, malformed field. The browser
does one `atob()` call before handing the string to OSMD.

**Reconnection / late-join.** The browser's native `EventSource` auto-reconnects
on any network hiccup with no application code required. Because every event
already carries the *complete* current score rather than a delta, there is no
need for SSE's `id:` / `Last-Event-ID` resumption machinery — a reconnect (or
a brand-new tab opened mid-session) just needs the server's current state,
and `LiveNotationServer` hands it over immediately: the moment a `/events`
request arrives, the server enqueues the most-recently-published payload to
that connection before anything else. A late joiner sees the piece "as of
now," never a blank page waiting for the next note.

There is no heartbeat/keep-alive ping in this design — a genuinely dead
connection is detected on its *next write attempt* (the usual limitation of
raw HTTP without a ping), which is an acceptable gap for a short, single-user
`listen` session; a future revision could add one if long-running sessions
ever make it matter (see *Out of scope*).

**Multiple clients.** `LiveNotationServer` tracks every open `/events`
connection and broadcasts each `PublishScore` call to all of them. A
connection whose write fails (tab closed, network dropped) is dropped from
the broadcast set on that failure, not proactively polled for.

---

## OSMD integration

`opensheetmusicdisplay.min.js` — OSMD's UMD build — is committed verbatim at
`src/AudioClaudio.Cli/wwwroot/osmd/opensheetmusicdisplay.min.js`, the same way
the GeneralUser GS SoundFont is committed under `fixtures/soundfont/`
(Step 8): fetched once, verified, checked in. No build step, no bundler, no
npm/Node toolchain enters this repository at all — the vendored file is a
binary-ish static asset like any other fixture. `AudioClaudio.Cli.csproj`
copies `wwwroot/**` to the build output directory so the assets ship next to
the compiled executable, the same way a published app would.

`app.js` is minimal and is the entire client-side logic this feature needs:

```js
const osmd = new opensheetmusicdisplay.OpenSheetMusicDisplay("osmd-container");
const source = new EventSource("/events");
source.addEventListener("score", (event) => {
  const xml = atob(event.data);
  osmd.load(xml).then(() => osmd.render());
});
```

`OpenSheetMusicDisplay` is a whole-score renderer: `.load(xmlString)` parses
a complete MusicXML document, `.render()` draws it into the named container
element, replacing whatever was there before. There is no incremental/append
API in the vendored bundle — which is exactly why the protocol above sends a
complete document each time rather than a diff (see "Full re-render per
update").

**Offline.** Everything the browser needs — the HTML, the script, the OSMD
bundle — is served by `LiveNotationServer` from the local filesystem. No
CDN, no external `<script src="https://...">`, no network access beyond
`localhost`. The feature works with no internet connection, the same
offline guarantee the committed SoundFont gives `render`/`play`.

---

## Design decisions

Each decision below was made by Cornelius during design; this records the
call and the rationale, per §1 rule 2.

1. **Input = microphone**, reusing the existing `listen` transcription path
   (`TranscriptionPipeline.StreamNotes` over `PortAudioAudioSource`).
   ***Rejected: MIDI input.*** A connected MIDI keyboard would skip pitch/onset
   detection entirely and hand back perfect, instant note data — a materially
   simpler, different feature ("view a MIDI stream as notation," not "watch
   the transcriber work"). Recorded as a future alternative input path (*Out
   of scope*), not built now.

2. **Display = a browser tab at `localhost`**, not an embedded webview.
   ***Rejected: an embedded webview*** (e.g. WebView2 or a cross-platform
   equivalent). A webview pulls in a platform-specific native dependency and
   complicates the "macOS-primary, Windows-secondary" cross-platform story
   the pinned stack (§3) already has to manage for PortAudio. A plain browser
   tab needs nothing beyond an HTTP listener the BCL already provides, behaves
   identically on every OS Cornelius uses, and is inspectable with the
   browser's own devtools for free while building this feature.

3. **Web server = `System.Net.HttpListener` + hand-rolled SSE, BCL only.**
   ***Rejected: ASP.NET Core*** (Kestrel / Minimal APIs). ASP.NET Core is the
   conventional choice for a "real" web app, but it pulls in the whole
   hosting/DI/middleware framework to do what is, in substance, "serve four
   static files and stream one kind of event." This repository's established
   character is hand-rolled-when-small: the WAV reader hand-rolls RIFF
   parsing, the FFT is a hand-rolled radix-2 implementation, MusicXML is a
   hand-rolled `StringBuilder` serializer — each chosen over an available
   library specifically to keep the dependency graph minimal and the
   mechanism legible (§3; §6 Steps 3 and 11). `HttpListener` plus a dozen
   lines of SSE framing is the same call: legible, dependency-free, and small
   enough that "hand-rolled" is a feature, not a liability. `HttpListener`
   has been a fully managed, cross-platform BCL implementation since .NET 5,
   and needs no `netsh`/admin ceremony when bound to a `localhost`-only
   prefix (the wildcard forms `http://+:port/` / `http://*:port/` are the
   ones that need a URL-ACL reservation or elevation on Windows; this design
   never uses them).

4. **SSE, not WebSocket.** ***Rejected: WebSocket.*** Data flows one way
   only — server to browser; the browser never needs to send anything back.
   SSE is a plain, long-lived HTTP response (`text/event-stream`) with a
   trivial wire format, needs no upgrade handshake, and `EventSource` gives
   client-side auto-reconnect for free. `HttpListener`'s WebSocket support
   requires its own (more involved) upgrade/frame-parsing API for a
   capability — bidirectional messaging — this feature never uses. Wrong
   tool for a one-way stream.

5. **Payload = full MusicXML per update, base64-encoded into a single SSE
   `data:` line.** Covered under *The SSE protocol* above. **Debounce/coalesce
   rapid notes:** each `/events` connection has its own capacity-1,
   drop-oldest outbox (a `System.Threading.Channels.Channel<string>`) rather
   than an unbounded per-note queue — the same non-blocking,
   drop-not-silently-swallow idiom `CaptureFrameStream` already uses for live
   audio frames (Step 10; see `DECISIONS.md`'s discussion of
   `BoundedChannelFullMode`). A burst of fast notes collapses to the
   freshest score by the time a slow client's writer catches up, and
   `PublishScore` never blocks the note-processing thread waiting on a
   browser's TCP window — reusing an idiom this codebase already decided on,
   rather than inventing a new one.

6. **Full re-render per update, not an incremental diff.** OSMD's public API
   (`.load()` + `.render()`) is a whole-document renderer; the vendored
   bundle has no supported "append these notes" call. A `listen` session's
   score stays small (a handful of bars for the length of a session), so
   re-parsing and redrawing the whole thing on every note is cheap — well
   under perceptible lag — and categorically simpler and more robust than
   hand-tracking a diff against a renderer that was never designed to accept
   one. An incremental cursor-based renderer is recorded as future work (see
   *Out of scope*) if a session ever grows long enough for this to matter.

7. **Live view is approximate, then snaps to the accurate score on stop.**
   The live feed (`StreamNotes`) reports each note at its onset with a
   *provisional* duration — by Step 10's own design (`TranscriptionPipeline.StreamNotes`'s
   remarks: "a live note is reported... with a PROVISIONAL duration...
   `Transcribe`... alone owns duration refinement"). The live sheet therefore
   shows onsets/pitches as detected and durations that are placeholders,
   exactly the way the live `listen` console output already behaves today.
   The instant the mic stops, `ListenCommand` already computes the accurate
   batch `Score` (via `Transcribe` over the buffered session audio) to write
   `score.mid`/`score.musicxml`; this feature publishes that *same* `Score`
   to the browser one more time, so the sheet corrects itself in place. This
   is an honest, visible instance of the project's existing "the live feed is
   a low-latency preview; the accurate output comes from the batch pass on
   stop" contract (Step 10, `DECISIONS.md`) — not a new promise, just a new
   place that contract is shown.

8. **Port selection: probe a free ephemeral port, reuse it for the listener.**
   `LiveNotationServer` defaults to port 0 ("pick one for me"), resolved by
   binding a `TcpListener` to `(loopback, 0)`, reading back the OS-assigned
   port, and releasing the probe immediately so `HttpListener` can bind the
   same number for its `http://localhost:<port>/` prefix. This has a
   microscopic release-then-rebind race in principle (another process could
   grab the same port in between); given this is a single-user,
   localhost-only feature invoked once per `listen --view` run, that
   documented risk is accepted rather than adding retry machinery for it. A
   single fixed default port was considered and rejected: a stale previous
   session, or literally any other local service, could already be squatting
   on a fixed number, whereas an OS-assigned ephemeral port is free virtually
   always.

9. **Clients: broadcast to every connection; a late joiner gets the current
   state.** Covered under *The SSE protocol* above — `LiveNotationServer`
   supports any number of simultaneously connected browsers, and a tab
   opened mid-session sees the piece as it stands, not a blank page.

---

## Testing

**Automated — no audio device, no browser; all `Category=Fast`, CI-safe:**

- `LiveScoreProjector` (Application): fake note lists (the same style as
  `LiveTranscriptionSessionTests`' `Note(midi, onset, dur)` helper) drive
  `Add`/`LiveScores`; assertions confirm the i-th produced `Score` is exactly
  `Quantizer.Quantize` over the first i+1 notes, that a previously-returned
  `Score` is never mutated by a later `Add`, and (via a CsCheck property) that
  this prefix relationship holds for randomly generated note sequences across
  the MVP pitch range. No `IAudioSource`, no pipeline, no device.
- `LiveNotationServer` (Infrastructure): started on an OS-assigned free port
  inside the test process; driven entirely by an in-process `HttpClient` —
  `GET /` returns the HTML, `GET /osmd/...` returns the bundle bytes (a small
  test fixture, not the real vendored file — the constructor takes an
  explicit `webRootPath` for exactly this reason), connecting to `/events`
  and calling `PublishScore` delivers a base64 event that decodes to
  `MusicXmlScoreWriter.WriteToString(score)` for that same score, a client
  that connects *after* a publish still receives the current score
  immediately (late-join), and two simultaneously connected clients both
  receive the same publish (broadcast).
- `ListenCommand`'s two new hooks: spy delegates, the same pattern as the
  existing `SpyMidiWriter`/`SpyScoreWriter` fakes in `ListenCommandTests.cs`,
  confirm they fire with the right notes/Score, and that the two *existing*
  tests (which don't pass these parameters) are untouched.
- Vendored-asset sanity (`opensheetmusicdisplay.min.js`, its license file):
  existence, a size floor, and a "doesn't start like a fetch-failure HTML
  page" check — the same shape as Step 8's `SoundFontFixtureTests`.
- Web-asset content: `index.html`/`app.js` are committed text; a test asserts
  they reference the right paths/calls (`EventSource("/events")`,
  `osmd.load(`, `osmd.render()`, the OSMD script path) without needing a DOM
  or a browser.

**Manual acceptance — documented in the plan's Task 6, not automated** (the
same posture Step 10 takes for the mic device and Step 11 for MuseScore):
running `claudio listen --tempo <bpm> --view` for real, confirming the
browser opens, confirming notes actually appear on rendered staves as
they're played, confirming the sheet corrects itself at Ctrl+C, and
confirming a second, manually-opened tab shows the current sheet
immediately. This is the only place actual OSMD rendering and actual browser
behavior are exercised — no headless-browser dependency is added to prove it
in CI.

**Determinism.** `MusicXmlScoreWriter.WriteToString` is already
byte-deterministic per `Score` (Step 11's golden + determinism guard); this
feature adds no new nondeterminism to that mapping. It only decides *when*
to call it and *how* to transport the result.

---

## License

OpenSheetMusicDisplay is **BSD-3-Clause**. Its vendored bundle and license
text are recorded in `DECISIONS.md` (§1 rule 7) with the same fields Step 8
recorded for the SoundFont: source URL and version, the committed file path,
its size and SHA-256, and a one-line permissiveness confirmation. BSD-3-Clause
is on the §1 rule 7 allow-list (MIT / Apache-2.0 / BSD / MPL-2.0) — no
copyleft concern, nothing to escalate.

---

## Out of scope / future

- **MIDI input** as an alternative to the microphone — skip pitch/onset
  detection entirely and feed a connected MIDI keyboard's note-on/off
  straight into the same live-Score-projection idea. A different, simpler
  input adapter; not built here (decision 1 above).
- **Polyphony** (§8 item 1) — the live view renders whatever `StreamNotes`
  emits; if/when a polyphonic `ITranscriber` lands behind the same port,
  this feature inherits it for free. Chords are not attempted now — the MVP
  transcriber is monophonic.
- **Incremental OSMD rendering** (an append/cursor API instead of
  whole-document `load()`/`render()`) — only worth building if session
  lengths grow large enough that full re-render becomes visibly slow; not a
  concern at MVP/Phase-2 session lengths.
- **A heartbeat/keep-alive ping** on the SSE stream, for prompt dead-connection
  detection on long-running sessions (see *The SSE protocol* above).
- **Treble/bass staff split, tempo estimation, pYIN** — the other Phase-2
  items (§8), unrelated to this feature and unaffected by it.
