using System;
using UnityEngine;

namespace UBasic {

    /// <summary>Drives a uBasic program: compile once, then run one frame's
    /// worth of instructions per Update and blit the framebuffer to a texture.
    ///
    /// Drop this on an empty GameObject, paste a program into Source (or assign
    /// a .ubas TextAsset), and press play.</summary>
    [AddComponentMenu("uBasic/uBasic Runner")]
    public class UBasicRunner : MonoBehaviour {

        public enum Display { FullscreenGUI, Renderer, None }

        [Header("Program")]
        public TextAsset SourceFile;
        [TextArea(8, 30)]
        public string Source =
            "SCREEN 320, 200\n" +
            "DIM t%\n" +
            "DO\n" +
            "  CLS 1\n" +
            "  CIRCLE (160 + CINT(SIN(t% / 12.0) * 90), 100), 20, 14, F\n" +
            "  LOCATE 0, 0\n" +
            "  PRINT \"hello from uBasic\"\n" +
            "  t% = t% + 1\n" +
            "  WAIT\n" +
            "LOOP\n";

        [Header("Screen")]
        public int ScreenWidth = 320;
        public int ScreenHeight = 200;
        public Display DisplayMode = Display.FullscreenGUI;
        [Tooltip("Nearest-neighbour keeps the pixels crisp when upscaled.")]
        public bool PointFilter = true;

        [Header("Audio")]
        public bool EnableSound = true;
        [Range(0f, 1f)] public float Volume = 0.28f;
        [Tooltip("Generate the full 84-note table on start so no note ever " +
                 "costs a clip allocation mid-game.")]
        public bool PrewarmNotes = true;
        [Tooltip("Sample-accurate note scheduling. Turn off only if a platform " +
                 "mishandles PlayScheduled.")]
        public bool ScheduledAudio = true;

        [Header("Execution")]
        [Tooltip("Instruction ceiling per frame. A program that blows through " +
                 "this without hitting WAIT is stopped rather than hanging the editor.")]
        public int InstructionBudget = 200000;
        [Tooltip("Collect the string heap once this many strings are live.")]
        public int GcThreshold = 512;
        public bool RestartOnEnable = true;
        [Header("Performance")]
        [Tooltip("Target frames per second for the uBasic runner. Set to 0 for unlimited (run every Unity frame).")]
        public int TargetFps = 30;
        private float _frameAccumulator = 0f;

        public Texture2D Texture { get; private set; }
        public bool IsFaulted { get; private set; }
        public string FaultMessage { get; private set; }

        private Screen _scr;
        private VM _vm;
        private Chunk _chunk;
        private byte[] _rgba;
        private UnityInputAdapter _input;
        private UnityAudioAdapter _audio;
        private Renderer _renderer;
        private bool _reportedOverrun;
        private float _blockedFor;
        private bool _warnedBlocked;

        void OnEnable() {
            if (RestartOnEnable || _vm == null) Restart();
        }

        void Update() {
            if (_vm == null || IsFaulted) return;

            // throttle execution to a target FPS if requested
            if (TargetFps > 0)
            {
                float interval = 1f / Mathf.Max(1, TargetFps);
                _frameAccumulator += Time.deltaTime;
                if (_frameAccumulator < interval) return;
                _frameAccumulator -= interval;
            }

            _input.Poll();
            if (_audio != null) _audio.Update(_vm.Sound);
            else _vm.Sound.HostBusy = false;      // no device: never block on sound

            // A sound device that reports busy forever would stall the program
            // silently, which is the worst way for this to fail. Say so.
            if (_vm.Sound.IsBusy) {
                _blockedFor += Time.unscaledDeltaTime;
                if (_blockedFor > 10f && !_warnedBlocked) {
                    _warnedBlocked = true;
                    Debug.LogWarning("[uBasic] waiting on sound for over 10s. The audio " +
                        "device is reporting busy and never clearing, so the program is " +
                        "stalled at a SOUND/PLAY. Uncheck EnableSound to confirm.", this);
                }
            } else {
                _blockedFor = 0f;
            }
            _vm.Time = Time.time;

            RunState st = _vm.Run(InstructionBudget);
            if (st == RunState.Faulted) {
                IsFaulted = true;
                FaultMessage = _vm.FaultMessage;
                Debug.LogError("[uBasic] runtime error: " + _vm.FaultMessage, this);
            } else if (st == RunState.Yielded && !_reportedOverrun) {
                _reportedOverrun = true;
                Debug.LogWarning("[uBasic] used the whole instruction budget without " +
                    "reaching WAIT. Add a WAIT inside your main loop, or raise " +
                    "InstructionBudget.", this);
            }

            Upload();
        }

        /// <summary>Recompile and reset. Safe to call at runtime -- this is the
        /// hot-reload hook: swap Source and call Restart.</summary>
        public void Restart() {
            IsFaulted = false;
            FaultMessage = null;
            _reportedOverrun = false;
            _blockedFor = 0f;
            _warnedBlocked = false;

            string src = SourceFile != null ? SourceFile.text : Source;
            try {
                _chunk = Compiler.Compile(src);
            } catch (UBasicError e) {
                IsFaulted = true;
                FaultMessage = e.Message;
                Debug.LogError("[uBasic] compile error: " + e.Message, this);
                return;
            }

            if (_audio != null) { _audio.Dispose(); _audio = null; }
            if (EnableSound) {
                _audio = new UnityAudioAdapter(transform);
                _audio.Volume = Volume;
                _audio.UseScheduling = ScheduledAudio;
                if (PrewarmNotes) _audio.PrewarmNoteTable();
            }

            _scr = new Screen(Mathf.Max(8, ScreenWidth), Mathf.Max(8, ScreenHeight));
            _input = new UnityInputAdapter(this);
            _vm = new VM(_chunk, _scr);
            _vm.Input = _input;
            _vm.GcThreshold = Mathf.Max(32, GcThreshold);
            _vm.Rng = new System.Random(Environment.TickCount);

            EnsureTexture();
            Upload();
        }

        private void EnsureTexture() {
            // SCREEN can resize the framebuffer from inside the program.
            if (Texture != null && (Texture.width != _scr.W || Texture.height != _scr.H)) {
                if (Application.isPlaying) Destroy(Texture); else DestroyImmediate(Texture);
                Texture = null;
            }
            if (Texture == null) {
                Texture = new Texture2D(_scr.W, _scr.H, TextureFormat.RGBA32, false);
                Texture.wrapMode = TextureWrapMode.Clamp;
                Texture.hideFlags = HideFlags.DontSave;
                _rgba = new byte[_scr.W * _scr.H * 4];
            }
            Texture.filterMode = PointFilter ? FilterMode.Point : FilterMode.Bilinear;

            if (DisplayMode == Display.Renderer) {
                if (_renderer == null) _renderer = GetComponent<Renderer>();
                if (_renderer != null && _renderer.sharedMaterial != null)
                    _renderer.material.mainTexture = Texture;
            }
        }

        private void Upload() {
            if (_scr == null) return;
            EnsureTexture();
            if (!_scr.Dirty) return;

            // Unity textures are bottom-up; the framebuffer is top-down.
            _scr.BlitRgba32(_rgba, true);
            Texture.LoadRawTextureData(_rgba);
            Texture.Apply(false);
            _scr.Dirty = false;
        }

        /// <summary>Where the framebuffer is drawn on screen, used both to draw
        /// it and to map the mouse back into framebuffer coordinates.</summary>
        public Rect DisplayRect {
            get {
                if (_scr == null) return new Rect(0, 0, 1, 1);
                float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
                float scale = Mathf.Min(sw / _scr.W, sh / _scr.H);
                if (PointFilter && scale > 1f) scale = Mathf.Floor(scale);  // integer scaling
                float w = _scr.W * scale, h = _scr.H * scale;
                return new Rect((sw - w) * 0.5f, (sh - h) * 0.5f, w, h);
            }
        }

        public bool TryMouseToScreen(out int sx, out int sy) {
            sx = sy = 0;
            if (_scr == null) return false;
            Rect r = DisplayRect;
            Vector3 m = UnityEngine.Input.mousePosition;
            float gx = m.x;
            float gy = UnityEngine.Screen.height - m.y;      // GUI space is top-down
            if (gx < r.x || gy < r.y || gx >= r.xMax || gy >= r.yMax) return false;
            sx = Mathf.Clamp((int)((gx - r.x) / r.width * _scr.W), 0, _scr.W - 1);
            sy = Mathf.Clamp((int)((gy - r.y) / r.height * _scr.H), 0, _scr.H - 1);
            return true;
        }

        void OnGUI() {
            if (DisplayMode != Display.FullscreenGUI || Texture == null) return;
            GUI.DrawTexture(DisplayRect, Texture, ScaleMode.StretchToFill, false);
            if (IsFaulted && FaultMessage != null) {
                GUI.color = Color.red;
                GUI.Label(new Rect(8, 8, UnityEngine.Screen.width - 16, 40), "uBasic: " + FaultMessage);
                GUI.color = Color.white;
            }
        }

        void OnDisable() {
            if (_audio != null) _audio.StopAll();
        }

        void OnDestroy() {
            if (_audio != null) { _audio.Dispose(); _audio = null; }
            if (Texture != null) {
                if (Application.isPlaying) Destroy(Texture); else DestroyImmediate(Texture);
                Texture = null;
            }
        }
    }
}
