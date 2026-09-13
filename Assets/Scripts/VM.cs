using System;
using System.Collections.Generic;

namespace UBasic {

    public enum RunState {
        /// <summary>Budget ran out mid-statement. Call Run again next frame.</summary>
        Yielded,
        /// <summary>Hit a WAIT. The program wants the next frame.</summary>
        Waiting,
        Halted,
        Faulted
    }

    /// <summary>The uBasic virtual machine.
    ///
    /// Interpreter state is (pc, operand stack, locals arena, frame stack) and
    /// nothing else, so suspending mid-program is just returning from Run.
    /// That is what makes WAIT, per-frame fuel limits and save/restore fall out
    /// for free rather than needing continuations.</summary>
    public class VM {
        private struct Frame {
            public int RetPc;
            public int LocalsBase;
            public int FuncIdx;
        }

        private readonly Chunk _c;
        private Instr[] _code;
        private int[] _constHandles;

        private Slot[] _stack = new Slot[256];
        private int _sp;
        private Slot[] _globals;
        private Slot[] _locals = new Slot[1024];
        private int _localsTop;
        private Frame[] _frames = new Frame[64];
        private int _fc;
        private Slot[][] _arrays;
        private readonly Slot[] _argBuf = new Slot[8];

        private int _pc;

        public readonly StringHeap Strings = new StringHeap();
        public Screen Scr;
        public IInput Input = new NullInput();
        public float Time;
        public Random Rng = new Random(12345);

        public string FaultMessage { get; private set; }
        public int FaultLine { get; private set; }
        public bool Finished { get { return _pc < 0; } }

        /// <summary>Collect once the heap grows past this many live strings.</summary>
        public int GcThreshold = 512;

        public VM(Chunk c, Screen screen) {
            _c = c;
            Scr = screen;
            Reset();
        }

        public void Reset() {
            _code = _c.Code.ToArray();
            _globals = new Slot[Math.Max(1, _c.GlobalTypes.Count)];
            _arrays = new Slot[_c.Arrays.Count][];
            for (int i = 0; i < _c.Arrays.Count; i++)
                _arrays[i] = new Slot[_c.Arrays[i].Length];

            _constHandles = new int[_c.StringConsts.Count];
            for (int i = 0; i < _c.StringConsts.Count; i++)
                _constHandles[i] = Strings.AllocPinned(_c.StringConsts[i]);

            _sp = 0; _fc = 0; _localsTop = 0;
            _pc = _c.EntryPoint;
            FaultMessage = null; FaultLine = 0;
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

        /// <summary>Run at most <paramref name="budget"/> instructions.</summary>
        public RunState Run(int budget) {
            if (_pc < 0) return RunState.Halted;
            try {
                return Loop(budget);
            } catch (UBasicError e) {
                FaultMessage = e.Message;
                FaultLine = e.Line;
                _pc = -1;
                return RunState.Faulted;
            }
        }

        private RunState Loop(int budget) {
            Instr[] code = _code;
            int pc = _pc;

            while (budget-- > 0) {
                Instr ins = code[pc++];
                switch (ins.Op) {
                    case Op.Nop: break;

                    case Op.PushI: Push(Slot.FromInt(ins.A)); break;
                    case Op.PushF: Push(Slot.FromFloat(BitConv.IntToFloat(ins.A))); break;
                    case Op.PushS: Push(Slot.FromStr(_constHandles[ins.A])); break;
                    case Op.Pop: _sp--; break;

                    case Op.LoadG: Push(_globals[ins.A]); break;
                    case Op.StoreG: _globals[ins.A] = Pop(); break;
                    case Op.LoadL: Push(_locals[CurBase + ins.A]); break;
                    case Op.StoreL: _locals[CurBase + ins.A] = Pop(); break;

                    case Op.ArrGet1: {
                        int i = Pop().I;
                        Slot[] arr = _arrays[ins.A];
                        if ((uint)i >= (uint)arr.Length) { _pc = pc; throw Fault(OutOfRange(ins.A, i)); }
                        Push(arr[i]);
                        break;
                    }
                    case Op.ArrSet1: {
                        Slot v = Pop();
                        int i = Pop().I;
                        Slot[] arr = _arrays[ins.A];
                        if ((uint)i >= (uint)arr.Length) { _pc = pc; throw Fault(OutOfRange(ins.A, i)); }
                        arr[i] = v;
                        break;
                    }
                    case Op.ArrGet2: {
                        int j = Pop().I, i = Pop().I;
                        int idx = Index2(ins.A, i, j, ref pc);
                        Push(_arrays[ins.A][idx]);
                        break;
                    }
                    case Op.ArrSet2: {
                        Slot v = Pop();
                        int j = Pop().I, i = Pop().I;
                        int idx = Index2(ins.A, i, j, ref pc);
                        _arrays[ins.A][idx] = v;
                        break;
                    }

                    case Op.AddI: { int b = Pop().I; _stack[_sp-1].I += b; break; }
                    case Op.SubI: { int b = Pop().I; _stack[_sp-1].I -= b; break; }
                    case Op.MulI: { int b = Pop().I; _stack[_sp-1].I *= b; break; }
                    case Op.DivI: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("division by zero"); } _stack[_sp-1].I /= b; break; }
                    case Op.IDiv: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("integer division by zero"); } _stack[_sp-1].I /= b; break; }
                    case Op.ModI: { int b = Pop().I; if (b == 0) { _pc = pc; throw Fault("MOD by zero"); } _stack[_sp-1].I %= b; break; }
                    case Op.NegI: _stack[_sp-1].I = -_stack[_sp-1].I; break;

                    case Op.AddF: { float b = Pop().F; _stack[_sp-1].F += b; break; }
                    case Op.SubF: { float b = Pop().F; _stack[_sp-1].F -= b; break; }
                    case Op.MulF: { float b = Pop().F; _stack[_sp-1].F *= b; break; }
                    case Op.DivF: { float b = Pop().F; if (b == 0f) { _pc = pc; throw Fault("division by zero"); } _stack[_sp-1].F /= b; break; }
                    case Op.NegF: _stack[_sp-1].F = -_stack[_sp-1].F; break;
                    case Op.PowF: { float b = Pop().F; _stack[_sp-1].F = (float)Math.Pow(_stack[_sp-1].F, b); break; }
                    case Op.PowI: { int b = Pop().I; _stack[_sp-1].I = (int)Math.Pow(_stack[_sp-1].I, b); break; }

                    case Op.Concat: {
                        int bh = Pop().S, ah = Pop().S;
                        Push(Slot.FromStr(Strings.Alloc(Strings.Get(ah) + Strings.Get(bh))));
                        break;
                    }

                    case Op.I2F: _stack[_sp-1].F = _stack[_sp-1].I; break;
                    case Op.F2I: _stack[_sp-1].I = (int)_stack[_sp-1].F; break;
                    case Op.I2FUnder: _stack[_sp-2].F = _stack[_sp-2].I; break;

                    case Op.EqI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I == b ? -1 : 0; break; }
                    case Op.NeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I != b ? -1 : 0; break; }
                    case Op.LtI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I <  b ? -1 : 0; break; }
                    case Op.LeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I <= b ? -1 : 0; break; }
                    case Op.GtI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I >  b ? -1 : 0; break; }
                    case Op.GeI: { int b = Pop().I; _stack[_sp-1].I = _stack[_sp-1].I >= b ? -1 : 0; break; }

                    case Op.EqF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F == b ? -1 : 0; break; }
                    case Op.NeF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F != b ? -1 : 0; break; }
                    case Op.LtF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F <  b ? -1 : 0; break; }
                    case Op.LeF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F <= b ? -1 : 0; break; }
                    case Op.GtF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F >  b ? -1 : 0; break; }
                    case Op.GeF: { float b = Pop().F; _stack[_sp-1].I = _stack[_sp-1].F >= b ? -1 : 0; break; }

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
                        Slot r = fn.Impl(this, _argBuf);
                        if (fn.HasRet) Push(r);
                        break;
                    }

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

        private string OutOfRange(int arrIdx, int i) {
            ArrayInfo a = _c.Arrays[arrIdx];
            return "subscript " + i + " out of range for " + a.Name;
        }

        private int Index2(int arrIdx, int i, int j, ref int pc) {
            ArrayInfo a = _c.Arrays[arrIdx];
            if ((uint)i > (uint)a.D0 || (uint)j > (uint)a.D1) {
                _pc = pc;
                throw Fault("subscript (" + i + "," + j + ") out of range for " + a.Name);
            }
            return i * (a.D1 + 1) + j;
        }

        /// <summary>Precise mark-sweep over the only three places a live string
        /// can be. Only ever called with an empty operand stack, which is what
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

            for (int a = 0; a < _arrays.Length; a++) {
                if (_c.Arrays[a].Type != VType.Str) continue;
                Slot[] arr = _arrays[a];
                for (int i = 0; i < arr.Length; i++) Strings.Mark(arr[i].S);
            }

            Strings.Sweep();
        }
    }
}
