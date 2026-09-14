using System.Collections.Generic;
using UnityEngine;

namespace UBasic {

    /// <summary>Turns queued NoteEvents into audible sound using pre-rendered
    /// AudioClips, with no OnAudioFilterRead anywhere -- so it works on WebGL.
    ///
    /// The trick that makes caching cheap: each cached clip holds a whole number
    /// of cycles of a square wave and is played **looping**, so a clip encodes a
    /// *frequency* and nothing else. Duration is decided by when playback stops.
    /// One clip therefore serves every note length, and the entire chromatic
    /// range QBasic can address is about 84 clips.
    ///
    /// All timing lives in SoundScheduler, which is engine-free and tested
    /// headlessly. This class only makes clips and talks to AudioSources.</summary>
    public class UnityAudioAdapter {

        /// <summary>Longer chunks mean smaller frequency error. 4096 samples
        /// keeps every note inside a quarter of a cent; a single cycle would be
        /// about 4 cents sharp at A4, which is audible in a melody.</summary>
        public const int CycleSamples = 4096;

        private readonly Dictionary<int, AudioClip> _clips = new Dictionary<int, AudioClip>();
        private readonly List<ScheduledNote> _starts = new List<ScheduledNote>();
        private readonly SoundScheduler _sched;
        private readonly AudioSource[] _sources;
        private readonly double[] _stopAt;
        private readonly int _rate;
        private readonly GameObject _host;
        private bool _warnedCache;

        public float Volume = 0.28f;
        /// <summary>Sample-accurate scheduling. If a platform mishandles
        /// PlayScheduled, turn this off for plain Play/Stop at frame
        /// granularity.</summary>
        public bool UseScheduling = true;
        public int MaxCachedClips = 256;

        public int CachedClipCount { get { return _clips.Count; } }
        public SoundScheduler Scheduler { get { return _sched; } }

        public UnityAudioAdapter(Transform parent, int voices = 4) {
            _rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 44100;
            _sched = new SoundScheduler(voices);

            _host = new GameObject("uBasic Audio");
            _host.transform.SetParent(parent, false);
            _host.hideFlags = HideFlags.DontSave;

            _sources = new AudioSource[_sched.VoiceCount];
            _stopAt = new double[_sources.Length];
            for (int i = 0; i < _sources.Length; i++) {
                AudioSource s = _host.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = true;              // the clip is one loopable chunk
                s.spatialBlend = 0f;        // 2D, no positional falloff
                s.bypassEffects = true;
                s.bypassReverbZones = true;
                _sources[i] = s;
            }
        }

        /// <summary>Build the whole chromatic range up front so no note ever
        /// costs a clip allocation mid-game. About 84 clips, well under a
        /// megabyte, and a few milliseconds to generate.</summary>
        public void PrewarmNoteTable() {
            for (int n = 0; n < 84; n++) GetClip(SoundDevice.NoteNumberToFreq(n));
        }

        private AudioClip GetClip(float freq) {
            int key = Mathf.RoundToInt(freq);
            AudioClip clip;
            if (_clips.TryGetValue(key, out clip) && clip != null) return clip;

            if (_clips.Count >= MaxCachedClips) {
                // Only reachable if a program sweeps continuously through
                // frequencies; normal music lands on a small stable set.
                if (!_warnedCache) {
                    _warnedCache = true;
                    Debug.LogWarning("[uBasic] tone cache full (" + MaxCachedClips +
                        "); clearing. This only happens with continuously varying " +
                        "SOUND frequencies.");
                }
                foreach (AudioClip c in _clips.Values) if (c != null) Object.Destroy(c);
                _clips.Clear();
            }

            float[] data = SoundDevice.BuildLoopCycle(freq, _rate, Volume, CycleSamples);
            clip = AudioClip.Create("tone" + key, data.Length, 1, _rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontSave;
            _clips[key] = clip;
            return clip;
        }

        /// <summary>Called once per frame from UBasicRunner.</summary>
        public void Update(SoundDevice dev) {
            double now = AudioSettings.dspTime;

            _sched.Update(dev, now, _starts);

            for (int i = 0; i < _starts.Count; i++) {
                ScheduledNote sn = _starts[i];
                AudioSource src = _sources[sn.Voice];
                src.clip = GetClip(sn.Freq);
                src.volume = 1f;

                if (UseScheduling) {
                    src.PlayScheduled(sn.Start);
                    src.SetScheduledEndTime(sn.End);
                    _stopAt[sn.Voice] = 0;             // the engine handles the stop
                } else {
                    src.Play();
                    _stopAt[sn.Voice] = now + (sn.End - sn.Start);
                }
            }

            if (!UseScheduling) {
                for (int i = 0; i < _sources.Length; i++)
                    if (_stopAt[i] > 0 && now >= _stopAt[i]) {
                        _sources[i].Stop();
                        _stopAt[i] = 0;
                    }
            }
        }

        public void StopAll() {
            for (int i = 0; i < _sources.Length; i++) { _sources[i].Stop(); _stopAt[i] = 0; }
            _sched.Reset();
        }

        public void Dispose() {
            StopAll();
            foreach (AudioClip c in _clips.Values) if (c != null) Object.Destroy(c);
            _clips.Clear();
            if (_host != null) Object.Destroy(_host);
        }
    }
}
