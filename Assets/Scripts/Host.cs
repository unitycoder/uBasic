using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UBasic {

    /// <summary>Everything the host program lets scripts touch. Unity supplies a
    /// real implementation; the headless test harness supplies a stub.</summary>
    public interface IInput {
        bool Held(int code);
        bool Hit(int code);
        int MouseX { get; }
        int MouseY { get; }
        bool MouseButton(int n);
    }

    public class NullInput : IInput {
        public bool Held(int code) { return false; }
        public bool Hit(int code) { return false; }
        public int MouseX { get { return 0; } }
        public int MouseY { get { return 0; } }
        public bool MouseButton(int n) { return false; }
    }

    public delegate Slot HostImpl(VM vm, Slot[] a);

    public class HostFn {
        public string Name;
        public VType[] Params;
        public VType Ret;
        public bool HasRet;
        public HostImpl Impl;
        public int Argc { get { return Params.Length; } }
    }

    /// <summary>Fixed-arity binding table. Because every signature is static and
    /// declared here, the compiler inserts any needed conversions and the VM
    /// never reflects, boxes, or type-checks at a call boundary.</summary>
    public static class Host {
        public static readonly List<HostFn> All = new List<HostFn>();
        private static readonly Dictionary<string, List<int>> ByName =
            new Dictionary<string, List<int>>();

        private static readonly VType I = VType.Int;
        private static readonly VType F = VType.Single;
        private static readonly VType S = VType.Str;

        private static void Def(string name, VType[] ps, VType ret, bool hasRet, HostImpl impl) {
            HostFn fn = new HostFn();
            fn.Name = name; fn.Params = ps; fn.Ret = ret; fn.HasRet = hasRet; fn.Impl = impl;
            All.Add(fn);
            List<int> lst;
            if (!ByName.TryGetValue(name, out lst)) { lst = new List<int>(); ByName[name] = lst; }
            lst.Add(All.Count - 1);
        }

        private static void Fn(string name, VType[] ps, VType ret, HostImpl impl) {
            Def(name, ps, ret, true, impl);
        }
        private static void Proc(string name, VType[] ps, HostImpl impl) {
            Def(name, ps, VType.Int, false, impl);
        }

        /// <summary>Resolve by name and argument count. Returns -1 if absent.</summary>
        public static int Find(string name, int argc) {
            List<int> lst;
            if (!ByName.TryGetValue(name, out lst)) return -1;
            for (int i = 0; i < lst.Count; i++)
                if (All[lst[i]].Argc == argc) return lst[i];
            return -1;
        }

        public static bool Exists(string name) { return ByName.ContainsKey(name); }

        public static int MinArgc(string name) {
            List<int> lst;
            if (!ByName.TryGetValue(name, out lst)) return -1;
            int m = int.MaxValue;
            for (int i = 0; i < lst.Count; i++) m = Math.Min(m, All[lst[i]].Argc);
            return m;
        }

        private static Slot NoRet() { return new Slot(); }
        private static Slot RI(int v) { return Slot.FromInt(v); }
        private static Slot RF(float v) { return Slot.FromFloat(v); }
        private static Slot RS(VM vm, string v) { return Slot.FromStr(vm.Strings.Alloc(v)); }

        static Host() {
            VType[] none = new VType[0];

            // ---- math ----
            Fn("ABS",  new[]{F}, F, (vm,a) => RF(Math.Abs(a[0].F)));
            Fn("SGN",  new[]{F}, I, (vm,a) => RI(a[0].F > 0 ? 1 : (a[0].F < 0 ? -1 : 0)));
            Fn("INT",  new[]{F}, F, (vm,a) => RF((float)Math.Floor(a[0].F)));
            Fn("CINT", new[]{F}, I, (vm,a) => RI((int)Math.Round(a[0].F, MidpointRounding.AwayFromZero)));
            Fn("SQR",  new[]{F}, F, (vm,a) => RF(a[0].F <= 0 ? 0f : (float)Math.Sqrt(a[0].F)));
            Fn("SIN",  new[]{F}, F, (vm,a) => RF((float)Math.Sin(a[0].F)));
            Fn("COS",  new[]{F}, F, (vm,a) => RF((float)Math.Cos(a[0].F)));
            Fn("TAN",  new[]{F}, F, (vm,a) => RF((float)Math.Tan(a[0].F)));
            Fn("ATN",  new[]{F}, F, (vm,a) => RF((float)Math.Atan(a[0].F)));
            Fn("ATN2", new[]{F,F}, F, (vm,a) => RF((float)Math.Atan2(a[0].F, a[1].F)));
            Fn("EXP",  new[]{F}, F, (vm,a) => RF((float)Math.Exp(a[0].F)));
            Fn("LOG",  new[]{F}, F, (vm,a) => RF(a[0].F <= 0 ? 0f : (float)Math.Log(a[0].F)));
            Fn("MIN",  new[]{F,F}, F, (vm,a) => RF(Math.Min(a[0].F, a[1].F)));
            Fn("MAX",  new[]{F,F}, F, (vm,a) => RF(Math.Max(a[0].F, a[1].F)));
            Fn("RND",  none,      F, (vm,a) => RF((float)vm.Rng.NextDouble()));
            Fn("RND",  new[]{F},  F, (vm,a) => {
                int n = (int)a[0].F;
                return RF(n <= 0 ? 0f : vm.Rng.Next(n));
            });

            // ---- conversion / strings ----
            Fn("CSNG", new[]{F}, F, (vm,a) => RF(a[0].F));
            Fn("LEN",  new[]{S}, I, (vm,a) => RI(vm.Strings.Get(a[0].S).Length));
            Fn("CHR$", new[]{F}, S, (vm,a) => RS(vm, ((char)(int)a[0].F).ToString()));
            Fn("ASC",  new[]{S}, I, (vm,a) => {
                string s = vm.Strings.Get(a[0].S);
                return RI(s.Length == 0 ? 0 : s[0]);
            });
            Fn("STR$", new[]{F}, S, (vm,a) => RS(vm, Screen.FormatNumber(a[0].F).TrimEnd()));
            Fn("VAL",  new[]{S}, F, (vm,a) => {
                float v;
                float.TryParse(vm.Strings.Get(a[0].S).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                return RF(v);
            });
            Fn("LEFT$", new[]{S,F}, S, (vm,a) => {
                string s = vm.Strings.Get(a[0].S);
                int n = Clamp((int)a[1].F, 0, s.Length);
                return RS(vm, s.Substring(0, n));
            });
            Fn("RIGHT$", new[]{S,F}, S, (vm,a) => {
                string s = vm.Strings.Get(a[0].S);
                int n = Clamp((int)a[1].F, 0, s.Length);
                return RS(vm, s.Substring(s.Length - n, n));
            });
            Fn("MID$", new[]{S,F}, S, (vm,a) => {
                string s = vm.Strings.Get(a[0].S);
                int st = Clamp((int)a[1].F - 1, 0, s.Length);      // QBasic is 1-based
                return RS(vm, s.Substring(st));
            });
            Fn("MID$", new[]{S,F,F}, S, (vm,a) => {
                string s = vm.Strings.Get(a[0].S);
                int st = Clamp((int)a[1].F - 1, 0, s.Length);
                int n  = Clamp((int)a[2].F, 0, s.Length - st);
                return RS(vm, s.Substring(st, n));
            });
            Fn("INSTR", new[]{S,S}, I, (vm,a) =>
                RI(vm.Strings.Get(a[0].S).IndexOf(vm.Strings.Get(a[1].S), StringComparison.Ordinal) + 1));
            Fn("UCASE$", new[]{S}, S, (vm,a) => RS(vm, vm.Strings.Get(a[0].S).ToUpperInvariant()));
            Fn("LCASE$", new[]{S}, S, (vm,a) => RS(vm, vm.Strings.Get(a[0].S).ToLowerInvariant()));
            Fn("TRIM$",  new[]{S}, S, (vm,a) => RS(vm, vm.Strings.Get(a[0].S).Trim()));
            Fn("SPACE$", new[]{F}, S, (vm,a) => RS(vm, new string(' ', Clamp((int)a[0].F, 0, 4096))));
            Fn("STRING$", new[]{F,S}, S, (vm,a) => {
                string s = vm.Strings.Get(a[1].S);
                char c = s.Length > 0 ? s[0] : ' ';
                return RS(vm, new string(c, Clamp((int)a[0].F, 0, 4096)));
            });

            // ---- screen queries ----
            Fn("POINT", new[]{F,F}, I, (vm,a) => RI(vm.Scr.Point((int)a[0].F, (int)a[1].F)));
            Fn("SCRW",  none, I, (vm,a) => RI(vm.Scr.W));
            Fn("SCRH",  none, I, (vm,a) => RI(vm.Scr.H));
            Fn("TIMER", none, F, (vm,a) => RF(vm.Time));

            // ---- input ----
            Fn("KEY",    new[]{F}, I, (vm,a) => RI(vm.Input.Held((int)a[0].F) ? -1 : 0));
            Fn("KEYHIT", new[]{F}, I, (vm,a) => RI(vm.Input.Hit((int)a[0].F) ? -1 : 0));
            Fn("MOUSEX", none, I, (vm,a) => RI(vm.Input.MouseX));
            Fn("MOUSEY", none, I, (vm,a) => RI(vm.Input.MouseY));
            Fn("MOUSEB", new[]{F}, I, (vm,a) => RI(vm.Input.MouseButton((int)a[0].F) ? -1 : 0));

            // ---- statement primitives (emitted by the compiler, not callable) ----
            Proc("__cls",     new[]{F},         (vm,a) => { vm.Scr.Cls((int)a[0].F); return NoRet(); });
            Proc("__screen",  new[]{F,F},       (vm,a) => { vm.Scr.Resize((int)a[0].F, (int)a[1].F); return NoRet(); });
            Proc("__pset",    new[]{F,F,F},     (vm,a) => { vm.Scr.PSet((int)a[0].F, (int)a[1].F, (int)a[2].F); return NoRet(); });
            Proc("__line",    new[]{F,F,F,F,F}, (vm,a) => { vm.Scr.Line((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F, (int)a[4].F); return NoRet(); });
            Proc("__box",     new[]{F,F,F,F,F}, (vm,a) => { vm.Scr.Box((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F, (int)a[4].F); return NoRet(); });
            Proc("__boxf",    new[]{F,F,F,F,F}, (vm,a) => { vm.Scr.BoxFill((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F, (int)a[4].F); return NoRet(); });
            Proc("__circle",  new[]{F,F,F,F},   (vm,a) => { vm.Scr.Circle((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F, false); return NoRet(); });
            Proc("__circlef", new[]{F,F,F,F},   (vm,a) => { vm.Scr.Circle((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F, true); return NoRet(); });
            Proc("__paint",   new[]{F,F,F},     (vm,a) => { vm.Scr.Paint((int)a[0].F, (int)a[1].F, (int)a[2].F); return NoRet(); });
            Proc("__cls0",    none,             (vm,a) => { vm.Scr.Cls(vm.Scr.Bg); return NoRet(); });
            Proc("__colorfg", new[]{F},         (vm,a) => { vm.Scr.Fg = (int)a[0].F & 15; return NoRet(); });
            Proc("__color",   new[]{F,F},       (vm,a) => { vm.Scr.Fg = (int)a[0].F & 15; vm.Scr.Bg = (int)a[1].F & 15; return NoRet(); });
            Proc("__locate",  new[]{F,F},       (vm,a) => { vm.Scr.Locate((int)a[0].F, (int)a[1].F); return NoRet(); });
            Proc("__palette", new[]{F,F,F,F},   (vm,a) => { vm.Scr.SetPalette((int)a[0].F, (int)a[1].F, (int)a[2].F, (int)a[3].F); return NoRet(); });
            Proc("__prints",  new[]{S},         (vm,a) => { vm.Scr.Write(vm.Strings.Get(a[0].S)); return NoRet(); });
            Proc("__printf",  new[]{F},         (vm,a) => { vm.Scr.Write(Screen.FormatNumber(a[0].F)); return NoRet(); });
            Proc("__printi",  new[]{I},         (vm,a) => { vm.Scr.Write(Screen.FormatInt(a[0].I)); return NoRet(); });
            Proc("__printnl", none,             (vm,a) => { vm.Scr.NewLine(); return NoRet(); });
            Proc("__printtab",none,             (vm,a) => { vm.Scr.WriteTab(); return NoRet(); });
            Proc("__randomize", new[]{F},       (vm,a) => { vm.Rng = new Random((int)a[0].F); return NoRet(); });
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
