using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Unitycoder.UBasic {

    /// <summary>Static type of a variable or expression. uBasic resolves every
    /// type at compile time, so runtime slots carry no tag.
    ///
    /// The five QBasic scalar types map onto three storage classes: INTEGER
    /// and LONG are both a 32-bit int, SINGLE and DOUBLE are both a 64-bit
    /// float, and STRING is a heap handle. The distinction between the two
    /// members of a class only matters at compile time -- it decides variable
    /// identity (a% and a& are different variables), result types, and how
    /// PRINT formats a number. Record is a user TYPE, which is laid out as a
    /// run of scalar slots and never appears on the operand stack.</summary>
    public enum VType { Int, Single, Str, Long, Double, Record }

    /// <summary>8-byte untyped cell. Which field is live is decided by the
    /// compiler, never checked at runtime -- that is what makes the VM fast.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct Slot {
        [FieldOffset(0)] public int I;    // INTEGER / LONG, boolean (-1 / 0), array handle
        [FieldOffset(0)] public double F; // SINGLE / DOUBLE
        [FieldOffset(0)] public int S;    // STRING handle into StringHeap

        public static Slot FromInt(int v)      { Slot s = new Slot(); s.I = v; return s; }
        public static Slot FromFloat(double v) { Slot s = new Slot(); s.F = v; return s; }
        public static Slot FromStr(int h)      { Slot s = new Slot(); s.S = h; return s; }
    }

    public enum Op {
        Nop,
        PushI, PushF, PushS,
        LoadG, StoreG, LoadL, StoreL,
        /// <summary>Read local A of the procedure that just returned. Its
        /// frame is gone but its slots are still intact directly above the
        /// caller's, which is how by-reference arguments are copied back.</summary>
        LoadRet,
        ArrNew, ArrFree, ArrLoad, ArrStore, ArrBound,
        AddI, SubI, MulI, DivI, IDiv, ModI, NegI, PowI,
        AddF, SubF, MulF, DivF, NegF, PowF,
        Concat,
        I2F, F2I, I2FUnder, F2IUnder,
        EqI, NeI, LtI, LeI, GtI, GeI,
        EqF, NeF, LtF, LeF, GtF, GeF,
        EqS, NeS, LtS, LeS, GtS, GeS,
        AndI, OrI, XorI, NotI,
        Jmp, JmpF, JmpT,
        Call, Ret, RetVal, CallHost,
        Gosub, Return,
        Pop, Wait, AwaitSound, AwaitSleep, InputLine, Halt
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
        public int ParamCount;      // in slots: a record parameter takes several
        public int LocalCount;      // includes params and the result slot
        public VType[] LocalTypes;
        public bool IsFunction;     // FUNCTION (returns) vs SUB (does not)
        public VType ReturnType;
        public int ResultSlot = -1; // local slot holding the return value
        public VType[] ParamTypes;
    }

    /// <summary>Compile-time description of an array: what one element looks
    /// like. Bounds are not here -- DIM and REDIM evaluate them at runtime.</summary>
    public class ArrayInfo {
        public string Name;
        public VType Type;          // element type; Record for a user TYPE
        public int Stride = 1;      // slots per element
        public VType[] SlotTypes;   // type of each slot of one element
        public bool HasStr;         // any string slot -> the GC must scan it
    }

    /// <summary>A live array.</summary>
    public class ArrayObj {
        public Slot[] Data;
        public int[] Lo;            // lower bound per dimension
        public int[] Len;           // element count per dimension
        public int Stride;
        public ArrayInfo Info;
        public int Count;           // total elements
    }

    /// <summary>A compiled uBasic program.</summary>
    public class Chunk {
        public List<Instr> Code = new List<Instr>();
        public List<string> StringConsts = new List<string>();
        public List<double> FloatConsts = new List<double>();
        public List<FuncInfo> Funcs = new List<FuncInfo>();
        public List<ArrayInfo> Arrays = new List<ArrayInfo>();
        public List<VType> GlobalTypes = new List<VType>();
        public List<string> GlobalNames = new List<string>();
        /// <summary>Every DATA item of the program, in source order.</summary>
        public List<string> Data = new List<string>();
        public int EntryPoint;
        /// <summary>True when the source was compiled as classic QBasic
        /// rather than the uBasic dialect. Decides the start-up screen
        /// (SCREEN 0 text), 1-based LOCATE, and friends.</summary>
        public bool QBasic;

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

        public int AddFloat(double v) {
            // Small programs repeat the same few constants a lot.
            int n = FloatConsts.Count;
            for (int i = n - 1; i >= 0 && i >= n - 16; i--)
                if (FloatConsts[i].Equals(v)) return i;
            FloatConsts.Add(v);
            return n;
        }
    }

    /// <summary>String storage with a free list and a precise mark-sweep.
    ///
    /// uBasic has no closures, so every live string is reachable from a small
    /// fixed set of places: the string constant pool, global slots typed Str,
    /// the locals of active frames typed Str, and string slots of live arrays.
    /// Collection only runs at a statement boundary where the operand stack is
    /// empty, so the root set is exact -- no conservative scanning, no write
    /// barriers.</summary>
    public class StringHeap {
        private string[] _slots = new string[256];
        private bool[] _mark = new bool[256];
        private bool[] _pinned = new bool[256];
        private readonly Stack<int> _free = new Stack<int>();
        private int _top;

        public StringHeap() {
            // Handle 0 is the empty string, so a zero-initialised string
            // variable reads as "" instead of aliasing whatever came first.
            AllocPinned("");
        }

        public int Count { get { return _top - _free.Count; } }
        public int Capacity { get { return _slots.Length; } }

        public string Get(int h) { return h >= 0 && h < _top ? (_slots[h] ?? "") : ""; }

        public int Alloc(string v) {
            if (v == null || v.Length == 0) return 0;
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
            int h;
            if (_top == _slots.Length) Grow();
            h = _top++;
            _slots[h] = v ?? "";
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
