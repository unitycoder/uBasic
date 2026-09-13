using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UBasic {

    /// <summary>Static type of a variable or expression. uBasic resolves every
    /// type at compile time, so runtime slots carry no tag.</summary>
    public enum VType { Int, Single, Str }

    /// <summary>8-byte untyped cell. Which field is live is decided by the
    /// compiler, never checked at runtime -- that is what makes the VM fast.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct Slot {
        [FieldOffset(0)] public int I;    // INTEGER, and boolean (-1 / 0)
        [FieldOffset(0)] public float F;  // SINGLE
        [FieldOffset(0)] public int S;    // STRING handle into StringHeap

        public static Slot FromInt(int v)   { Slot s = new Slot(); s.I = v; return s; }
        public static Slot FromFloat(float v) { Slot s = new Slot(); s.F = v; return s; }
        public static Slot FromStr(int h)   { Slot s = new Slot(); s.S = h; return s; }
    }

    public enum Op {
        Nop,
        PushI, PushF, PushS,
        LoadG, StoreG, LoadL, StoreL,
        ArrGet1, ArrSet1, ArrGet2, ArrSet2,
        AddI, SubI, MulI, DivI, IDiv, ModI, NegI, PowI,
        AddF, SubF, MulF, DivF, NegF, PowF,
        Concat,
        I2F, F2I, I2FUnder,
        EqI, NeI, LtI, LeI, GtI, GeI,
        EqF, NeF, LtF, LeF, GtF, GeF,
        EqS, NeS, LtS, LeS, GtS, GeS,
        AndI, OrI, XorI, NotI,
        Jmp, JmpF, JmpT,
        Call, Ret, RetVal, CallHost,
        Pop, Wait, Halt
    }

    /// <summary>One instruction. A struct array rather than a packed byte
    /// stream: slightly larger, far easier to read and debug.</summary>
    public struct Instr {
        public Op Op;
        public int A;    // primary operand (slot / address / index)
        public int B;    // secondary operand (argc, dimension)
        public int Line; // source line, for runtime error messages

        public Instr(Op op, int a, int b, int line) { Op = op; A = a; B = b; Line = line; }
    }

    public class FuncInfo {
        public string Name;
        public int Addr;            // entry point in code
        public int ParamCount;
        public int LocalCount;      // includes params and the result slot
        public VType[] LocalTypes;
        public bool IsFunction;     // FUNCTION (returns) vs SUB (does not)
        public VType ReturnType;
        public int ResultSlot = -1; // local slot holding the return value
        public VType[] ParamTypes;
    }

    public class ArrayInfo {
        public string Name;
        public VType Type;
        public int D0, D1;   // inclusive upper bounds; D1 < 0 means 1-D
        public int Length;
    }

    /// <summary>A compiled uBasic program.</summary>
    public class Chunk {
        public List<Instr> Code = new List<Instr>();
        public List<string> StringConsts = new List<string>();
        public List<FuncInfo> Funcs = new List<FuncInfo>();
        public List<ArrayInfo> Arrays = new List<ArrayInfo>();
        public List<VType> GlobalTypes = new List<VType>();
        public List<string> GlobalNames = new List<string>();
        public int EntryPoint;

        public int Emit(Op op, int a, int b, int line) {
            Code.Add(new Instr(op, a, b, line));
            return Code.Count - 1;
        }
        public void Patch(int at, int addr) {
            Instr i = Code[at];
            i.A = addr;
            Code[at] = i;
        }
        public int Here { get { return Code.Count; } }

        /// <summary>Insert an instruction mid-stream. Safe only inside an
        /// expression, which by construction contains no jumps -- the compiler
        /// uses this to add argument conversions once an overload is resolved.</summary>
        public void Insert(int at, Op op, int line) {
            Code.Insert(at, new Instr(op, 0, 0, line));
        }
    }

    /// <summary>String storage with a free list and a precise mark-sweep.
    ///
    /// uBasic has no closures and no reference containers, so every live string
    /// is reachable from exactly three places: the string constant pool, global
    /// slots typed Str, and the locals of active frames typed Str. Collection
    /// only runs at a statement boundary where the operand stack is empty, so
    /// the root set is exact -- no conservative scanning, no write barriers.</summary>
    public class StringHeap {
        private string[] _slots = new string[256];
        private bool[] _mark = new bool[256];
        private bool[] _pinned = new bool[256];
        private readonly Stack<int> _free = new Stack<int>();
        private int _top;

        public int Count { get { return _top - _free.Count; } }
        public int Capacity { get { return _slots.Length; } }

        public string Get(int h) { return h >= 0 && h < _top ? (_slots[h] ?? "") : ""; }

        public int Alloc(string v) {
            int h;
            if (_free.Count > 0) {
                h = _free.Pop();
            } else {
                if (_top == _slots.Length) Grow();
                h = _top++;
            }
            _slots[h] = v;
            return h;
        }

        /// <summary>Constants never move and never die.</summary>
        public int AllocPinned(string v) {
            int h = Alloc(v);
            _pinned[h] = true;
            return h;
        }

        private void Grow() {
            int n = _slots.Length * 2;
            Array.Resize(ref _slots, n);
            Array.Resize(ref _mark, n);
            Array.Resize(ref _pinned, n);
        }

        public void BeginMark() { Array.Clear(_mark, 0, _mark.Length); }
        public void Mark(int h) { if (h >= 0 && h < _top) _mark[h] = true; }

        public void Sweep() {
            for (int h = 0; h < _top; h++) {
                if (_mark[h] || _pinned[h] || _slots[h] == null) continue;
                _slots[h] = null;
                _free.Push(h);
            }
        }
    }

    public class UBasicError : Exception {
        public int Line;
        public UBasicError(string msg, int line) : base(
            line > 0 ? ("line " + line + ": " + msg) : msg) { Line = line; }
    }
}
