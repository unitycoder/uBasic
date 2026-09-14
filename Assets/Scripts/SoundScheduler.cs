using System;
using System.Collections.Generic;

namespace UBasic {

    /// <summary>A note placed on the audio timeline and assigned a voice.</summary>
    public struct ScheduledNote {
        public int Voice;
        public float Freq;
        public double Start;   // when the tone begins, on the host's audio clock
        public double End;     // when it stops (Start + the sounded portion)
    }

    /// <summary>Decides *when* each queued note plays and on which voice.
    ///
    /// Deliberately engine-free and clock-injected. This logic used to live
    /// inside the Unity adapter, where it could not be run headlessly -- and a
    /// bug that reported "busy" forever while idle therefore survived a full
    /// test pass, permanently blocking the first SOUND. Both the Unity adapter
    /// and the test harness now drive this same class.</summary>
    public class SoundScheduler {

        /// <summary>How far ahead of the audio clock notes are scheduled.</summary>
        public double Lookahead = 0.15;
        /// <summary>Margin between "now" and the earliest note start. Needs to
        /// comfortably exceed one frame, or a scheduled start can fall into the
        /// past before the audio thread sees it.</summary>
        public double StartOffset = 0.05;

        private readonly Queue<NoteEvent> _pending = new Queue<NoteEvent>();
        private readonly double[] _voiceFreeAt;
        private double _nextStart;
        private double _busyUntil;

        public SoundScheduler(int voices) {
            _voiceFreeAt = new double[Math.Max(1, voices)];
        }

        public int VoiceCount { get { return _voiceFreeAt.Length; } }
        public int PendingCount { get { return _pending.Count; } }
        public double BusyUntil { get { return _busyUntil; } }

        public void Reset() {
            _pending.Clear();
            _nextStart = 0;
            _busyUntil = 0;
            for (int i = 0; i < _voiceFreeAt.Length; i++) _voiceFreeAt[i] = 0;
        }

        /// <summary>Drain the device, lay notes on the timeline, and report
        /// whether sound is still outstanding. Notes that should start are
        /// appended to <paramref name="starts"/>, which is cleared first.</summary>
        public void Update(SoundDevice dev, double now, List<ScheduledNote> starts) {
            if (starts != null) starts.Clear();

            NoteEvent e;
            while (dev.TryDequeue(out e)) _pending.Enqueue(e);

            // Anchor the timeline only when there is something to place on it.
            // Pushing _nextStart forward while idle is precisely what made an
            // idle scheduler look permanently busy.
            if (_pending.Count > 0 && _nextStart < now + StartOffset)
                _nextStart = now + StartOffset;

            while (_pending.Count > 0 && _nextStart < now + Lookahead) {
                NoteEvent note = _pending.Peek();

                // Rests, and the inaudible high tones QBasic programs use as a
                // delay, consume time without needing a voice.
                bool audible = note.Freq > 0f
                            && note.Freq <= SoundDevice.MaxAudible
                            && note.Sounded > 0f;

                int voice = -1;
                if (audible) {
                    voice = FindVoice(_nextStart);
                    if (voice < 0) break;          // all busy; try again next frame
                }

                _pending.Dequeue();

                if (audible) {
                    ScheduledNote sn;
                    sn.Voice = voice;
                    sn.Freq = note.Freq;
                    sn.Start = _nextStart;
                    sn.End = _nextStart + note.Sounded;
                    _voiceFreeAt[voice] = sn.End;
                    if (starts != null) starts.Add(sn);
                }

                _nextStart += note.Duration;
                if (_nextStart > _busyUntil) _busyUntil = _nextStart;
            }

            // Busy is derived only from notes actually placed, never from the
            // write cursor.
            dev.HostBusy = _pending.Count > 0 || now < _busyUntil;
        }

        private int FindVoice(double at) {
            for (int i = 0; i < _voiceFreeAt.Length; i++)
                if (_voiceFreeAt[i] <= at + 1e-4) return i;
            return -1;
        }
    }
}
