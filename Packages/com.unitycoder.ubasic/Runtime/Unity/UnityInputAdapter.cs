using System.Collections.Generic;
using UnityEngine;

namespace Unitycoder.UBasic {

    /// <summary>Maps uBasic key codes onto Unity's input.
    ///
    /// A uBasic key code is just the ASCII code of the key ("A", "1", " "),
    /// plus 1..4 for the arrows. KEYHIT needs a rising edge, which Unity's
    /// GetKeyDown already gives us -- but only if polled once per frame, which
    /// is what Poll is for.
    ///
    /// QBasic's INKEY$ and INPUT read a typed-character buffer instead, which
    /// PumpKeys fills once per frame.</summary>
    public class UnityInputAdapter : IInput {

        private readonly UBasicRunner _runner;
        private readonly Dictionary<int, KeyCode> _map = new Dictionary<int, KeyCode>();
        private int _mx, _my;
        private bool _warned;

        public UnityInputAdapter(UBasicRunner runner) {
            _runner = runner;

            _map[1] = KeyCode.LeftArrow;
            _map[2] = KeyCode.RightArrow;
            _map[3] = KeyCode.UpArrow;
            _map[4] = KeyCode.DownArrow;
            _map[8] = KeyCode.Backspace;
            _map[9] = KeyCode.Tab;
            _map[13] = KeyCode.Return;
            _map[27] = KeyCode.Escape;
            _map[32] = KeyCode.Space;

            for (int c = 'A'; c <= 'Z'; c++) {
                KeyCode kc = KeyCode.A + (c - 'A');
                _map[c] = kc;
                _map[c + 32] = kc;            // accept lowercase ASCII too
            }
            for (int d = '0'; d <= '9'; d++)
                _map[d] = KeyCode.Alpha0 + (d - '0');
        }

        public void Poll() {
            int x, y;
            if (_runner != null && _runner.TryMouseToScreen(out x, out y)) { _mx = x; _my = y; }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // Keys that INKEY$ reports as CHR$(0) + scan code.
        private static readonly KeyCode[] ExtKeys = {
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
            KeyCode.Insert, KeyCode.Delete,
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5,
            KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10
        };
        private static readonly int[] ExtScan = {
            72, 80, 75, 77, 71, 79, 73, 81, 82, 83,
            59, 60, 61, 62, 63, 64, 65, 66, 67, 68
        };
#endif

        /// <summary>Move this frame's typing into the VM's keyboard buffer.
        /// Call once per frame, before VM.Run.</summary>
        public void PumpKeys(VM vm) {
#if ENABLE_LEGACY_INPUT_MANAGER
            string typed = Input.inputString;
            for (int i = 0; i < typed.Length; i++) {
                char c = typed[i];
                if (c == '\n' || c == '\r') vm.PushKey('\r');
                else if (c == '\b') vm.PushKey('\b');
                else if (c == 27 || c == '\t') continue;       // pushed from key state below
                else if (c >= ' ') vm.PushKey(Cp437.FromUnicode(c));
            }
            // inputString only carries printable text, Enter and Backspace.
            if (Input.GetKeyDown(KeyCode.Escape)) vm.PushKey((char)27);
            if (Input.GetKeyDown(KeyCode.Tab)) vm.PushKey('\t');
            for (int i = 0; i < ExtKeys.Length; i++)
                if (Input.GetKeyDown(ExtKeys[i])) vm.PushExtendedKey(ExtScan[i]);
#endif
        }

        private bool TryKey(int code, out KeyCode kc) {
            return _map.TryGetValue(code, out kc);
        }

        public bool Held(int code) {
#if ENABLE_LEGACY_INPUT_MANAGER
            KeyCode kc;
            return TryKey(code, out kc) && Input.GetKey(kc);
#else
            WarnOnce();
            return false;
#endif
        }

        public bool Hit(int code) {
#if ENABLE_LEGACY_INPUT_MANAGER
            KeyCode kc;
            return TryKey(code, out kc) && Input.GetKeyDown(kc);
#else
            WarnOnce();
            return false;
#endif
        }

        public int MouseX { get { return _mx; } }
        public int MouseY { get { return _my; } }

        public bool MouseButton(int n) {
#if ENABLE_LEGACY_INPUT_MANAGER
            return n >= 0 && n <= 2 && Input.GetMouseButton(n);
#else
            WarnOnce();
            return false;
#endif
        }

#if !ENABLE_LEGACY_INPUT_MANAGER
        private void WarnOnce() {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning("[uBasic] KEY/MOUSE/INKEY$ need the legacy Input Manager. " +
                "Set Project Settings > Player > Active Input Handling to " +
                "\"Both\", or replace UnityInputAdapter with one built on the " +
                "new Input System.");
        }
#endif
    }
}
