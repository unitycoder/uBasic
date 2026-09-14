using System;
using System.Collections.Generic;

namespace UBasic {

    /// <summary>One scheduled note. Duration is the full slot; Sounded is how
    /// much of it actually makes noise (QBasic's ML/MN/MS articulation puts a
    /// gap at the end of the slot rather than shortening the slot).</summary>
    public struct NoteEvent {
        public float Freq;        // Hz; 0 means a rest
        public float Duration;    // seconds, full slot
        public float Sounded;     // seconds of actual tone
        public bool Foreground;   // MF notes block the program, MB notes do not
    }

    /// <summary>QBasic's SOUND and PLAY, with no engine dependency.
    ///
    /// This only ever produces a queue of NoteEvents and the raw waveform for a
    /// given frequency. Who turns that into audible sound -- a Unity AudioClip
    /// pool, or a WAV file in a test -- is the host's problem.</summary>
    public class SoundDevice {

        /// <summary>QBasic's timer tick. One second is about 18.2 of them.</summary>
        public const float TicksPerSecond = 18.2f;
        public const float MinFreq = 37f;      // QBasic's documented floor
        public const float MaxAudible = 20000f; // above this, generate silence

        private readonly Queue<NoteEvent> _queue = new Queue<NoteEvent>();

        // ---- PLAY state, persistent across calls exactly as in QBasic ----
        private int _tempo = 120;      // quarter notes per minute
        private int _octave = 4;
        private int _length = 4;       // default note length denominator
        private float _articulation = 7f / 8f;   // MN
        private bool _foreground = true;         // MF

        /// <summary>Foreground notes still waiting to be picked up by the host.</summary>
        public int ForegroundQueued { get; private set; }

        /// <summary>Set by the host while previously dequeued foreground notes
        /// are still sounding. The VM blocks on IsBusy.</summary>
        public bool HostBusy;

        public bool IsBusy { get { return ForegroundQueued > 0 || HostBusy; } }
        public int PendingCount { get { return _queue.Count; } }

        public void Reset() {
            _queue.Clear();
            ForegroundQueued = 0;
            HostBusy = false;
            _tempo = 120; _octave = 4; _length = 4;
            _articulation = 7f / 8f;
            _foreground = true;
        }

        private void Enqueue(float freq, float seconds, float sounded, bool fg) {
            NoteEvent e = new NoteEvent();
            e.Freq = freq; e.Duration = seconds; e.Sounded = sounded; e.Foreground = fg;
            _queue.Enqueue(e);
            if (fg) ForegroundQueued++;
        }

        public bool TryDequeue(out NoteEvent e) {
            if (_queue.Count == 0) { e = new NoteEvent(); return false; }
            e = _queue.Dequeue();
            if (e.Foreground && ForegroundQueued > 0) ForegroundQueued--;
            return true;
        }

        // ---- SOUND ---------------------------------------------------------

        /// <summary>SOUND f, d -- d is in QBasic ticks. Always foreground, so
        /// the program waits for it, which is what makes the classic
        /// "SOUND 32000, 18.2" one-second-delay idiom work.</summary>
        public void Sound(float hz, float ticks) {
            float secs = Math.Max(0f, ticks) / TicksPerSecond;
            if (secs <= 0f) return;
            Enqueue(hz, secs, secs, true);
        }

        public void Beep() { Sound(800f, 4f); }

        // ---- PLAY ----------------------------------------------------------

        /// <summary>Parse and queue an MML string. Parsing happens at runtime
        /// because the string may be built dynamically -- it runs once per
        /// PLAY, not per frame, so the cost is irrelevant.</summary>
        public void Play(string mml) {
            if (string.IsNullOrEmpty(mml)) return;
            int i = 0;
            int n = mml.Length;

            while (i < n) {
                char c = char.ToUpperInvariant(mml[i]);

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }

                if (c == 'O') { i++; _octave = Clamp(ReadInt(mml, ref i, _octave), 0, 6); continue; }
                if (c == '>') { i++; if (_octave < 6) _octave++; continue; }
                if (c == '<') { i++; if (_octave > 0) _octave--; continue; }
                if (c == 'T') { i++; _tempo = Clamp(ReadInt(mml, ref i, _tempo), 32, 255); continue; }
                if (c == 'L') { i++; _length = ClampLen(ReadInt(mml, ref i, _length)); continue; }

                if (c == 'M') {
                    i++;
                    char m = i < n ? char.ToUpperInvariant(mml[i]) : '\0';
                    i++;
                    if (m == 'B') _foreground = false;
                    else if (m == 'F') _foreground = true;
                    else if (m == 'L') _articulation = 1f;
                    else if (m == 'N') _articulation = 7f / 8f;
                    else if (m == 'S') _articulation = 3f / 4f;
                    continue;
                }

                if (c == 'P' || c == 'R') {                 // rest
                    i++;
                    int len = ClampLen(ReadInt(mml, ref i, _length));
                    float d = NoteSeconds(len, ReadDots(mml, ref i));
                    Enqueue(0f, d, 0f, _foreground);
                    continue;
                }

                if (c == 'N') {                              // absolute note number
                    i++;
                    int num = Clamp(ReadInt(mml, ref i, 0), 0, 84);
                    float d = NoteSeconds(_length, ReadDots(mml, ref i));
                    if (num == 0) Enqueue(0f, d, 0f, _foreground);
                    else EnqueueTone(NoteNumberToFreq(num - 1), d);
                    continue;
                }

                if (c >= 'A' && c <= 'G') {
                    i++;
                    int semi = LetterSemitone(c);
                    // accidentals
                    while (i < n && (mml[i] == '#' || mml[i] == '+' || mml[i] == '-')) {
                        semi += (mml[i] == '-') ? -1 : 1;
                        i++;
                    }
                    int len = ClampLen(ReadInt(mml, ref i, _length));
                    float d = NoteSeconds(len, ReadDots(mml, ref i));
                    EnqueueTone(NoteNumberToFreq(_octave * 12 + semi), d);
                    continue;
                }

                i++;   // unknown character: skip it rather than fault
            }
        }

        private void EnqueueTone(float freq, float slot) {
            Enqueue(freq, slot, slot * _articulation, _foreground);
        }

        private static int LetterSemitone(char c) {
            switch (c) {
                case 'C': return 0;
                case 'D': return 2;
                case 'E': return 4;
                case 'F': return 5;
                case 'G': return 7;
                case 'A': return 9;
                default:  return 11;   // B
            }
        }

        /// <summary>Note 0 is C in octave 0; A in octave 4 is 440 Hz.</summary>
        public static float NoteNumberToFreq(int idx) {
            return (float)(440.0 * Math.Pow(2.0, (idx - 57) / 12.0));
        }

        private float NoteSeconds(int len, int dots) {
            float whole = 4f * (60f / _tempo);
            float d = whole / len;
            float add = d;
            for (int k = 0; k < dots; k++) { add *= 0.5f; d += add; }
            return d;
        }

        private static int ReadInt(string s, ref int i, int fallback) {
            int start = i;
            int v = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') {
                v = v * 10 + (s[i] - '0');
                i++;
            }
            return i == start ? fallback : v;
        }

        private static int ReadDots(string s, ref int i) {
            int d = 0;
            while (i < s.Length && s[i] == '.') { d++; i++; }
            return d;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static int ClampLen(int v) { return v < 1 ? 1 : (v > 64 ? 64 : v); }

        // ---- waveform ------------------------------------------------------

        /// <summary>Build one seamlessly loopable chunk of square wave.
        ///
        /// The chunk holds a whole number of cycles, so looping it gives a
        /// continuous tone and a single short clip serves any duration. Using
        /// several cycles rather than one keeps the frequency error to a
        /// fraction of a cent -- rounding a single cycle to whole samples puts
        /// 440 Hz about 4 cents sharp, which is audible in a melody.</summary>
        public static float[] BuildLoopCycle(float freq, int sampleRate, float amplitude, int minSamples) {
            // Above human hearing, generate silence rather than a tone. A real
            // PC speaker simply produced nothing audible; a sampled system would
            // alias it back down into a piercing screech instead.
            if (freq <= 0f || freq > MaxAudible || freq > sampleRate * 0.45f)
                return new float[Math.Max(64, minSamples)];

            if (freq < MinFreq) freq = MinFreq;

            int cycles = (int)Math.Round(minSamples * (double)freq / sampleRate);
            if (cycles < 1) cycles = 1;
            int len = (int)Math.Round(cycles * (double)sampleRate / freq);
            if (len < 2) len = 2;

            float[] data = new float[len];
            for (int i = 0; i < len; i++) {
                double phase = (i * (double)cycles / len) % 1.0;
                data[i] = phase < 0.5 ? amplitude : -amplitude;
            }
            return data;
        }

        /// <summary>Actual frequency of a clip produced by BuildLoopCycle, which
        /// differs from the request by the rounding above.</summary>
        public static float ActualFreq(float freq, int sampleRate, int minSamples) {
            int cycles = (int)Math.Round(minSamples * (double)freq / sampleRate);
            if (cycles < 1) cycles = 1;
            int len = (int)Math.Round(cycles * (double)sampleRate / freq);
            if (len < 2) len = 2;
            return (float)(cycles * (double)sampleRate / len);
        }
    }
}
