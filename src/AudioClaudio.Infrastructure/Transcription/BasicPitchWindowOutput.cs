namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// One 43 844-sample window's three posteriorgrams from the Basic Pitch model, each indexed
/// <c>[frame, bin]</c> over the window's 172 frames. Pitch bin <c>i</c> is MIDI note <c>21 + i</c>
/// (the 88 piano keys, A0 first). Values are activations in roughly 0..1.
/// </summary>
/// <param name="NoteFrames">Per-frame note presence, [172, 88]. High where a pitch is sounding.</param>
/// <param name="Onsets">Per-frame note onsets, [172, 88]. Peaks at note attacks.</param>
/// <param name="Contours">Fine-grained pitch contour, [172, 264] (3 bins/semitone) — for pitch bends.</param>
public sealed record BasicPitchWindowOutput(float[,] NoteFrames, float[,] Onsets, float[,] Contours);
