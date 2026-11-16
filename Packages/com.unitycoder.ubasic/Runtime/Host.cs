using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Unitycoder.UBasic {

    /// <summary>Everything the host program lets scripts touch. Unity supplies a
    /// real implementation; the headless test harness supplies a stub.
    ///
    /// This is the polled, key-state side of input that the uBasic KEY() and
    /// MOUSEX functions read. QBasic's buffered keyboard (INKEY$, INPUT) is fed
    /// separately through VM.PushKey.</summary>
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
        /// <summary>Int = rounded to an integer, Single = any number passed
        /// as a float, Str = string. The compiler converts to these.</summary>
        public VType[] Params;
        public VType Ret;
        public bool HasRet;
        /// <summary>0: Ret as declared. 1: SINGLE, or DOUBLE if any argument
        /// is (SIN, SQR...). 2: the type of the first argument (ABS, INT).</summary>
        public int RetRule;
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

        private static HostFn Def(string name, VType[] ps, VType ret, bool hasRet, HostImpl impl) {
            HostFn fn = new HostFn();
            fn.Name = name; fn.Params = ps; fn.Ret = ret; fn.HasRet = hasRet; fn.Impl = impl;
            All.Add(fn);
            List<int> lst;
            if (!ByName.TryGetValue(name, out lst)) { lst = new List<int>(); ByName[name] = lst; }
            lst.Add(All.Count - 1);
            return fn;
        }

        private static void Fn(string name, VType[] ps, VType ret, HostImpl impl) {
            Def(name, ps, ret, true, impl);
        }
        /// <summary>Numeric function whose result is SINGLE or DOUBLE to
        /// match its arguments.</summary>
        private static void FnF(string name, VType[] ps, HostImpl impl) {
            Def(name, ps, VType.Single, true, impl).RetRule = 1;
        }
        /// <summary>Numeric function that returns its argument's own type.</summary>
        private static void FnN(string name, VType[] ps, HostImpl impl) {
            Def(name, ps, VType.Single, true, impl).RetRule = 2;
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
        private static Slot RF(double v) { return Slot.FromFloat(v); }
        private static Slot RS(VM vm, string v) { return Slot.FromStr(vm.Strings.Alloc(v)); }
        private static string Str(VM vm, Slot s) { return vm.Strings.Get(s.S); }

        private static int ToInt(double v) {
            if (v >= 2147483647.0) return int.MaxValue;
            if (v <= -2147483648.0) return int.MinValue;
            return (int)Math.Round(v);
        }

        static Host() {
            VType[] none = new VType[0];

            // ---- math ----
            FnN("ABS", new[]{F}, (vm,a) => RF(Math.Abs(a[0].F)));
            Fn ("SGN", new[]{F}, I, (vm,a) => RI(a[0].F > 0 ? 1 : (a[0].F < 0 ? -1 : 0)));
            FnN("INT", new[]{F}, (vm,a) => RF(Math.Floor(a[0].F)));
            FnN("FIX", new[]{F}, (vm,a) => RF(Math.Truncate(a[0].F)));
            Fn ("CINT", new[]{F}, I, (vm,a) => RI(ToInt(a[0].F)));
            Fn ("CLNG", new[]{F}, VType.Long, (vm,a) => RI(ToInt(a[0].F)));
            Fn ("CSNG", new[]{F}, VType.Single, (vm,a) => RF((float)a[0].F));
            Fn ("CDBL", new[]{F}, VType.Double, (vm,a) => RF(a[0].F));
            FnF("SQR",  new[]{F}, (vm,a) => RF(a[0].F <= 0 ? 0.0 : Math.Sqrt(a[0].F)));
            FnF("SIN",  new[]{F}, (vm,a) => RF(Math.Sin(a[0].F)));
            FnF("COS",  new[]{F}, (vm,a) => RF(Math.Cos(a[0].F)));
            FnF("TAN",  new[]{F}, (vm,a) => RF(Math.Tan(a[0].F)));
            FnF("ATN",  new[]{F}, (vm,a) => RF(Math.Atan(a[0].F)));
            FnF("ATN2", new[]{F,F}, (vm,a) => RF(Math.Atan2(a[0].F, a[1].F)));
            FnF("EXP",  new[]{F}, (vm,a) => RF(Math.Exp(a[0].F)));
            FnF("LOG",  new[]{F}, (vm,a) => RF(a[0].F <= 0 ? 0.0 : Math.Log(a[0].F)));
            FnF("MIN",  new[]{F,F}, (vm,a) => RF(Math.Min(a[0].F, a[1].F)));
            FnF("MAX",  new[]{F,F}, (vm,a) => RF(Math.Max(a[0].F, a[1].F)));
            Fn ("RND",  none, F, (vm,a) => RF(vm.NextRnd()));
            // uBasic: RND(n) is a whole number 0..n-1.
            Fn ("RND",  new[]{F}, F, (vm,a) => {
                int n = (int)a[0].F;
                return RF(n <= 0 ? 0.0 : vm.Rng.Next(n));
            });
            // QBasic: RND(x>0) next number, RND(0) the last one, RND(x<0) reseed.
            Fn ("__rnd", new[]{F}, F, (vm,a) => {
                if (a[0].F == 0) return RF(vm.LastRnd);
                if (a[0].F < 0) vm.Rng = new Random(Seed(a[0].F));
                return RF(vm.NextRnd());
            });

            // ---- strings ----
            Fn("LEN",  new[]{S}, I, (vm,a) => RI(Str(vm, a[0]).Length));
            Fn("CHR$", new[]{I}, S, (vm,a) => RS(vm, ((char)(a[0].I & 255)).ToString()));
            Fn("ASC",  new[]{S}, I, (vm,a) => {
                string s = Str(vm, a[0]);
                return RI(s.Length == 0 ? 0 : (s[0] & 255));
            });
            Fn("STR$", new[]{F}, S, (vm,a) => RS(vm, Screen.FormatNumber(a[0].F, false).TrimEnd()));
            Fn("__strd", new[]{F}, S, (vm,a) => RS(vm, Screen.FormatNumber(a[0].F, true).TrimEnd()));
            Fn("VAL",  new[]{S}, VType.Double, (vm,a) => RF(Val(Str(vm, a[0]))));
            Fn("LEFT$", new[]{S,I}, S, (vm,a) => {
                string s = Str(vm, a[0]);
                return RS(vm, s.Substring(0, Clamp(a[1].I, 0, s.Length)));
            });
            Fn("RIGHT$", new[]{S,I}, S, (vm,a) => {
                string s = Str(vm, a[0]);
                int n = Clamp(a[1].I, 0, s.Length);
                return RS(vm, s.Substring(s.Length - n, n));
            });
            Fn("MID$", new[]{S,I}, S, (vm,a) => {
                string s = Str(vm, a[0]);
                int st = Clamp(a[1].I - 1, 0, s.Length);      // QBasic is 1-based
                return RS(vm, s.Substring(st));
            });
            Fn("MID$", new[]{S,I,I}, S, (vm,a) => {
                string s = Str(vm, a[0]);
                int st = Clamp(a[1].I - 1, 0, s.Length);
                int n  = Clamp(a[2].I, 0, s.Length - st);
                return RS(vm, s.Substring(st, n));
            });
            // The MID$ statement: overwrite part of a string in place.
            Fn("__midset", new[]{S,I,I,S}, S, (vm,a) => {
                string s = Str(vm, a[0]), r = Str(vm, a[3]);
                int st = a[1].I - 1;
                if (st < 0 || st >= s.Length) return RS(vm, s);
                int n = a[2].I < 0 ? r.Length : Math.Min(a[2].I, r.Length);
                n = Math.Min(n, s.Length - st);
                return RS(vm, s.Substring(0, st) + r.Substring(0, n) + s.Substring(st + n));
            });
            Fn("INSTR", new[]{S,S}, I, (vm,a) => RI(Instr(1, Str(vm, a[0]), Str(vm, a[1]))));
            Fn("INSTR", new[]{I,S,S}, I, (vm,a) => RI(Instr(a[0].I, Str(vm, a[1]), Str(vm, a[2]))));
            Fn("UCASE$", new[]{S}, S, (vm,a) => RS(vm, Case(Str(vm, a[0]), true)));
            Fn("LCASE$", new[]{S}, S, (vm,a) => RS(vm, Case(Str(vm, a[0]), false)));
            Fn("LTRIM$", new[]{S}, S, (vm,a) => RS(vm, Str(vm, a[0]).TrimStart(' ')));
            Fn("RTRIM$", new[]{S}, S, (vm,a) => RS(vm, Str(vm, a[0]).TrimEnd(' ')));
            Fn("TRIM$",  new[]{S}, S, (vm,a) => RS(vm, Str(vm, a[0]).Trim()));
            Fn("SPACE$", new[]{I}, S, (vm,a) => RS(vm, new string(' ', Clamp(a[0].I, 0, 32767))));
            Fn("STRING$", new[]{I,S}, S, (vm,a) => {
                string s = Str(vm, a[1]);
                char c = s.Length > 0 ? s[0] : ' ';
                return RS(vm, new string(c, Clamp(a[0].I, 0, 32767)));
            });
            Fn("__stringc", new[]{I,I}, S, (vm,a) =>
                RS(vm, new string((char)(a[1].I & 255), Clamp(a[0].I, 0, 32767))));
            Fn("HEX$", new[]{I}, S, (vm,a) => RS(vm, a[0].I.ToString("X", CultureInfo.InvariantCulture)));
            Fn("OCT$", new[]{I}, S, (vm,a) => RS(vm, Convert.ToString(a[0].I, 8)));
            Fn("DATE$", none, S, (vm,a) => RS(vm, DateTime.Now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture)));
            Fn("TIME$", none, S, (vm,a) => RS(vm, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));

            // ---- screen queries ----
            Fn("POINT", new[]{I,I}, I, (vm,a) => RI(vm.Scr.Point(a[0].I, a[1].I)));
            Fn("SCRW",  none, I, (vm,a) => RI(vm.Scr.W));
            Fn("SCRH",  none, I, (vm,a) => RI(vm.Scr.H));
            Fn("CSRLIN", none, I, (vm,a) => RI(vm.Scr.CurRow + 1));
            Fn("POS",   new[]{I}, I, (vm,a) => RI(vm.Scr.CurCol + 1));
            Fn("TIMER", none, F, (vm,a) => RF(vm.Timer()));

            // ---- input ----
            Fn("INKEY$", none, S, (vm,a) => RS(vm, vm.Inkey()));
            Fn("KEY",    new[]{I}, I, (vm,a) => RI(vm.Input.Held(a[0].I) ? -1 : 0));
            Fn("KEYHIT", new[]{I}, I, (vm,a) => RI(vm.Input.Hit(a[0].I) ? -1 : 0));
            Fn("MOUSEX", none, I, (vm,a) => RI(vm.Input.MouseX));
            Fn("MOUSEY", none, I, (vm,a) => RI(vm.Input.MouseY));
            Fn("MOUSEB", new[]{I}, I, (vm,a) => RI(vm.Input.MouseButton(a[0].I) ? -1 : 0));

            // ---- DOS leftovers: accepted so old programs compile ----
            Fn("PEEK", new[]{I}, I, (vm,a) => RI(0));
            Fn("INP",  new[]{I}, I, (vm,a) => RI(0));
            Fn("FRE",  new[]{F}, VType.Long, (vm,a) => RI(655360));

            // ---- statement primitives (emitted by the compiler, not callable) ----
            Proc("__cls",     new[]{I},   (vm,a) => { vm.Scr.Cls(a[0].I); return NoRet(); });
            Proc("__cls0",    none,       (vm,a) => { vm.Scr.Cls(vm.Scr.Bg); return NoRet(); });
            Proc("__clsq",    new[]{I},   (vm,a) => { vm.Scr.ClsQB(a[0].I == 2); return NoRet(); });
            Proc("__screen1", new[]{I},   (vm,a) => { SetMode(vm, a[0].I); return NoRet(); });
            Proc("__screen",  new[]{I,I}, (vm,a) => {
                // SCREEN w, h is the uBasic free-size screen; SCREEN mode,
                // colorswitch is QBasic. No QBasic mode number reaches 32.
                if (a[0].I >= 32) vm.Scr.Resize(a[0].I, a[1].I);
                else SetMode(vm, a[0].I);
                return NoRet();
            });
            Proc("__width",   new[]{I,I}, (vm,a) => { vm.Scr.SetTextSize(a[0].I, a[1].I); return NoRet(); });
            Proc("__viewprint", new[]{I,I}, (vm,a) => {
                if (a[0].I <= 0) vm.Scr.SetTextView(-1, -1);
                else vm.Scr.SetTextView(a[0].I - 1, a[1].I - 1);
                return NoRet();
            });
            Proc("__pset", new[]{I,I,I,I}, (vm,a) => {
                Screen s = vm.Scr;
                int x = a[0].I, y = a[1].I, fl = a[3].I;
                if ((fl & 1) != 0) { x += s.LastX; y += s.LastY; }
                int c = a[2].I >= 0 ? a[2].I : ((fl & 2) != 0 ? 0 : s.Fg);
                s.PSet(x, y, c);
                s.LastX = x; s.LastY = y;
                return NoRet();
            });
            Proc("__line", new[]{I,I,I,I,I,I}, (vm,a) => {
                Screen s = vm.Scr;
                int fl = a[5].I;
                int x0 = a[0].I, y0 = a[1].I, x1 = a[2].I, y1 = a[3].I;
                if ((fl & 4) != 0) { x0 = s.LastX; y0 = s.LastY; }
                else if ((fl & 1) != 0) { x0 += s.LastX; y0 += s.LastY; }
                if ((fl & 2) != 0) { x1 += x0; y1 += y0; }
                int c = a[4].I >= 0 ? a[4].I : s.Fg;
                if ((fl & 16) != 0) s.BoxFill(x0, y0, x1, y1, c);
                else if ((fl & 8) != 0) s.Box(x0, y0, x1, y1, c);
                else s.Line(x0, y0, x1, y1, c);
                s.LastX = x1; s.LastY = y1;
                return NoRet();
            });
            Proc("__circle", new[]{I,I,F,I,F,F,F,I}, (vm,a) => {
                Screen s = vm.Scr;
                int x = a[0].I, y = a[1].I, fl = a[7].I;
                if ((fl & 1) != 0) { x += s.LastX; y += s.LastY; }
                int c = a[3].I >= 0 ? a[3].I : s.Fg;
                if ((fl & 16) != 0) s.Circle(x, y, (int)Math.Round(a[2].F), c, true);
                else s.Ellipse(x, y, a[2].F, c, (fl & 2) != 0, a[4].F, (fl & 4) != 0, a[5].F,
                               (fl & 8) != 0 ? a[6].F : 0.0);
                s.LastX = x; s.LastY = y;
                return NoRet();
            });
            Proc("__paintu", new[]{I,I,I}, (vm,a) => { vm.Scr.Paint(a[0].I, a[1].I, a[2].I); return NoRet(); });
            Proc("__paint", new[]{I,I,I,I,I}, (vm,a) => {
                Screen s = vm.Scr;
                int x = a[0].I, y = a[1].I, fl = a[4].I;
                if ((fl & 1) != 0) { x += s.LastX; y += s.LastY; }
                int c = a[2].I >= 0 ? a[2].I : s.Fg;
                s.PaintBorder(x, y, c, (fl & 2) != 0 ? a[3].I : c);
                s.LastX = x; s.LastY = y;
                return NoRet();
            });
            Proc("__get", new[]{I,I,I,I,I,I}, (vm,a) => { GetImage(vm, a); return NoRet(); });
            Proc("__put", new[]{I,I,I,I,I},   (vm,a) => { PutImage(vm, a); return NoRet(); });
            Proc("__color", new[]{I,I}, (vm,a) => { Color(vm, a[0].I, a[1].I); return NoRet(); });
            Proc("__locate", new[]{I,I}, (vm,a) => {
                Screen s = vm.Scr;
                int b = vm.QBasic ? 1 : 0;
                // A negative argument is one that was left out: keep it.
                s.Locate(a[0].I < 0 ? s.CurRow : a[0].I - b, a[1].I < 0 ? s.CurCol : a[1].I - b);
                return NoRet();
            });
            Proc("__palette", new[]{I,I,I,I}, (vm,a) => { vm.Scr.SetPalette(a[0].I, a[1].I, a[2].I, a[3].I); return NoRet(); });
            Proc("__palq",    new[]{I,I},     (vm,a) => { vm.Scr.SetPaletteQB(a[0].I, a[1].I); return NoRet(); });
            Proc("__palreset", none,          (vm,a) => { vm.Scr.ResetPalette(); return NoRet(); });

            Proc("__prints",  new[]{S}, (vm,a) => { vm.Scr.Write(Str(vm, a[0])); return NoRet(); });
            Proc("__printf",  new[]{F}, (vm,a) => { vm.Scr.Write(Screen.FormatNumber(a[0].F, false)); return NoRet(); });
            Proc("__printd",  new[]{F}, (vm,a) => { vm.Scr.Write(Screen.FormatNumber(a[0].F, true)); return NoRet(); });
            Proc("__printi",  new[]{I}, (vm,a) => { vm.Scr.Write(Screen.FormatInt(a[0].I)); return NoRet(); });
            Proc("__printnl", none,     (vm,a) => { vm.Scr.NewLine(); return NoRet(); });
            Proc("__printtab",none,     (vm,a) => { vm.Scr.WriteTab(); return NoRet(); });
            Proc("__tab",     new[]{I}, (vm,a) => { vm.Scr.TabTo(a[0].I); return NoRet(); });
            Proc("__spc",     new[]{I}, (vm,a) => { vm.Scr.WriteSpaces(Clamp(a[0].I, 0, 4096)); return NoRet(); });

            Proc("__usingfmt", new[]{S}, (vm,a) => { vm.UsingFmt = Str(vm, a[0]); vm.UsingPos = 0; return NoRet(); });
            Proc("__usingnum", new[]{F}, (vm,a) => { UsingNum(vm, a[0].F); return NoRet(); });
            Proc("__usingstr", new[]{S}, (vm,a) => { UsingStr(vm, Str(vm, a[0])); return NoRet(); });
            Proc("__usingend", none,     (vm,a) => {
                StringBuilder sb = new StringBuilder();
                UsingLiterals(vm, sb);
                vm.Scr.Write(sb.ToString());
                return NoRet();
            });

            Fn("__inpline", none, S, (vm,a) => RS(vm, vm.InputText));
            Fn("__inpfield_s", new[]{I,I}, S, (vm,a) =>
                RS(vm, a[1].I <= 1 ? vm.InputText : InputField(vm.InputText, a[0].I)));
            Fn("__inpfield_f", new[]{I}, VType.Double, (vm,a) => RF(Val(InputField(vm.InputText, a[0].I))));

            Fn("__readf", none, VType.Double, (vm,a) => RF(Val(vm.ReadData())));
            Fn("__reads", none, S, (vm,a) => RS(vm, vm.ReadData()));
            Proc("__restore", new[]{I}, (vm,a) => { vm.RestoreData(a[0].I); return NoRet(); });

            Proc("__sound",   new[]{F,F}, (vm,a) => { vm.Sound.Sound((float)a[0].F, (float)a[1].F); return NoRet(); });
            Proc("__beep",    none,       (vm,a) => { vm.Sound.Beep(); return NoRet(); });
            Proc("__play",    new[]{S},   (vm,a) => { vm.Sound.Play(Str(vm, a[0])); return NoRet(); });
            Proc("__randomize", new[]{F}, (vm,a) => { vm.Rng = new Random(Seed(a[0].F)); return NoRet(); });
            Proc("__randomize0", none,    (vm,a) => { vm.Rng = new Random(Seed(vm.Timer())); return NoRet(); });
            Proc("__sleep",   new[]{F},   (vm,a) => { vm.SleepFor(a[0].F); return NoRet(); });
            Proc("__erase",   new[]{I},   (vm,a) => {
                ArrayObj arr = vm.GetArray(a[0].I);
                Array.Clear(arr.Data, 0, arr.Data.Length);
                return NoRet();
            });
            Proc("__resume",  none, (vm,a) => { throw vm.Error("RESUME without an error (ON ERROR is not supported)"); });
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private static int Seed(double v) {
            long bits = BitConverter.DoubleToInt64Bits(v);
            return unchecked((int)(bits ^ (bits >> 32)));
        }

        private static void SetMode(VM vm, int mode) {
            if (!vm.Scr.SetMode(mode))
                throw vm.Error("SCREEN " + mode + " is not supported (use 0, 1, 2, 7, 8, 9, 11, 12 or 13)");
        }

        /// <summary>COLOR means something different in each kind of mode.</summary>
        private static void Color(VM vm, int fg, int bg) {
            Screen s = vm.Scr;
            switch (s.Mode) {
                case Screen.CustomMode:
                    if (fg >= 0) s.Fg = fg & 15;
                    if (bg >= 0) s.Bg = bg & 15;
                    break;
                case 0:
                    if (fg >= 0) s.Fg = fg & 15;      // 16..31 would blink
                    if (bg >= 0) s.Bg = bg & 7;
                    break;
                case 1:
                    // COLOR background, palette. Only the background is kept.
                    if (fg >= 0) s.SetPaletteQB(0, fg);
                    break;
                case 7: case 8: case 9:
                    // The second argument recolours the background attribute.
                    if (fg >= 0) s.Fg = fg & s.ColorMask;
                    if (bg >= 0) s.SetPaletteQB(0, bg);
                    break;
                default:
                    if (fg >= 0) s.Fg = fg & s.ColorMask;
                    break;
            }
        }

        private static string Case(string s, bool upper) {
            // ASCII only: characters above 127 are CP437 codes, not letters.
            char[] c = null;
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                bool hit = upper ? (ch >= 'a' && ch <= 'z') : (ch >= 'A' && ch <= 'Z');
                if (!hit) continue;
                if (c == null) c = s.ToCharArray();
                c[i] = (char)(upper ? ch - 32 : ch + 32);
            }
            return c == null ? s : new string(c);
        }

        private static int Instr(int start, string s, string find) {
            if (start < 1) start = 1;
            if (start > s.Length) return 0;
            if (find.Length == 0) return start;
            return s.IndexOf(find, start - 1, StringComparison.Ordinal) + 1;
        }

        /// <summary>VAL: the leading number of a string, 0 if there is none.
        /// Unlike a strict parse it stops quietly at the first character that
        /// does not belong, so VAL("12 apples") is 12.</summary>
        public static double Val(string s) {
            int i = 0, n = s.Length;
            while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i + 1 < n && s[i] == '&') {
                char r = char.ToUpperInvariant(s[i + 1]);
                if (r == 'H' || r == 'O') {
                    int j = i + 2;
                    long v = 0;
                    int radix = r == 'H' ? 16 : 8;
                    while (j < n) {
                        int d = HexVal(s[j]);
                        if (d < 0 || d >= radix) break;
                        v = v * radix + d;
                        if (v > 0xFFFFFFFFL) break;
                        j++;
                    }
                    return v <= 0xFFFF ? (short)v : (int)v;
                }
            }
            StringBuilder sb = new StringBuilder();
            if (i < n && (s[i] == '+' || s[i] == '-')) sb.Append(s[i++]);
            bool digits = false, dot = false;
            while (i < n) {
                char c = s[i];
                if (c >= '0' && c <= '9') { sb.Append(c); digits = true; }
                else if (c == '.' && !dot) { sb.Append(c); dot = true; }
                else if (c == ' ') { }           // QBasic skips blanks inside
                else break;
                i++;
            }
            if (!digits) return 0;
            if (i < n && (s[i] == 'E' || s[i] == 'e' || s[i] == 'D' || s[i] == 'd')) {
                int j = i + 1;
                StringBuilder ex = new StringBuilder("E");
                if (j < n && (s[j] == '+' || s[j] == '-')) ex.Append(s[j++]);
                int k = j;
                while (j < n && s[j] >= '0' && s[j] <= '9') ex.Append(s[j++]);
                if (j > k) sb.Append(ex);
            }
            double r2;
            double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out r2);
            return r2;
        }

        private static int HexVal(char c) {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>Field n of a typed INPUT line: comma separated, optionally
        /// quoted, surrounding blanks dropped.</summary>
        private static string InputField(string line, int n) {
            int i = 0, len = line.Length, field = 0;
            while (true) {
                while (i < len && line[i] == ' ') i++;
                StringBuilder sb = new StringBuilder();
                if (i < len && line[i] == '"') {
                    i++;
                    while (i < len && line[i] != '"') sb.Append(line[i++]);
                    while (i < len && line[i] != ',') i++;
                } else {
                    while (i < len && line[i] != ',') sb.Append(line[i++]);
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                }
                if (field == n) return sb.ToString();
                if (i >= len) return "";
                i++; field++;
            }
        }

        // ---- GET / PUT -------------------------------------------------------

        [StructLayout(LayoutKind.Explicit)]
        private struct F32 {
            [FieldOffset(0)] public int I;
            [FieldOffset(0)] public float F;
        }

        private static byte[] _scratch = new byte[1024];

        /// <summary>Bytes one array element holds in real QBasic. GET and PUT
        /// address an array as that byte stream, so image data moves between
        /// here and DOS unchanged.</summary>
        private static int ElemBytes(VM vm, ArrayObj arr) {
            switch (arr.Info.Type) {
                case VType.Int: return 2;
                case VType.Long: case VType.Single: return 4;
                case VType.Double: return 8;
            }
            throw vm.Error("GET/PUT need a numeric array");
        }

        private static int FirstElem(ArrayObj arr, int idx) {
            if (idx == int.MinValue) return 0;
            return idx - arr.Lo[0];
        }

        private static void GetImage(VM vm, Slot[] a) {
            ArrayObj arr = vm.GetArray(a[4].I);
            int eb = ElemBytes(vm, arr);
            int first = FirstElem(arr, a[5].I);
            byte[] d = vm.Scr.GetImage(a[0].I, a[1].I, a[2].I, a[3].I);
            int need = (d.Length + eb - 1) / eb;
            if (first < 0 || first + need > arr.Count)
                throw vm.Error("GET: array " + arr.Info.Name + " is too small (needs " +
                               need + " elements, has " + (arr.Count - Math.Max(0, first)) + ")");
            Slot[] data = arr.Data;
            for (int e = 0; e < need; e++) {
                int o = e * eb;
                long v = 0;
                for (int k = eb - 1; k >= 0; k--) v = (v << 8) | (o + k < d.Length ? d[o + k] : (byte)0);
                Slot s = new Slot();
                switch (arr.Info.Type) {
                    case VType.Int: s.I = (short)v; break;
                    case VType.Long: s.I = (int)v; break;
                    case VType.Single: { F32 f = new F32(); f.I = (int)v; s.F = f.F; break; }
                    default: s.F = BitConverter.Int64BitsToDouble(v); break;
                }
                data[first + e] = s;
            }
        }

        private static void PutImage(VM vm, Slot[] a) {
            ArrayObj arr = vm.GetArray(a[2].I);
            int eb = ElemBytes(vm, arr);
            int first = FirstElem(arr, a[3].I);
            if (first < 0 || first >= arr.Count) throw vm.Error("PUT: bad array index");
            int bytes = (arr.Count - first) * eb;
            if (bytes > 1 << 22) bytes = 1 << 22;
            if (_scratch.Length < bytes) _scratch = new byte[bytes];
            byte[] d = _scratch;
            Slot[] data = arr.Data;
            int count = bytes / eb;
            for (int e = 0; e < count; e++) {
                Slot s = data[first + e];
                long v;
                switch (arr.Info.Type) {
                    case VType.Int: case VType.Long: v = s.I; break;
                    case VType.Single: { F32 f = new F32(); f.F = (float)s.F; v = f.I; break; }
                    default: v = BitConverter.DoubleToInt64Bits(s.F); break;
                }
                int o = e * eb;
                for (int k = 0; k < eb; k++) { d[o + k] = (byte)v; v >>= 8; }
            }
            // Anything past the image in the scratch buffer is stale, so the
            // size check must be against what this array really holds.
            if (bytes < 4) throw vm.Error("PUT: array holds no image");
            int unit = vm.Scr.ImageFormat == ImageFormat.Byte8 ? 8 :
                       vm.Scr.ImageFormat == ImageFormat.Packed2 ? 2 : 1;
            int w = (d[0] | (d[1] << 8)) / unit, h = d[2] | (d[3] << 8);
            if (w <= 0 || h <= 0 || vm.Scr.ImageSize(w, h) > bytes || !vm.Scr.PutImage(a[0].I, a[1].I, d, a[4].I))
                throw vm.Error("PUT: array " + arr.Info.Name + " does not hold an image for this screen mode");
        }

        // ---- PRINT USING -------------------------------------------------------

        private static bool FieldStart(string f, int i) {
            char c = f[i];
            char n = i + 1 < f.Length ? f[i + 1] : '\0';
            if (c == '#' || c == '&' || c == '!' || c == '\\') return true;
            if (c == '.' && n == '#') return true;
            if (c == '+' && (n == '#' || n == '.' || n == '*' || n == '$')) return true;
            if (c == '*' && n == '*') return true;
            if (c == '$' && n == '$') return true;
            return false;
        }

        private static void UsingLiterals(VM vm, StringBuilder sb) {
            string f = vm.UsingFmt ?? "";
            while (vm.UsingPos < f.Length && !FieldStart(f, vm.UsingPos)) {
                char c = f[vm.UsingPos++];
                if (c == '_' && vm.UsingPos < f.Length) c = f[vm.UsingPos++];
                sb.Append(c);
            }
        }

        /// <summary>Advance to the next field, printing the literal text in
        /// between. When the format runs out it starts over, as in QBasic.</summary>
        private static void UsingSeek(VM vm, StringBuilder sb) {
            string f = vm.UsingFmt ?? "";
            UsingLiterals(vm, sb);
            if (vm.UsingPos >= f.Length) {
                vm.UsingPos = 0;
                UsingLiterals(vm, sb);
                if (vm.UsingPos >= f.Length) throw vm.Error("PRINT USING: the format has no # or & field");
            }
        }

        private static void UsingStr(VM vm, string s) {
            StringBuilder sb = new StringBuilder();
            UsingSeek(vm, sb);
            string f = vm.UsingFmt;
            char c = f[vm.UsingPos];
            if (c == '&') { vm.UsingPos++; sb.Append(s); }
            else if (c == '!') { vm.UsingPos++; sb.Append(s.Length > 0 ? s[0] : ' '); }
            else if (c == '\\') {
                int close = f.IndexOf('\\', vm.UsingPos + 1);
                if (close < 0) close = f.Length - 1;
                int w = close - vm.UsingPos + 1;
                sb.Append(s.Length >= w ? s.Substring(0, w) : s.PadRight(w));
                vm.UsingPos = close + 1;
            } else throw vm.Error("PRINT USING: a string was given for a numeric field");
            vm.Scr.Write(sb.ToString());
        }

        private static void UsingNum(VM vm, double v) {
            StringBuilder sb = new StringBuilder();
            UsingSeek(vm, sb);
            string f = vm.UsingFmt;
            int i = vm.UsingPos, n = f.Length;
            if (f[i] == '&' || f[i] == '!' || f[i] == '\\')
                throw vm.Error("PRINT USING: a number was given for a string field");

            bool plusLead = false, star = false, dollar = false, comma = false;
            bool plusTrail = false, minusTrail = false, exp = false;
            int dec = 0;
            bool hasDot = false;
            if (f[i] == '+') { plusLead = true; i++; }
            if (i + 1 < n && f[i] == '*' && f[i + 1] == '*') {
                star = true; i += 2;
                if (i < n && f[i] == '$') { dollar = true; i++; }
            } else if (i + 1 < n && f[i] == '$' && f[i + 1] == '$') { dollar = true; i += 2; }
            while (i < n && (f[i] == '#' || f[i] == ',')) { if (f[i] == ',') comma = true; i++; }
            if (i < n && f[i] == '.') {
                hasDot = true; i++;
                while (i < n && f[i] == '#') { dec++; i++; }
            }
            if (i + 3 < n && f[i] == '^' && f[i + 1] == '^' && f[i + 2] == '^' && f[i + 3] == '^') { exp = true; i += 4; }
            if (i < n && f[i] == '+') { plusTrail = true; i++; }
            else if (i < n && f[i] == '-') { minusTrail = true; i++; }
            int width = i - vm.UsingPos;
            vm.UsingPos = i;

            bool neg = v < 0;
            double av = Math.Abs(v);
            string body;
            if (exp) {
                body = av.ToString((hasDot ? "0." + new string('0', dec) : "0") + "E+00", CultureInfo.InvariantCulture);
            } else {
                body = av.ToString("F" + dec, CultureInfo.InvariantCulture);
                if (hasDot && dec == 0) body += ".";
                if (comma) {
                    int end = body.IndexOf('.');
                    if (end < 0) end = body.Length;
                    for (int k = end - 3; k > 0; k -= 3) body = body.Insert(k, ",");
                }
            }
            if (dollar) body = "$" + body;
            if (plusLead) body = (neg ? "-" : "+") + body;
            else if (neg && !plusTrail && !minusTrail) body = "-" + body;
            if (plusTrail) body += neg ? "-" : "+";
            else if (minusTrail) body += neg ? "-" : " ";

            if (body.Length > width) sb.Append('%').Append(body);
            else sb.Append(new string(star ? '*' : ' ', width - body.Length)).Append(body);
            vm.Scr.Write(sb.ToString());
        }
    }
}
