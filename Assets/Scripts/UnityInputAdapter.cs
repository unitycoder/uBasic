using System.Collections.Generic;
using UnityEngine;

namespace UBasic {

    /// <summary>Maps uBasic key codes onto Unity's input.
    ///
    /// A uBasic key code is just the ASCII code of the key ("A", "1", " "),
    /// plus 1..4 for the arrows. KEYHIT needs a rising edge, which Unity's
    /// GetKeyDown already gives us -- but only if polled once per frame, which
    /// is what Poll is for.</summary>
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
            Debug.LogWarning("[uBasic] KEY/MOUSE need the legacy Input Manager. " +
                "Set Project Settings > Player > Active Input Handling to " +
                "\"Both\", or replace UnityInputAdapter with one built on the " +
                "new Input System.");
        }
#endif
    }
}
