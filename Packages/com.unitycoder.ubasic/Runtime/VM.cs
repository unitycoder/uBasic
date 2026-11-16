using System;
using System.Collections.Generic;
using System.Text;

namespace Unitycoder.UBasic {

    public enum RunState {
        /// <summary>Budget ran out mid-statement. Call Run again next frame.</summary>
        Yielded,
        /// <summary>Hit a WAIT, or is blocked on a key, a sound or a SLEEP.
        /// The program wants the next frame.</summary>
        Waiting,
        Halted,
        Faulted
    }

    /// <summary>The uBasic virtual machine.
    ///
    /// Interpreter state is (pc, operand stack, locals arena, frame stack) and
    /// nothing else, so suspending mid-program is just returning from Run.
    /// That is what makes WAIT, INPUT, per-frame fuel limits and save/restore
    /// fall out for free rather than needing continuations.</summary>
    public class VM {
        private struct Frame {
            public int RetPc;
            public int LocalsBase;
            public int FuncIdx;
        }

        private readonly Chunk _c;
        private Instr[] _code;
        private int[] _constHandles;
        private double[] _floats;

        private Slot[] _stack = new Slot[256];
        private int _sp;
        private Slot[] _globals;
        private Slot[] _locals = new Slot[1024];
        private int _localsTop;
        private Frame[] _frames = new Frame[64];
        private int _fc;
        private int[] _gosub = new int[64];
        private int _gc;
        private readonly Slot[] _argBuf = new Slot[8];

        // Arrays live in a handle table; handle 0 means "not dimensioned".
        private readonly List<ArrayObj> _arrs = new List<ArrayObj>();
        private readonly Stack<int> _arrFree = new Stack<int>();

        private int _pc;

        public readonly StringHeap Strings = new StringHeap();
        public Screen Scr;
        public readonly SoundDevice Sound = new SoundDevice();
        public IInput Input = new NullInput();
        public Random Rng = new Random(12345);
        public double LastRnd;

        /// <summary>Host clock in seconds, set once per frame before Run.</summary>
        public double Time;
        /// <summary>Expected length of a frame in seconds. Used to spread the
        /// instructions of one Run across the frame for TIMER.</summary>
        public double FrameDt = 1.0 / 60.0;
        /// <summary>Added to Time to make TIMER read as seconds since midnight.</summary>
        public double TimerBase;

        /// <summary>A loop that does nothing but read TIMER is a delay or a
        /// speed-calibration loop. Each such read is charged 1/N of the frame's
        /// instruction budget, which pins the loop to about N*fps polls per
        /// second -- the speed of the machines these programs were tuned on.
        /// 0 turns the throttle off.</summary>
        public int TimerPollsPerFrame = 16;
        /// <summary>When INKEY$ finds no key and was also called only a moment
        /// ago, the program is spinning on the keyboard: end the frame instead
        /// of burning the rest of the budget.</summary>
        public bool YieldOnIdleInkey = true;

        public string FaultMessage { get; private set; }
        public int FaultLine { get; private set; }
        public bool Finished { get { return _pc < 0; } }
        public bool QBasic { get { return _c.QBasic; } }

        /// <summary>Collect once the heap grows past this many live strings.</summary>
        public int GcThreshold = 512;

        // ---- keyboard buffer, INPUT, DATA, SLEEP, PRINT USING state ----
        private readonly Queue<string> _keys = new Queue<string>();
        private bool _inputActive;
        private readonly StringBuilder _inputSb = new StringBuilder();
        public string InputText = "";
        private int _dataPos;
        private double _sleepUntil;
        public string UsingFmt;
        public int UsingPos;

        // ---- virtual clock ----
        private int _runBudget = 1, _budget;
        private long _instrBase, _lastTimerRead = long.MinValue / 2, _lastInkey = long.MinValue / 2;
        private double _lastTimer;
        private bool _yield;

        public VM(Chunk c, Screen screen) {
            _c = c;
            Scr = screen;
            Reset();
        }

        public void Reset() {
            _code = _c.Code.ToArray();
            _floats = _c.FloatConsts.ToArray();
            _globals = new Slot[Math.Max(1, _c.GlobalTypes.Count)];
            _arrs.Clear(); _arrFree.Clear();
            _arrs.Add(null);

            _constHandles = new int[_c.StringConsts.Count];
            for (int i = 0; i < _c.StringConsts.Count; i++)
                _constHandles[i] = Strings.AllocPinned(_c.StringConsts[i]);

            Sound.Reset();
            _keys.Clear();
            _inputActive = false;
            _dataPos = 0;
            _sp = 0; _fc = 0; _gc = 0; _localsTop = 0;
            _pc = _c.EntryPoint;
            FaultMessage = null; FaultLine = 0;
            TimerBase = DateTime.Now.TimeOfDay.TotalSeconds;

            // A classic program starts on the 80x25 text screen.
            if (_c.QBasic && Scr != null) Scr.SetMode(0);
        }

        private void Push(Slot s) {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = s;
        }
        private Slot Pop() { return _stack[--_sp]; }

        private UBasicError Fault(string msg) {
            int line = (_pc > 0 && _pc - 1 < _code.Length) ? _code[_pc - 1].Line : 0;
            return new UBasicError(msg, line);
        }

        /// <summary>For host functions: a runtime error at the line of the
        /// statement that made the call.</summary>
        public UBasicError Error(string msg) { return Fault(msg); }

        // ---- keyboard --------------------------------------------------------

        /// <summary>Queue a key press for INKEY$ / INPUT. Use a one-character
        /// string for ordinary keys (CHR$(13) for Enter, CHR$(8) for
        /// Backspace, CHR$(27) for Esc) and CHR$(0) + scan code for extended
        /// keys, exactly what INKEY$ returns in QBasic.</summary>
        public void PushKey(string key) {
            if (string.IsNullOrEmpty(key) || _keys.Count >= 64) return;
            _keys.Enqueue(key);
        }
        public void PushKey(char c) { PushKey(c.ToString()); }
        /// <summary>Queue an extended key by PC scan code: 72 up, 80 down,
        /// 75 left, 77 right, 59..68 F1..F10.</summary>
        public void PushExtendedKey(int scanCode) { PushKey("\0" + (char)scanCode); }
        public int KeysWaiting { get { return _keys.Count; } }

        private long InstrNow { get { return _instrBase + (_runBudget - _budget); } }

        public string Inkey() {
            long now = InstrNow;
            long since = now - _lastInkey;
            _lastInkey = now;
            if (_keys.Count > 0) return _keys.Dequeue();
            if (YieldOnIdleInkey && since < 20000) _yield = true;
            return "";
        }

        // ---- clock -----------------------------------------------------------

        /// <summary>TIMER: seconds since midnight. Within one Run the clock
        /// advances with the instructions executed, so a program that times
        /// its own busy loops gets a consistent answer.</summary>
        public double Timer() {
            long now = InstrNow;
            double frac = (double)(_runBudget - _budget) / _runBudget;
            if (frac > 1) frac = 1;
            double t = TimerBase + Time + frac * FrameDt;
            if (t < _lastTimer) t = _lastTimer;
            _lastTimer = t;
            if (TimerPollsPerFrame > 0 && now - _lastTimerRead < 64)
                _budget -= Math.Max(1, _runBudget / TimerPollsPerFrame);
            _lastTimerRead = InstrNow;
            return t;
        }

        public double NextRnd() { LastRnd = (float)Rng.NextDouble(); if (LastRnd >= 1) LastRnd = 0; return LastRnd; }

        public void SleepFor(double seconds) {
            _sleepUntil = seconds > 0 ? Time + seconds : double.PositiveInfinity;
        }

        // ---- DATA ------------------------------------------------------------

        public string ReadData() {
            if (_dataPos >= _c.Data.Count) throw Fault("out of DATA");
            return _c.Data[_dataPos++];
        }
        public void RestoreData(int index) {
            _dataPos = index < 0 ? 0 : index;
        }

        // ---- arrays ----------------------------------------------------------

        public ArrayObj GetArray(int handle) {
            if (handle <= 0 || handle >= _arrs.Count || _arrs[handle] == null)
                throw Fault("array used before DIM");
            return _arrs[handle];
        }

        private int NewArray(ArrayInfo info, int[] lo, int[] len) {
            long count = 1;
            for (int i = 0; i < len.Length; i++) count *= len[i];
            if (count * info.Stride > 16 * 1024 * 1024) throw Fault("array " + info.Name + " is too large");
            ArrayObj a = new ArrayObj();
            a.Info = info; a.Lo = lo; a.Len = len; a.Stride = info.Stride;
            a.Count = (int)count;
            a.Data = new Slot[Math.Max(1, (int)count * info.Stride)];
            int h;
            if (_arrFree.Count > 0) { h = _arrFree.Pop(); _arrs[h] = a; }
            else { h = _arrs.Count; _arrs.Add(a); }
            return h;
        }

        private void FreeArray(int h) {
            if (h <= 0 || h >= _arrs.Count || _arrs[h] == null) return;
            _arrs[h] = null;
            _arrFree.Push(h);
        }

        /// <summary>Run at most <paramref name="budget"/> instructions.</summary>
        public RunState Run(int budget) {
            if (_pc < 0) return RunState.Halted;
            if (budget < 1) budget = 1;
            _runBudget = budget;
            _budget = budget;
            RunState st;
            try {
                st = Loop(budget);
            } catch (UBasicError e) {
                FaultMessage = e.Message;
                FaultLine = e.Line;
                _pc = -1;
                st = RunState.Faulted;
            }
            _instrBase += budget;
            _budget = budget;     // between runs, "now" is the start of the next one
            if (st != RunState.Yielded) {
                // The program gave the frame back, so it is not busy-waiting.
                _lastTimerRead = long.MinValue / 2;
            }
            return st;
        }

        private RunState Loop(int budget) {
            Instr[] code = _code;
            int pc = _pc;

            while (budget-- > 0) {
                Instr ins = code[pc++];
                switch (ins.Op) {
                    case Op.Nop: break;

                    case Op.PushI: Push(Slot.FromInt(ins.A)); break;
                    case Op.PushF: Push(Slot.FromFloat(_floats[ins.A])); break;
                    case Op.PushS: Push(Slot.FromStr(_constHandles[ins.A])); break;
                    case Op.Pop: _sp--; break;

                    case Op.LoadG: Push(_globals[ins.A]); break;
                    case Op.StoreG: _globals[ins.A] = Pop(); break;
                    case Op.LoadL: Push(_locals[CurBase + ins.A]); break;
                    case Op.StoreL: _locals[CurBase + ins.A] = Pop(); break;
                    case Op.LoadRet: Push(_locals[_localsTop + ins.A]); break;

                    case Op.ArrNew: {
                        int n = ins.B;
                        int[] lo = new int[n], len = new int[n];
                        for (int k = n - 1; k >= 0; k--) {
                            int hi = Pop().I;
                            lo[k] = Pop().I;
                            if (hi < lo[k] - 1) { _pc = pc; throw Fault("bad array bounds (" + lo[k] + " TO " + hi + ")"); }
                            len[k] = hi - lo[k] + 1;
                        }
                        _pc = pc;
                        Push(Slot.FromInt(NewArray(_c.Arrays[ins.A], lo, len)));
                        break;
                    }
                    case Op.ArrFree: FreeArray(Pop().I); break;

                    case Op.ArrLoad: {
                        int n = ins.B;
                        _sp -= n + 1;
                        int at = ElemIndex(n, pc);
                        ArrayObj a = _arrs[_stack[_sp].I];
                        _stack[_sp++] = a.Data[at * a.Stride + ins.A];
                        break;
                    }
                    case Op.ArrStore: {
                        int n = ins.B;
                        Slot v = _stack[--_sp];
                        _sp -= n + 1;
                        int at = ElemIndex(n, pc);
                        ArrayObj a = _arrs[_stack[_sp].I];
                        a.Data[at * a.Stride + ins.A] = v;
                        break;
                    }
                    case Op.ArrBound: {
                        int dim = Pop().I;
                        _pc = pc;
                        ArrayObj a = GetArray(Pop().I);
                        if (dim < 1 || dim > a.Lo.Length) throw Fault("bad dimension for LBOUND/UBOUND");
                        Push(Slot.FromInt(ins.A == 0 ? a.Lo[dim - 1] : a.Lo[dim - 1] + a.Len[dim - 1] - 1));
                        break;
                    }

                    case Op.AddI: { int b = Pop().I; _stack[_sp-1].I += b; break; }
                    case Op.SubI: { int b = Pop().I; _stack[_sp-1].I -= b; break; }
                    case Op.MulI: { int b = Pop().I; _stack[_sp-1].I *= b; break; }
                    case Op.DivI: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("division by zero"); } _stack[_sp-1].I /= b; break; }
                    case Op.IDiv: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("integer division by zero"); } _stack[_sp-1].I = b == -1 ? -_stack[_sp-1].I : _stack[_sp-1].I / b; break; }
                    case Op.ModI: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("MOD by zero"); } _stack[_sp-1].I = b == -1 ? 0 : _stack[_sp-1].I % b; break; }
                    case Op.NegI: _stack[_sp-1].I = -_stack[_sp-1].I; break;

                    case Op.AddF: { double b = Pop().F; _stack[_sp-1].F += b; break; }
                    case Op.SubF: { double b = Pop().F; _stack[_sp-1].F -= b; break; }
                    case Op.MulF: { double b = Pop().F; _stack[_sp-1].F *= b; break; }
                    case Op.DivF: { double b = Pop().F; if (b == 0.0) { _pc = pc; throw Fault("division by zero"); } _stack[_sp-1].F /= b; break; }
                    case Op.NegF: _stack[_sp-1].F = -_stack[_sp-1].F; break;
                    case Op.PowF: { double b = Pop().F; _stack[_sp-1].F = Math.Pow(_stack[_sp-1].F, b); break; }
                    case Op.PowI: { int b = Pop().I; _stack[_sp-1].I = (int)Math.Pow(_stack[_sp-1].I, b); break; }

                    case Op.Concat: {
                        int bh = Pop().S, ah = Pop().S;
                        Push(Slot.FromStr(Strings.Alloc(Strings.Get(ah) + Strings.Get(bh))));
                        break;
                    }

                    // Float to integer rounds to nearest (ties to even), as
                    // QBasic does on every assignment to an integer variable.
                    case Op.I2F: _stack[_sp-1].F = _stack[_sp-1].I; break;
                    case Op.F2I: _stack[_sp-1].I = RoundToInt(_stack[_sp-1].F); break;
                    case Op.I2FUnder: _stack[_sp-2].F = _stack[_sp-2].I; break;
                    case Op.F2IUnder: _stack[_sp-2].I = RoundToInt(_stack[_sp-2].F); break;

                    case Op.EqI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I == b ? -1 : 0; break; }
                    case Op.NeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I != b ? -1 : 0; break; }
                    case Op.LtI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I <  b ? -1 : 0; break; }
                    case Op.LeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I <= b ? -1 : 0; break; }
                    case Op.GtI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I >  b ? -1 : 0; break; }
                    case Op.GeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I >= b ? -1 : 0; break; }

                    case Op.EqF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F == b ? -1 : 0; break; }
                    case Op.NeF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F != b ? -1 : 0; break; }
                    case Op.LtF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F <  b ? -1 : 0; break; }
                    case Op.LeF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F <= b ? -1 : 0; break; }
                    case Op.GtF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F >  b ? -1 : 0; break; }
                    case Op.GeF: { double b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F >= b ? -1 : 0; break; }

                    case Op.EqS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.Equals(Strings.Get(_stack[_sp-1].S), b, StringComparison.Ordinal) ? -1 : 0; break; }
                    case Op.NeS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.Equals(Strings.Get(_stack[_sp-1].S), b, StringComparison.Ordinal) ? 0 : -1; break; }
                    case Op.LtS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.CompareOrdinal(Strings.Get(_stack[_sp-1].S), b) <  0 ? -1 : 0; break; }
                    case Op.LeS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.CompareOrdinal(Strings.Get(_stack[_sp-1].S), b) <= 0 ? -1 : 0; break; }
                    case Op.GtS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.CompareOrdinal(Strings.Get(_stack[_sp-1].S), b) >  0 ? -1 : 0; break; }
                    case Op.GeS: { string b = Strings.Get(Pop().S); _stack[_sp-1].I = string.CompareOrdinal(Strings.Get(_stack[_sp-1].S), b) >= 0 ? -1 : 0; break; }

                    case Op.AndI: { int b = Pop().I; _stack[_sp-1].I &= b; break; }
                    case Op.OrI:  { int b = Pop().I; _stack[_sp-1].I |= b; break; }
                    case Op.XorI: { int b = Pop().I; _stack[_sp-1].I ^= b; break; }
                    case Op.NotI: _stack[_sp-1].I = ~_stack[_sp-1].I; break;

                    case Op.Jmp:
                        if (ins.A <= pc && _sp == 0 && Strings.Count > GcThreshold) Collect();
                        pc = ins.A;
                        break;
                    case Op.JmpF: { int v = Pop().I; if (v == 0) pc = ins.A; break; }
                    case Op.JmpT: { int v = Pop().I; if (v != 0) pc = ins.A; break; }

                    case Op.Gosub:
                        if (_gc == _gosub.Length) {
                            if (_gc >= 8192) { _pc = pc; throw Fault("GOSUB nested too deep (missing RETURN?)"); }
                            Array.Resize(ref _gosub, _gosub.Length * 2);
                        }
                        _gosub[_gc++] = pc;
                        pc = ins.A;
                        break;
                    case Op.Return:
                        if (_gc == 0) { _pc = pc; throw Fault("RETURN without GOSUB"); }
                        pc = _gosub[--_gc];
                        if (ins.A >= 0) pc = ins.A;       // RETURN label
                        break;

                    case Op.Call: {
                        FuncInfo fi = _c.Funcs[ins.A];
                        int argc = ins.B;
                        if (_fc == _frames.Length) {
                            if (_fc >= 512) { _pc = pc; throw Fault("call stack overflow (runaway recursion?)"); }
                            Array.Resize(ref _frames, _frames.Length * 2);
                        }
                        int newBase = _localsTop;
                        int need = newBase + Math.Max(fi.LocalCount, argc);
                        if (need > _locals.Length) {
                            int n = _locals.Length;
                            while (n < need) n *= 2;
                            Array.Resize(ref _locals, n);
                        }
                        // Arguments sit on the operand stack in declaration order.
                        for (int i = argc - 1; i >= 0; i--) _locals[newBase + i] = Pop();
                        for (int i = argc; i < fi.LocalCount; i++) _locals[newBase + i] = new Slot();

                        Frame f = new Frame();
                        f.RetPc = pc; f.LocalsBase = newBase; f.FuncIdx = ins.A;
                        _frames[_fc++] = f;
                        _localsTop = newBase + fi.LocalCount;
                        pc = fi.Addr;
                        break;
                    }
                    case Op.Ret: {
                        if (_fc == 0) { _pc = -1; return RunState.Halted; }
                        Frame f = _frames[--_fc];
                        _localsTop = f.LocalsBase;
                        pc = f.RetPc;
                        break;
                    }
                    case Op.RetVal: {
                        if (_fc == 0) { _pc = -1; return RunState.Halted; }
                        Frame f = _frames[--_fc];
                        FuncInfo fi = _c.Funcs[f.FuncIdx];
                        Slot rv = _locals[f.LocalsBase + fi.ResultSlot];
                        _localsTop = f.LocalsBase;
                        pc = f.RetPc;
                        Push(rv);
                        break;
                    }

                    case Op.CallHost: {
                        HostFn fn = Host.All[ins.A];
                        int argc = ins.B;
                        for (int i = argc - 1; i >= 0; i--) _argBuf[i] = Pop();
                        _pc = pc;                       // host code may throw
                        _budget = budget;               // and may read or spend the clock
                        Slot r = fn.Impl(this, _argBuf);
                        budget = _budget;
                        if (fn.HasRet) Push(r);
                        if (_yield) { _yield = false; return RunState.Waiting; }
                        break;
                    }

                    // Re-executes itself until the host reports the sound
                    // finished, so blocking needs no extra VM state at all.
                    case Op.AwaitSound:
                        if (Sound.IsBusy) {
                            _pc = pc - 1;
                            return RunState.Waiting;
                        }
                        break;

                    // SLEEP: until the time is up or a key arrives.
                    case Op.AwaitSleep:
                        if (Time < _sleepUntil && _keys.Count == 0) {
                            _pc = pc - 1;
                            return RunState.Waiting;
                        }
                        break;

                    // INPUT / LINE INPUT: edit a line at the cursor, one
                    // frame's worth of keys at a time.
                    case Op.InputLine:
                        if (!EditLine((ins.A & 1) != 0)) {
                            _pc = pc - 1;
                            return RunState.Waiting;
                        }
                        break;

                    case Op.Wait:
                        _pc = pc;
                        if (_sp == 0 && Strings.Count > GcThreshold) Collect();
                        return RunState.Waiting;

                    case Op.Halt:
                        _pc = -1;
                        return RunState.Halted;

                    default:
                        _pc = pc;
                        throw Fault("unimplemented opcode " + ins.Op);
                }
            }
            _pc = pc;
            return RunState.Yielded;
        }

        private int CurBase { get { return _fc > 0 ? _frames[_fc - 1].LocalsBase : 0; } }

        private static int RoundToInt(double v) {
            if (v >= 2147483647.0) return int.MaxValue;
            if (v <= -2147483648.0) return int.MinValue;
            return (int)Math.Round(v);
        }

        /// <summary>Flat element index for the n subscripts sitting above the
        /// array handle at _stack[_sp]. Bounds-checks every dimension.</summary>
        private int ElemIndex(int n, int pc) {
            int h = _stack[_sp].I;
            ArrayObj a = (h > 0 && h < _arrs.Count) ? _arrs[h] : null;
            if (a == null) { _pc = pc; throw Fault("array used before DIM"); }
            int[] lo = a.Lo, len = a.Len;
            if (n != lo.Length) { _pc = pc; throw Fault("wrong number of subscripts for " + a.Info.Name); }
            int at = 0;
            for (int k = 0; k < n; k++) {
                int i = _stack[_sp + 1 + k].I - lo[k];
                if ((uint)i >= (uint)len[k]) {
                    _pc = pc;
                    throw Fault("subscript " + _stack[_sp + 1 + k].I + " out of range for " + a.Info.Name +
                                " (" + lo[k] + " TO " + (lo[k] + len[k] - 1) + ")");
                }
                at = at * len[k] + i;
            }
            return at;
        }

        /// <summary>One frame of line editing. Returns true once Enter has
        /// been pressed and InputText holds the line.</summary>
        private bool EditLine(bool stayOnLine) {
            if (!_inputActive) {
                _inputActive = true;
                _inputSb.Length = 0;
            } else {
                EraseCursor();
            }
            while (_keys.Count > 0) {
                string k = _keys.Dequeue();
                if (k.Length != 1) continue;              // extended keys do nothing here
                char c = k[0];
                if (c == '\r' || c == '\n') {
                    InputText = _inputSb.ToString();
                    _inputActive = false;
                    if (!stayOnLine) Scr.NewLine();
                    return true;
                }
                if (c == '\b') {
                    if (_inputSb.Length > 0 && Scr.CurCol > 0) {
                        _inputSb.Length--;
                        Scr.CurCol--;
                        Scr.Write(" ");
                        Scr.CurCol--;
                    }
                    continue;
                }
                if (c < ' ' || _inputSb.Length >= 255) continue;
                if (Scr.CurCol >= Scr.Cols - 1) continue;  // keep the line on one row
                _inputSb.Append(c);
                Scr.Write(k);
            }
            // Show a cursor where the next character will go.
            if (Scr.CurCol < Scr.Cols) {
                Scr.Write("_");
                Scr.CurCol--;
            }
            return false;
        }

        private void EraseCursor() {
            if (Scr.CurCol < Scr.Cols) {
                Scr.Write(" ");
                Scr.CurCol--;
            }
        }

        /// <summary>Precise mark-sweep over the only places a live string can
        /// be. Only ever called with an empty operand stack, which is what
        /// makes the root set exact.</summary>
        public void Collect() {
            Strings.BeginMark();

            for (int i = 0; i < _c.GlobalTypes.Count; i++)
                if (_c.GlobalTypes[i] == VType.Str) Strings.Mark(_globals[i].S);

            for (int f = 0; f < _fc; f++) {
                FuncInfo fi = _c.Funcs[_frames[f].FuncIdx];
                if (fi.LocalTypes == null) continue;
                int b = _frames[f].LocalsBase;
                for (int i = 0; i < fi.LocalTypes.Length; i++)
                    if (fi.LocalTypes[i] == VType.Str) Strings.Mark(_locals[b + i].S);
            }

            for (int h = 1; h < _arrs.Count; h++) {
                ArrayObj a = _arrs[h];
                if (a == null || !a.Info.HasStr) continue;
                VType[] st = a.Info.SlotTypes;
                Slot[] d = a.Data;
                int stride = a.Stride;
                for (int i = 0; i < d.Length; i++)
                    if (st[i % stride] == VType.Str) Strings.Mark(d[i].S);
            }

            Strings.Sweep();
        }
    }
}
