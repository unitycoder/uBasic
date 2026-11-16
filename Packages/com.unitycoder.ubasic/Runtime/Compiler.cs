using System;
using System.Collections.Generic;

namespace Unitycoder.UBasic {

    /// <summary>Which flavour of BASIC a source file is written in.
    ///
    /// The two differ only where uBasic had already made its own choice before
    /// classic QBasic programs were supported: the start-up screen (a 320x200
    /// graphics screen vs SCREEN 0 text), LOCATE (0-based vs 1-based), CLS n
    /// (a colour vs QBasic's clear mode), RND(n) and PAINT.</summary>
    public enum Dialect {
        /// <summary>uBasic if the program has a bare WAIT or a SCREEN w, h;
        /// otherwise QBasic if it uses anything only classic programs do
        /// (DECLARE, DEFINT, GOSUB, DATA, INKEY$, INPUT, TYPE, line numbers...);
        /// otherwise uBasic.</summary>
        Auto,
        UBasic,
        QBasic
    }

    /// <summary>Compiles BASIC source straight to bytecode. No AST: a few
    /// cheap pre-passes over the token list collect what must be known up
    /// front (DEFtype defaults, TYPEs, DATA, SUB/FUNCTION signatures), then
    /// code is emitted in a single recursive descent.</summary>
    public class Compiler {

        private class ConstVal { public VType Type; public double Num; public string Str; }

        private class Field {
            public string Name; public VType Type; public TypeInfo Rec;
            public int Offset, Size;
        }

        /// <summary>A user TYPE, flattened to a run of scalar slots.</summary>
        private class TypeInfo {
            public string Name;
            public List<Field> Fields = new List<Field>();
            public List<VType> Slots = new List<VType>();
            public int Size { get { return Slots.Count; } }
            public Field Find(string name) {
                for (int i = 0; i < Fields.Count; i++) if (Fields[i].Name == name) return Fields[i];
                return null;
            }
        }

        private class Sym {
            public bool IsLocal;
            public int Slot;
            public VType Type;
            public TypeInfo Rec;
            public bool IsArray;        // Slot holds an array handle
            public int Desc = -1;       // index into Chunk.Arrays
            public bool Owned;          // local array, freed when the procedure returns
            public bool Once;           // STATIC array, allocated on first DIM only
            public int ParamIdx = -1;   // which parameter of the current procedure
        }

        private class Param {
            public Token Tok;
            public VType Type; public TypeInfo Rec;
            public bool IsArray, AsDeclared, IsAny;
            public int Slot, Size;
        }

        private class Proc {
            public FuncInfo Fi;
            public int Index;
            public string Bare;
            public List<Param> Params = new List<Param>();
            public bool IsStatic, ByVal;
            public bool[] Written;      // parameter is assigned somewhere in the body
        }

        private class LoopCtx {
            public List<int> ExitJumps = new List<int>();
            public int Top;
            public string Kind;      // "FOR" | "WHILE" | "DO"
        }

        /// <summary>Something that can be read and assigned: a variable, an
        /// array element, or a field of either.</summary>
        private class LV {
            public Sym Base;
            public bool Elem;
            public int NDims;
            public Sym[] Temps;         // subscripts saved in temporaries, or null if on the stack
            public int Offset;
            public VType Type;
            public TypeInfo Rec;
        }

        private struct LabelFix { public int At; public string Key; public string Raw; public int Line; }
        private struct CopyFix { public int Start, End; public Proc P; public int Param; }
        private struct Forward { public Proc From; public int FromParam; public Proc To; public int ToParam; }

        private List<Token> _t;
        private int _p;
        private Chunk _c;
        private bool _qb;

        private readonly Dictionary<string, ConstVal> _consts = new Dictionary<string, ConstVal>();
        private readonly Dictionary<string, TypeInfo> _types = new Dictionary<string, TypeInfo>();
        private readonly Dictionary<string, Sym> _gsyms = new Dictionary<string, Sym>();
        private readonly HashSet<string> _shared = new HashSet<string>();
        private readonly Dictionary<string, Proc> _procs = new Dictionary<string, Proc>();
        private readonly Dictionary<string, int> _labelData = new Dictionary<string, int>();
        private readonly List<CopyFix> _copyFixes = new List<CopyFix>();
        private readonly List<Forward> _forwards = new List<Forward>();

        // current procedure (null at module level)
        private Proc _cur;
        private bool _defFn;
        private Sym _resultSym;
        private Dictionary<string, Sym> _lsyms;
        private List<VType> _localTypes;
        private List<int> _exitFix;
        private readonly List<LoopCtx> _loops = new List<LoopCtx>();
        private Dictionary<string, int> _labels;
        private List<LabelFix> _labelFix;
        private int _optionBase;
        private bool _nextComma;

        // ---- token helpers -------------------------------------------------

        private Token Cur { get { return _t[_p]; } }
        private Token Peek(int n) { int i = _p + n; return i < _t.Count ? _t[i] : _t[_t.Count - 1]; }
        private Token At(int i) { return i < _t.Count ? _t[i] : _t[_t.Count - 1]; }
        private Token Adv() { return _t[_p++]; }
        private int Line { get { return Cur.Line; } }

        private bool IsKw(string k) { return Cur.Kind == T.Keyword && Cur.Text == k; }
        private static bool IsKw(Token t, string k) { return t.Kind == T.Keyword && t.Text == k; }
        private bool IsIdent(string name) { return Cur.Kind == T.Ident && Cur.Text == name && !Cur.HadSigil; }

        private bool AcceptKw(string k) {
            if (!IsKw(k)) return false;
            _p++; return true;
        }
        private void ExpectKw(string k) {
            if (!AcceptKw(k)) throw Err("expected " + k);
        }
        private bool Accept(T k) {
            if (Cur.Kind != k) return false;
            _p++; return true;
        }
        private void Expect(T k, string what) {
            if (!Accept(k)) throw Err("expected " + what);
        }
        private static string Describe(Token t) {
            return t.Kind == T.EOF ? "end of file"
                 : t.Kind == T.EOL ? "end of line"
                 : (t.Raw ?? t.Text ?? Punct(t.Kind));
        }
        private static string Punct(T k) {
            switch (k) {
                case T.Plus: return "+"; case T.Minus: return "-"; case T.Star: return "*";
                case T.Slash: return "/"; case T.Backslash: return "\\"; case T.Caret: return "^";
                case T.LParen: return "("; case T.RParen: return ")"; case T.Comma: return ",";
                case T.Semi: return ";"; case T.Colon: return ":"; case T.Eq: return "=";
                case T.Ne: return "<>"; case T.Lt: return "<"; case T.Le: return "<=";
                case T.Gt: return ">"; case T.Ge: return ">="; case T.Dot: return "."; case T.Hash: return "#";
            }
            return k.ToString();
        }
        private UBasicError Err(string msg) {
            return new UBasicError(msg + ", got '" + Describe(Cur) + "'", Line);
        }

        private static bool IsInt(VType t) { return t == VType.Int || t == VType.Long; }
        private static bool IsFlt(VType t) { return t == VType.Single || t == VType.Double; }

        private static string Suffix(VType t) {
            switch (t) {
                case VType.Int: return "%";
                case VType.Long: return "&";
                case VType.Double: return "#";
                case VType.Str: return "$";
                default: return "!";
            }
        }
        private static string TKey(Token t) { return t.Text + Suffix(t.Sig); }

        /// <summary>Skip to the next statement. A line number in front of it
        /// is taken as a label here, so that numbered programs can put one
        /// before NEXT, CASE, END IF and the like.</summary>
        private void SkipTerminators() {
            while (true) {
                if (Cur.Kind == T.EOL || Cur.Kind == T.Colon) { _p++; continue; }
                if (Cur.Kind == T.IntLit && _p > 0 && _t[_p - 1].Kind == T.EOL && _labels != null) {
                    DefineLabel(Cur.IVal.ToString());
                    _p++;
                    continue;
                }
                break;
            }
        }
        /// <summary>A statement ends at end of line, a colon, end of file --
        /// or at ELSE, which belongs to the enclosing single-line IF rather
        /// than to the statement that just finished.</summary>
        private bool AtEnd() {
            return Cur.Kind == T.EOL || Cur.Kind == T.Colon || Cur.Kind == T.EOF || IsKw("ELSE");
        }
        private static bool IsEnd(Token t) {
            return t.Kind == T.EOL || t.Kind == T.Colon || t.Kind == T.EOF || IsKw(t, "ELSE");
        }
        private void EndOfStatement() {
            if (AtEnd()) return;
            throw Err("unexpected token after statement");
        }
        /// <summary>There is an argument here, as opposed to an omitted one
        /// (", ,") or the end of the list.</summary>
        private bool HasArg() { return Cur.Kind != T.Comma && !AtEnd(); }
        private void SkipStatement() { while (!AtEnd()) _p++; }
        private void SkipLine() { while (Cur.Kind != T.EOL && Cur.Kind != T.EOF) _p++; }

        /// <summary>Index of the ')' matching the '(' at index open, or -1.</summary>
        private int MatchParen(int open) {
            int depth = 0;
            for (int i = open; i < _t.Count; i++) {
                T k = _t[i].Kind;
                if (k == T.LParen) depth++;
                else if (k == T.RParen) { if (--depth == 0) return i; }
                else if (k == T.EOL || k == T.EOF) return -1;
            }
            return -1;
        }

        // ---- entry ---------------------------------------------------------

        public static Chunk Compile(string source) { return Compile(source, Dialect.Auto); }

        public static Chunk Compile(string source, Dialect dialect) {
            Compiler co = new Compiler();
            return co.Run(source, dialect);
        }

        /// <summary>What Dialect.Auto would pick for this source.</summary>
        public static Dialect Detect(string source) {
            Compiler co = new Compiler();
            co._t = new Lexer(source).Tokenize();
            return co.DetectQBasic() ? Dialect.QBasic : Dialect.UBasic;
        }

        private Chunk Run(string source, Dialect dialect) {
            _t = new Lexer(source).Tokenize();
            _c = new Chunk();
            _qb = dialect == Dialect.QBasic || (dialect == Dialect.Auto && DetectQBasic());
            _c.QBasic = _qb;
            ApplyDefTypes();
            ScanTypes();
            ScanData();
            DefinePredefinedConsts();
            ScanSignatures();
            CompileModule();
            FinishCopyOuts();
            return _c;
        }

        private bool DetectQBasic() {
            bool classic = false;
            bool lineStart = true;
            for (int i = 0; i < _t.Count; i++) {
                Token tk = _t[i];
                Token nx = At(i + 1);
                if (tk.Kind == T.Keyword) {
                    switch (tk.Text) {
                        case "WAIT":
                            if (IsEnd(nx)) return false;          // uBasic's frame sync
                            break;
                        case "SCREEN":
                            if (nx.Kind == T.IntLit && nx.IVal >= 32) return false;   // SCREEN w, h
                            classic = true;
                            break;
                        case "DECLARE": case "DEFINT": case "DEFSNG": case "DEFDBL": case "DEFLNG":
                        case "DEFSTR": case "GOSUB": case "RETURN": case "READ": case "TYPE":
                        case "INPUT": case "DEF": case "ON": case "SLEEP": case "REDIM": case "COMMON":
                        case "VIEW": case "WIDTH": case "PUT": case "GET": case "USING": case "ERASE":
                        case "POKE": case "RESTORE": case "STATIC": case "PRESET":
                            classic = true;
                            break;
                    }
                } else if (tk.Kind == T.Data) classic = true;
                else if (tk.Kind == T.Ident && tk.Text == "INKEY" && tk.Sig == VType.Str) classic = true;
                else if (tk.Kind == T.IntLit && lineStart) classic = true;   // line numbers
                lineStart = tk.Kind == T.EOL;
            }
            return classic;
        }

        private void DefinePredefinedConsts() {
            // Lowest priority: a variable or CONST of the same name wins.
            DefConst("TRUE", VType.Int, -1);
            DefConst("FALSE", VType.Int, 0);
            DefConst("PI", VType.Single, 3.14159265358979);
            DefConst("KEY_LEFT", VType.Int, 1);
            DefConst("KEY_RIGHT", VType.Int, 2);
            DefConst("KEY_UP", VType.Int, 3);
            DefConst("KEY_DOWN", VType.Int, 4);
            DefConst("KEY_ESC", VType.Int, 27);
            DefConst("KEY_SPACE", VType.Int, 32);
            DefConst("KEY_ENTER", VType.Int, 13);
        }
        private void DefConst(string key, VType t, double v) {
            ConstVal c = new ConstVal(); c.Type = t; c.Num = v;
            _consts[key] = c;
        }

        // ---- pre-pass: DEFINT and friends ------------------------------------

        /// <summary>Give every sigil-less identifier its default type. DEFtype
        /// statements apply from where they appear to the end of the file,
        /// which is why this walks the tokens in order rather than being part
        /// of code generation (procedures are compiled after the module).</summary>
        private void ApplyDefTypes() {
            VType[] def = new VType[26];
            for (int i = 0; i < 26; i++) def[i] = VType.Single;
            for (int i = 0; i < _t.Count; i++) {
                Token tk = _t[i];
                if (tk.Kind == T.Keyword && tk.Text.Length == 6 && tk.Text.StartsWith("DEF")) {
                    VType ty;
                    switch (tk.Text) {
                        case "DEFINT": ty = VType.Int; break;
                        case "DEFLNG": ty = VType.Long; break;
                        case "DEFSNG": ty = VType.Single; break;
                        case "DEFDBL": ty = VType.Double; break;
                        default: ty = VType.Str; break;
                    }
                    int j = i + 1;
                    while (At(j).Kind == T.Ident && At(j).Text.Length == 1) {
                        int a = At(j).Text[0] - 'A', b = a;
                        j++;
                        if (At(j).Kind == T.Minus && At(j + 1).Kind == T.Ident && At(j + 1).Text.Length == 1) {
                            b = At(j + 1).Text[0] - 'A';
                            j += 2;
                        }
                        if (a > b) { int s = a; a = b; b = s; }
                        for (int k = Math.Max(0, a); k <= Math.Min(25, b); k++) def[k] = ty;
                        if (At(j).Kind != T.Comma) break;
                        j++;
                    }
                    i = j - 1;
                    continue;
                }
                if (tk.Kind == T.Ident && !tk.HadSigil) {
                    char c = tk.Text[0];
                    if (c >= 'A' && c <= 'Z') { tk.Sig = def[c - 'A']; _t[i] = tk; }
                }
            }
        }

        // ---- pre-pass: TYPE ... END TYPE ---------------------------------------

        private bool StatementStartsAt(int i) {
            if (i == 0) return true;
            T k = _t[i - 1].Kind;
            if (k == T.IntLit) return i == 1 || _t[i - 2].Kind == T.EOL;   // after a line number
            return k == T.EOL || k == T.Colon;
        }

        private void ScanTypes() {
            for (int i = 0; i < _t.Count; i++) {
                if (!IsKw(_t[i], "TYPE") || !StatementStartsAt(i)) continue;
                Token nameTok = At(i + 1);
                if (nameTok.Kind != T.Ident && nameTok.Kind != T.Keyword)
                    throw new UBasicError("expected a name after TYPE", _t[i].Line);
                TypeInfo ti = new TypeInfo();
                ti.Name = nameTok.Text;
                int j = i + 2;
                while (true) {
                    while (At(j).Kind == T.EOL || At(j).Kind == T.Colon) j++;
                    Token f = At(j);
                    if (f.Kind == T.EOF) throw new UBasicError("missing END TYPE", _t[i].Line);
                    if (IsKw(f, "END") && IsKw(At(j + 1), "TYPE")) { j += 2; break; }
                    if (f.Kind != T.Ident && f.Kind != T.Keyword)
                        throw new UBasicError("expected a field name in TYPE " + nameTok.Raw, f.Line);
                    if (!IsKw(At(j + 1), "AS"))
                        throw new UBasicError("expected AS after field '" + f.Raw + "'", f.Line);
                    Field fd = new Field();
                    fd.Name = f.Text;
                    j += 2;
                    ParseAsTypeAt(ref j, out fd.Type, out fd.Rec);
                    fd.Offset = ti.Slots.Count;
                    if (fd.Rec != null) { fd.Size = fd.Rec.Size; ti.Slots.AddRange(fd.Rec.Slots); }
                    else { fd.Size = 1; ti.Slots.Add(fd.Type); }
                    ti.Fields.Add(fd);
                }
                if (ti.Fields.Count == 0) throw new UBasicError("TYPE " + nameTok.Raw + " has no fields", _t[i].Line);
                _types[ti.Name] = ti;
                i = j - 1;
            }
        }

        /// <summary>Parse the type name after AS, starting at token index j.</summary>
        private void ParseAsTypeAt(ref int j, out VType ty, out TypeInfo rec) {
            Token t = At(j);
            rec = null;
            if (t.Kind == T.Keyword) {
                j++;
                switch (t.Text) {
                    case "INTEGER": ty = VType.Int; return;
                    case "LONG": ty = VType.Long; return;
                    case "SINGLE": ty = VType.Single; return;
                    case "DOUBLE": ty = VType.Double; return;
                    case "STRING":
                        ty = VType.Str;
                        // STRING * n: fixed length is accepted and ignored.
                        if (At(j).Kind == T.Star) { j++; if (At(j).Kind == T.IntLit || At(j).Kind == T.Ident) j++; }
                        return;
                }
            } else if (t.Kind == T.Ident) {
                TypeInfo ti;
                if (_types.TryGetValue(t.Text, out ti)) { j++; ty = VType.Record; rec = ti; return; }
                throw new UBasicError("unknown type '" + t.Raw + "'", t.Line);
            }
            throw new UBasicError("expected a type name after AS", t.Line);
        }

        // ---- pre-pass: DATA and the labels RESTORE can name --------------------

        private void ScanData() {
            bool lineStart = true;
            for (int i = 0; i < _t.Count; i++) {
                Token tk = _t[i];
                if (lineStart) {
                    string key = null;
                    if (tk.Kind == T.IntLit) key = tk.IVal.ToString();
                    else if (tk.Kind == T.Ident && !tk.HadSigil && At(i + 1).Kind == T.Colon) key = tk.Text;
                    if (key != null && !_labelData.ContainsKey(key)) _labelData[key] = _c.Data.Count;
                }
                if (tk.Kind == T.Data) Lexer.SplitData(tk.Text, _c.Data);
                lineStart = tk.Kind == T.EOL;
            }
        }

        // ---- pre-pass: procedure signatures ----------------------------------

        private void ScanSignatures() {
            for (int i = 0; i < _t.Count; i++) {
                Token tk = _t[i];
                if (IsKw(tk, "DECLARE")) {
                    while (At(i).Kind != T.EOL && At(i).Kind != T.EOF) i++;
                    continue;
                }
                bool isDef = IsKw(tk, "DEF") && At(i + 1).Kind == T.Ident && At(i + 1).Text.StartsWith("FN");
                if (!isDef && !IsKw(tk, "SUB") && !IsKw(tk, "FUNCTION")) continue;
                // "END SUB" / "EXIT SUB" are not declarations
                if (i > 0 && (IsKw(_t[i - 1], "END") || IsKw(_t[i - 1], "EXIT"))) continue;

                int j = i + 1;
                if (At(j).Kind != T.Ident) throw new UBasicError(
                    "expected a name after " + tk.Text, At(j).Line);
                Token nameTok = _t[j++];

                Proc p = new Proc();
                p.Bare = nameTok.Text;
                p.ByVal = isDef;
                FuncInfo fi = new FuncInfo();
                fi.Name = nameTok.Text;
                fi.IsFunction = isDef || tk.Text == "FUNCTION";
                fi.ReturnType = nameTok.Sig;
                p.Fi = fi;

                int slot = 0;
                List<VType> slotTypes = new List<VType>();
                if (At(j).Kind == T.LParen) {
                    j++;
                    if (At(j).Kind != T.RParen) {
                        while (true) {
                            if (At(j).Kind != T.Ident) throw new UBasicError(
                                "expected a parameter name", At(j).Line);
                            Param pa = new Param();
                            pa.Tok = _t[j++];
                            pa.Type = pa.Tok.Sig;
                            if (At(j).Kind == T.LParen) {
                                int close = MatchParen(j);
                                if (close < 0) throw new UBasicError("expected ')'", At(j).Line);
                                pa.IsArray = true;
                                j = close + 1;
                            }
                            if (IsKw(At(j), "AS")) {
                                j++;
                                if (IsKw(At(j), "ANY")) { pa.IsAny = true; j++; }
                                else { ParseAsTypeAt(ref j, out pa.Type, out pa.Rec); pa.AsDeclared = true; }
                            }
                            pa.Slot = slot;
                            pa.Size = (pa.Rec != null && !pa.IsArray) ? pa.Rec.Size : 1;
                            if (pa.Rec != null && !pa.IsArray) slotTypes.AddRange(pa.Rec.Slots);
                            else slotTypes.Add(pa.IsArray ? VType.Int : pa.Type);
                            slot += pa.Size;
                            p.Params.Add(pa);
                            if (At(j).Kind == T.Comma) { j++; continue; }
                            break;
                        }
                    }
                    if (At(j).Kind != T.RParen) throw new UBasicError(
                        "expected ')' in parameter list", At(j).Line);
                    j++;
                }
                if (IsKw(At(j), "STATIC")) p.IsStatic = true;
                fi.ParamTypes = slotTypes.ToArray();
                fi.ParamCount = slot;
                p.Written = new bool[p.Params.Count];

                if (_procs.ContainsKey(p.Bare))
                    throw new UBasicError("duplicate definition of " + nameTok.Raw, nameTok.Line);
                p.Index = _c.Funcs.Count;
                _procs[p.Bare] = p;
                _c.Funcs.Add(fi);
            }
        }

        // ---- module and procedures -------------------------------------------

        private void CompileModule() {
            _p = 0;
            _cur = null;
            _labels = new Dictionary<string, int>();
            _labelFix = new List<LabelFix>();
            _c.EntryPoint = 0;

            SkipTerminators();
            while (Cur.Kind != T.EOF) {
                if (IsKw("SUB") || IsKw("FUNCTION")) { SkipProcedure(); }
                else Statement();
                SkipTerminators();
            }
            _c.Emit(Op.Halt, 0, 0, Line);
            ResolveLabels();

            // Procedure bodies are emitted after the module body.
            _p = 0;
            SkipTerminators();
            while (Cur.Kind != T.EOF) {
                if (IsKw("SUB") || IsKw("FUNCTION")) CompileProcedure();
                else SkipLine();
                SkipTerminators();
            }
        }

        private void SkipProcedure() {
            string kind = Cur.Text;
            int line = Line;
            _p++;
            while (Cur.Kind != T.EOF) {
                if (IsKw("END") && IsKw(Peek(1), kind)) { _p += 2; return; }
                _p++;
            }
            throw new UBasicError("missing END " + kind, line);
        }

        private void BeginProc(Proc p) {
            _cur = p;
            _lsyms = new Dictionary<string, Sym>();
            _localTypes = new List<VType>();
            _exitFix = new List<int>();
            _resultSym = null;

            // Parameters occupy the first local slots, in order.
            for (int i = 0; i < p.Params.Count; i++) {
                Param pa = p.Params[i];
                Sym s = new Sym();
                s.IsLocal = true;
                s.Slot = _localTypes.Count;
                s.Type = pa.Rec != null ? VType.Record : pa.Type;
                s.Rec = pa.Rec;
                s.IsArray = pa.IsArray;
                s.ParamIdx = pa.IsArray ? -1 : i;
                if (pa.IsArray) {
                    s.Desc = pa.IsAny ? -1 : MakeDesc(pa.Tok.Text, s.Type, pa.Rec);
                    _localTypes.Add(VType.Int);
                    _lsyms[pa.Tok.Text + "("] = s;
                } else {
                    if (pa.Rec != null) _localTypes.AddRange(pa.Rec.Slots);
                    else _localTypes.Add(pa.Type);
                    _lsyms[pa.AsDeclared ? pa.Tok.Text : TKey(pa.Tok)] = s;
                }
            }
            if (p.Fi.IsFunction) {
                _resultSym = new Sym();
                _resultSym.IsLocal = true;
                _resultSym.Type = p.Fi.ReturnType;
                _resultSym.Slot = _localTypes.Count;
                _localTypes.Add(p.Fi.ReturnType);
                p.Fi.ResultSlot = _resultSym.Slot;
            }
            p.Fi.Addr = _c.Here;
        }

        private void EndProc(Proc p, int line) {
            // Every EXIT SUB / EXIT FUNCTION lands here, so local arrays are
            // released on all paths.
            for (int i = 0; i < _exitFix.Count; i++) _c.Patch(_exitFix[i], _c.Here);
            foreach (KeyValuePair<string, Sym> kv in _lsyms) {
                Sym s = kv.Value;
                if (!s.IsArray || !s.Owned || !s.IsLocal) continue;
                LoadSym(s, 0, line);
                _c.Emit(Op.ArrFree, 0, 0, line);
            }
            _c.Emit(p.Fi.IsFunction ? Op.RetVal : Op.Ret, 0, 0, line);
            p.Fi.LocalCount = _localTypes.Count;
            p.Fi.LocalTypes = _localTypes.ToArray();
            _cur = null;
            _lsyms = null;
            _localTypes = null;
            _exitFix = null;
            _resultSym = null;
        }

        private void CompileProcedure() {
            string kind = Adv().Text;                   // SUB | FUNCTION
            Token nameTok = Adv();
            Proc p = _procs[nameTok.Text];

            _labels = new Dictionary<string, int>();
            _labelFix = new List<LabelFix>();
            BeginProc(p);

            if (Cur.Kind == T.LParen) {
                int close = MatchParen(_p);
                if (close < 0) throw Err("expected ')'");
                _p = close + 1;
            }
            AcceptKw("STATIC");

            SkipTerminators();
            while (!(IsKw("END") && IsKw(Peek(1), kind))) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing END " + kind, nameTok.Line);
                Statement();
                SkipTerminators();
            }
            int line = Line;
            _p += 2;                                     // END SUB / END FUNCTION
            EndProc(p, line);
            ResolveLabels();
        }

        /// <summary>DEF FNname(args) = expr, or the multi-line form ending in
        /// END DEF. Compiled in place as a by-value function whose other
        /// names are the module's own variables.</summary>
        private void DefFnStatement() {
            int line = Line;
            _p++;                                        // DEF
            Token nameTok = Adv();
            Proc p = _procs[nameTok.Text];
            if (_cur != null) throw new UBasicError("DEF FN is only allowed at module level", line);

            int over = _c.Emit(Op.Jmp, 0, 0, line);
            BeginProc(p);
            _defFn = true;
            if (Cur.Kind == T.LParen) _p = MatchParen(_p) + 1;

            if (Accept(T.Eq)) {
                CoerceTo(Expr(), p.Fi.ReturnType, line);
                StoreSym(_resultSym, 0, line);
            } else {
                SkipTerminators();
                while (!(IsKw("END") && IsKw(Peek(1), "DEF"))) {
                    if (Cur.Kind == T.EOF) throw new UBasicError("missing END DEF", line);
                    Statement();
                    SkipTerminators();
                }
                _p += 2;
            }
            _defFn = false;
            EndProc(p, line);
            _c.Patch(over, _c.Here);
        }

        private void DefineLabel(string key) {
            if (!_labels.ContainsKey(key)) _labels[key] = _c.Here;
        }

        private void LabelRef(int at, int line) {
            Token lbl = Adv();
            LabelFix f = new LabelFix();
            f.At = at; f.Line = line;
            if (lbl.Kind == T.IntLit) { f.Key = lbl.IVal.ToString(); f.Raw = f.Key; }
            else if (lbl.Kind == T.Ident) { f.Key = lbl.Text; f.Raw = lbl.Raw; }
            else throw new UBasicError("expected a label", line);
            _labelFix.Add(f);
        }

        private void ResolveLabels() {
            for (int i = 0; i < _labelFix.Count; i++) {
                int addr;
                if (!_labels.TryGetValue(_labelFix[i].Key, out addr))
                    throw new UBasicError("undefined label '" + _labelFix[i].Raw + "'", _labelFix[i].Line);
                _c.Patch(_labelFix[i].At, addr);
            }
            _labelFix.Clear();
        }

        /// <summary>By-reference arguments are implemented as copy-in /
        /// copy-out. The copy back is only wanted when the procedure really
        /// assigns that parameter (directly, or by passing it on to one that
        /// does); otherwise it is jumped over, which both saves time and stops
        /// a stale copy from overwriting a SHARED variable the procedure
        /// changed by name.</summary>
        private void FinishCopyOuts() {
            bool changed = true;
            while (changed) {
                changed = false;
                for (int i = 0; i < _forwards.Count; i++) {
                    Forward f = _forwards[i];
                    if (f.To.Written[f.ToParam] && !f.From.Written[f.FromParam]) {
                        f.From.Written[f.FromParam] = true;
                        changed = true;
                    }
                }
            }
            for (int i = 0; i < _copyFixes.Count; i++) {
                CopyFix f = _copyFixes[i];
                if (f.P.Written[f.Param] || f.End <= f.Start) continue;
                _c.Code[f.Start] = new Instr(Op.Jmp, f.End, 0, _c.Code[f.Start].Line);
            }
        }

        // ---- symbols ---------------------------------------------------------

        private int AddLocalSlots(VType type, TypeInfo rec) {
            int slot = _localTypes.Count;
            if (rec != null) _localTypes.AddRange(rec.Slots);
            else _localTypes.Add(type);
            return slot;
        }

        private int AddGlobalSlots(string name, VType type, TypeInfo rec) {
            int slot = _c.GlobalTypes.Count;
            if (rec != null) {
                for (int i = 0; i < rec.Slots.Count; i++) { _c.GlobalTypes.Add(rec.Slots[i]); _c.GlobalNames.Add(name); }
            } else {
                _c.GlobalTypes.Add(type);
                _c.GlobalNames.Add(name);
            }
            return slot;
        }

        private int MakeDesc(string name, VType type, TypeInfo rec) {
            ArrayInfo d = new ArrayInfo();
            d.Name = name;
            d.Type = rec != null ? VType.Record : type;
            if (rec != null) { d.Stride = rec.Size; d.SlotTypes = rec.Slots.ToArray(); }
            else { d.Stride = 1; d.SlotTypes = new[] { type }; }
            for (int i = 0; i < d.SlotTypes.Length; i++) if (d.SlotTypes[i] == VType.Str) d.HasStr = true;
            _c.Arrays.Add(d);
            return _c.Arrays.Count - 1;
        }

        /// <summary>Create a variable or array-handle symbol in the right
        /// place: a local slot, a module global, or -- for the locals of a
        /// STATIC procedure -- a hidden global that only this procedure
        /// can name.</summary>
        private Sym Declare(string key, VType type, TypeInfo rec, bool isArray, bool forceGlobal, bool forceStatic) {
            Sym s = new Sym();
            s.Type = rec != null ? VType.Record : type;
            s.Rec = rec;
            s.IsArray = isArray;
            VType slotType = isArray ? VType.Int : type;
            TypeInfo slotRec = isArray ? null : rec;
            if (_cur != null && !forceGlobal && !_defFn) {
                if (_cur.IsStatic || forceStatic) {
                    s.IsLocal = false;
                    s.Slot = AddGlobalSlots(_cur.Bare + "." + key, slotType, slotRec);
                    s.Once = isArray;
                } else {
                    s.IsLocal = true;
                    s.Slot = AddLocalSlots(slotType, slotRec);
                    s.Owned = isArray;
                }
                _lsyms[key] = s;
            } else {
                s.IsLocal = false;
                s.Slot = AddGlobalSlots(key, slotType, slotRec);
                _gsyms[key] = s;
            }
            if (isArray) s.Desc = MakeDesc(key.TrimEnd('('), type, rec);
            return s;
        }

        private bool Visible(string key) { return _defFn || _shared.Contains(key); }

        /// <summary>QBasic scoping: inside a procedure a name is local unless
        /// it was declared SHARED. A name declared with AS is found without
        /// its sigil; anything else is keyed by name plus type.</summary>
        private Sym FindScalar(Token t) {
            if (_cur != null && _resultSym != null && t.Text == _cur.Bare) return _resultSym;
            string k1 = t.HadSigil ? null : t.Text;
            string k2 = TKey(t);
            Sym s;
            if (_cur != null) {
                if (k1 != null && _lsyms.TryGetValue(k1, out s)) return s;
                if (_lsyms.TryGetValue(k2, out s)) return s;
                if (k1 != null && Visible(k1) && _gsyms.TryGetValue(k1, out s)) return s;
                if (Visible(k2) && _gsyms.TryGetValue(k2, out s)) return s;
                return null;
            }
            if (k1 != null && _gsyms.TryGetValue(k1, out s)) return s;
            if (_gsyms.TryGetValue(k2, out s)) return s;
            return null;
        }

        private Sym GetScalar(Token t) {
            Sym s = FindScalar(t);
            if (s != null) return s;
            return Declare(TKey(t), t.Sig, null, false, false, false);
        }

        private Sym FindArray(string bare) {
            string key = bare + "(";
            Sym s;
            if (_cur != null) {
                if (_lsyms.TryGetValue(key, out s)) return s;
                if (Visible(key) && _gsyms.TryGetValue(key, out s)) return s;
                return null;
            }
            return _gsyms.TryGetValue(key, out s) ? s : null;
        }

        private Sym NewTemp(VType t) {
            Sym s = new Sym();
            s.Type = t;
            if (_cur != null) { s.IsLocal = true; s.Slot = AddLocalSlots(t, null); }
            else { s.IsLocal = false; s.Slot = AddGlobalSlots("$t", t, null); }
            return s;
        }

        private void LoadSym(Sym s, int off, int line) {
            _c.Emit(s.IsLocal ? Op.LoadL : Op.LoadG, s.Slot + off, 0, line);
        }
        private void StoreSym(Sym s, int off, int line) {
            _c.Emit(s.IsLocal ? Op.StoreL : Op.StoreG, s.Slot + off, 0, line);
            if (s.ParamIdx >= 0 && _cur != null) _cur.Written[s.ParamIdx] = true;
        }

        // ---- lvalues ---------------------------------------------------------

        /// <summary>Old BASIC allowed periods inside names (my.var). A dot
        /// after something that is not a TYPE variable is therefore part of
        /// the name, and the pieces are glued back together here.</summary>
        private Token MergeDotted(Token t) {
            if (Cur.Kind != T.Dot) return t;
            Sym s = FindScalar(t);
            if (s != null && s.Rec != null) return t;
            while (Cur.Kind == T.Dot && (Peek(1).Kind == T.Ident || Peek(1).Kind == T.Keyword)) {
                Token part = Peek(1);
                _p += 2;
                t.Text = t.Text + "." + part.Text;
                t.Raw = t.Raw + "." + part.Raw;
                if (part.Kind == T.Ident && part.HadSigil) { t.Sig = part.Sig; t.HadSigil = true; }
            }
            return t;
        }

        /// <summary>Parse name[(subscripts)][.field...]. With temps false the
        /// array handle and subscripts are left on the stack for exactly one
        /// LVLoad or LVStore. With temps true the subscripts are evaluated
        /// once into temporaries, so the lvalue can be used any number of
        /// times and its address is fixed at this point in the program.</summary>
        private LV ParseLV(bool temps) {
            int line = Line;
            Token t = Adv();
            if (t.Kind != T.Ident) throw new UBasicError("expected a variable name, got '" + Describe(t) + "'", line);
            LV lv = new LV();
            if (Cur.Kind == T.LParen) {
                Sym arr = FindArray(t.Text);
                if (arr == null) throw new UBasicError("array '" + t.Raw + "' is not defined (DIM it first)", line);
                _p++;
                lv.Base = arr; lv.Elem = true;
                if (!temps) LoadSym(arr, 0, line);
                List<Sym> tl = temps ? new List<Sym>() : null;
                int n = 0;
                while (true) {
                    CoerceTo(Expr(), VType.Int, line);
                    if (temps) { Sym tmp = NewTemp(VType.Int); StoreSym(tmp, 0, line); tl.Add(tmp); }
                    n++;
                    if (Accept(T.Comma)) continue;
                    break;
                }
                Expect(T.RParen, "')'");
                lv.NDims = n;
                lv.Temps = temps ? tl.ToArray() : null;
                lv.Type = arr.Type; lv.Rec = arr.Rec;
                if (arr.Desc < 0 && Cur.Kind == T.Dot)
                    throw new UBasicError("'" + t.Raw + "' was declared AS ANY: give the parameter its TYPE name " +
                                          "in the SUB line so its fields can be resolved", line);
            } else {
                t = MergeDotted(t);
                Sym s = GetScalar(t);
                lv.Base = s; lv.Type = s.Type; lv.Rec = s.Rec;
            }
            while (Cur.Kind == T.Dot) {
                if (lv.Rec == null) throw new UBasicError("'" + t.Raw + "' is not a TYPE variable", line);
                _p++;
                Token f = Adv();
                Field fd = (f.Kind == T.Ident || f.Kind == T.Keyword) ? lv.Rec.Find(f.Text) : null;
                if (fd == null) throw new UBasicError("TYPE " + lv.Rec.Name + " has no field '" + Describe(f) + "'", line);
                lv.Offset += fd.Offset;
                lv.Type = fd.Rec != null ? VType.Record : fd.Type;
                lv.Rec = fd.Rec;
            }
            return lv;
        }

        private int LVSize(LV lv) { return lv.Rec != null ? lv.Rec.Size : 1; }

        private void LVAddr(LV lv, int line) {
            LoadSym(lv.Base, 0, line);
            for (int i = 0; i < lv.Temps.Length; i++) LoadSym(lv.Temps[i], 0, line);
        }

        private void LVLoad(LV lv, int d, int line) {
            if (!lv.Elem) { LoadSym(lv.Base, lv.Offset + d, line); return; }
            if (lv.Temps != null) LVAddr(lv, line);
            _c.Emit(Op.ArrLoad, lv.Offset + d, lv.NDims, line);
        }

        /// <summary>Call before pushing the value to store, when the lvalue
        /// was parsed with temps.</summary>
        private void LVPrep(LV lv, int line) {
            if (lv.Elem && lv.Temps != null) LVAddr(lv, line);
        }

        private void LVStore(LV lv, int d, int line, bool mark) {
            if (!lv.Elem) {
                _c.Emit(lv.Base.IsLocal ? Op.StoreL : Op.StoreG, lv.Base.Slot + lv.Offset + d, 0, line);
                if (mark && lv.Base.ParamIdx >= 0 && _cur != null) _cur.Written[lv.Base.ParamIdx] = true;
                return;
            }
            _c.Emit(Op.ArrStore, lv.Offset + d, lv.NDims, line);
        }

        // ---- statements -----------------------------------------------------

        private void Statement() {
            if (Cur.Kind == T.EOL || Cur.Kind == T.Colon) return;

            bool lineStart = _p == 0 || _t[_p - 1].Kind == T.EOL;
            if (lineStart) {
                if (Cur.Kind == T.IntLit) {                       // line number
                    DefineLabel(Cur.IVal.ToString());
                    _p++;
                    if (AtEnd()) return;
                } else if (Cur.Kind == T.Ident && Peek(1).Kind == T.Colon && !Cur.HadSigil &&
                           !_procs.ContainsKey(Cur.Text)) {
                    DefineLabel(Cur.Text);
                    _p += 2;
                    return;
                }
            }
            int line = Line;

            if (Cur.Kind == T.Keyword) {
                switch (Cur.Text) {
                    case "LET":       _p++; AssignStatement(); return;
                    case "DIM":       DimStatement(false, false); return;
                    case "REDIM":     DimStatement(true, false); return;
                    case "COMMON":    DimStatement(false, false); return;
                    case "STATIC":    DimStatement(false, true); return;
                    case "SHARED":    SharedStatement(); return;
                    case "CONST":     ConstStatement(); return;
                    case "IF":        IfStatement(); return;
                    case "FOR":       ForStatement(); return;
                    case "WHILE":     WhileStatement(); return;
                    case "DO":        DoStatement(); return;
                    case "SELECT":    SelectStatement(); return;
                    case "EXIT":      ExitStatement(); return;
                    case "GOTO":      _p++; LabelRef(_c.Emit(Op.Jmp, 0, 0, line), line); EndOfStatement(); return;
                    case "GOSUB":     _p++; LabelRef(_c.Emit(Op.Gosub, 0, 0, line), line); EndOfStatement(); return;
                    case "RETURN":
                        _p++;
                        if (AtEnd()) _c.Emit(Op.Return, -1, 0, line);
                        else LabelRef(_c.Emit(Op.Return, 0, 0, line), line);
                        EndOfStatement();
                        return;
                    case "ON":        OnStatement(); return;
                    case "CALL":      _p++; CallStatement(true); return;
                    case "PRINT":     PrintStatement(); return;
                    case "INPUT":     InputStatement(false); return;
                    case "READ":      ReadStatement(); return;
                    case "RESTORE":   RestoreStatement(); return;
                    case "SWAP":      SwapStatement(); return;
                    case "ERASE":     EraseStatement(); return;
                    case "CLS":       ClsStatement(); return;
                    case "SCREEN":    ScreenStatement(); return;
                    case "WIDTH":     WidthStatement(); return;
                    case "VIEW":      ViewStatement(); return;
                    case "PSET":      PSetStatement(false); return;
                    case "PRESET":    PSetStatement(true); return;
                    case "LINE":      LineStatement(); return;
                    case "CIRCLE":    CircleStatement(); return;
                    case "PAINT":     PaintStatement(); return;
                    case "GET":       GetStatement(); return;
                    case "PUT":       PutStatement(); return;
                    case "COLOR":     TwoOptional("__color"); return;
                    case "LOCATE":    LocateStatement(); return;
                    case "PALETTE":   PaletteStatement(); return;
                    case "RANDOMIZE": RandomizeStatement(); return;
                    case "SOUND":     SoundStatement(); return;
                    case "BEEP":      BeepStatement(); return;
                    case "PLAY":      PlayStatement(); return;
                    case "SLEEP":     SleepStatement(); return;
                    case "WAIT":
                        _p++;
                        if (AtEnd()) _c.Emit(Op.Wait, 0, 0, line);    // uBasic: end the frame
                        else SkipStatement();                          // QBasic: WAIT port, mask
                        EndOfStatement();
                        return;
                    case "END":
                        if (Peek(1).Kind == T.Keyword && (Peek(1).Text == "IF" || Peek(1).Text == "SELECT" ||
                            Peek(1).Text == "SUB" || Peek(1).Text == "FUNCTION" || Peek(1).Text == "TYPE" ||
                            Peek(1).Text == "DEF"))
                            throw new UBasicError("END " + Peek(1).Text + " without a matching " + Peek(1).Text, line);
                        _p++; _c.Emit(Op.Halt, 0, 0, line); EndOfStatement(); return;
                    case "STOP": case "SYSTEM":
                        _p++; _c.Emit(Op.Halt, 0, 0, line); EndOfStatement(); return;
                    case "RESUME":
                        _p++; SkipStatement(); EmitHost("__resume", 0, line); return;

                    // Declarations already handled by the pre-passes.
                    case "DECLARE":   SkipLine(); return;
                    case "DEFINT": case "DEFSNG": case "DEFDBL": case "DEFLNG": case "DEFSTR":
                        SkipStatement(); return;
                    case "TYPE":
                        while (!(IsKw("END") && IsKw(Peek(1), "TYPE"))) {
                            if (Cur.Kind == T.EOF) throw new UBasicError("missing END TYPE", line);
                            _p++;
                        }
                        _p += 2;
                        return;
                    case "DEF":
                        if (Peek(1).Kind == T.Ident && Peek(1).Text.StartsWith("FN")) { DefFnStatement(); return; }
                        SkipStatement();          // DEF SEG
                        return;
                    case "OPTION":
                        _p++;
                        if (IsIdent("BASE")) { _p++; _optionBase = ConstInt(); }
                        else SkipStatement();
                        EndOfStatement();
                        return;

                    // Hardware pokes and settings with no meaning here.
                    case "POKE": case "OUT": case "CLEAR":
                        SkipStatement(); return;
                    case "ERROR":
                        throw new UBasicError("ERROR / ON ERROR handling is not supported", line);
                }
                throw Err("unexpected keyword '" + Cur.Text + "'");
            }

            if (Cur.Kind == T.Data) { _p++; return; }

            if (Cur.Kind == T.Ident) {
                Token t = Cur;
                if (t.Text == "MID" && t.HadSigil && t.Sig == VType.Str && Peek(1).Kind == T.LParen) {
                    MidStatement();
                    return;
                }
                if (!t.HadSigil && Peek(1).Kind != T.Eq) {
                    // KEY OFF, TRON and friends: accepted and ignored.
                    if ((t.Text == "KEY" && Peek(1).Kind != T.LParen) || t.Text == "TRON" || t.Text == "TROFF" ||
                        t.Text == "PCOPY") { SkipStatement(); return; }
                    if (Unsupported.Contains(t.Text) && FindScalar(t) == null && !_procs.ContainsKey(t.Text))
                        throw new UBasicError(t.Text + " is not supported", line);
                }
                Proc p;
                if (_procs.TryGetValue(t.Text, out p) && !(p.Fi.IsFunction && Peek(1).Kind == T.Eq)) {
                    CallStatement(false);
                    return;
                }
                AssignStatement();
                return;
            }
            throw Err("expected a statement");
        }

        private static readonly HashSet<string> Unsupported = new HashSet<string> {
            "OPEN", "CLOSE", "DRAW", "WINDOW", "CHAIN", "SHELL", "FILES", "KILL", "FIELD", "LSET",
            "RSET", "LPRINT", "WRITE", "RUN", "CHDIR", "MKDIR", "RMDIR", "SEEK", "LOCK", "UNLOCK"
        };

        private struct Mark { public int P, Code, Fixes, Fwds; }

        private Mark Save() {
            Mark m = new Mark();
            m.P = _p; m.Code = _c.Here;
            m.Fixes = _copyFixes.Count; m.Fwds = _forwards.Count;
            return m;
        }
        private void Rewind(Mark m) {
            _p = m.P;
            _c.Code.RemoveRange(m.Code, _c.Here - m.Code);
            _copyFixes.RemoveRange(m.Fixes, _copyFixes.Count - m.Fixes);
            _forwards.RemoveRange(m.Fwds, _forwards.Count - m.Fwds);
        }

        private void AssignStatement() {
            int line = Line;
            Mark m = Save();
            LV lv = ParseLV(false);
            if (lv.Rec != null) {
                // Whole-record assignment: copy slot by slot.
                Rewind(m);
                lv = ParseLV(true);
                Expect(T.Eq, "'='");
                LV src = ParseLV(true);
                if (src.Rec != lv.Rec) throw new UBasicError("type mismatch: both sides must be the same TYPE", line);
                for (int i = 0; i < lv.Rec.Size; i++) {
                    LVPrep(lv, line);
                    LVLoad(src, i, line);
                    LVStore(lv, i, line, true);
                }
                EndOfStatement();
                return;
            }
            Expect(T.Eq, "'='");
            CoerceTo(Expr(), lv.Type, line);
            LVStore(lv, 0, line, true);
            EndOfStatement();
        }

        /// <summary>MID$(s$, start[, len]) = text$</summary>
        private void MidStatement() {
            int line = Line;
            _p += 2;                                     // MID$ (
            LV lv = ParseLV(true);
            if (lv.Type != VType.Str) throw new UBasicError("MID$ needs a string variable", line);
            LVPrep(lv, line);
            LVLoad(lv, 0, line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Int, line);
            if (Accept(T.Comma)) CoerceTo(Expr(), VType.Int, line);
            else _c.Emit(Op.PushI, -1, 0, line);
            Expect(T.RParen, "')'");
            Expect(T.Eq, "'='");
            CoerceTo(Expr(), VType.Str, line);
            EmitHost("__midset", 4, line);
            LVStore(lv, 0, line, true);
            EndOfStatement();
        }

        private void OptAsType(ref VType ty, out TypeInfo rec, out bool asDecl) {
            rec = null; asDecl = false;
            if (!AcceptKw("AS")) return;
            ParseAsTypeAt(ref _p, out ty, out rec);
            asDecl = true;
        }

        /// <summary>DIM, REDIM, COMMON and STATIC. Arrays are allocated when
        /// the statement runs, so bounds may be any expression.</summary>
        private void DimStatement(bool redim, bool isStatic) {
            int line = Line;
            _p++;
            bool shared = AcceptKw("SHARED");
            if (isStatic && _cur == null) throw new UBasicError("STATIC is only allowed inside a SUB or FUNCTION", line);
            bool toGlobal = shared || _cur == null;

            while (true) {
                Token nameTok = Adv();
                if (nameTok.Kind != T.Ident) throw new UBasicError("expected a name, got '" + Describe(nameTok) + "'", line);
                VType ty = nameTok.Sig;
                TypeInfo rec;
                bool asDecl;

                if (Cur.Kind == T.LParen) {
                    int close = MatchParen(_p);
                    if (close < 0) throw Err("expected ')'");
                    if (IsKw(At(close + 1), "AS")) {
                        int j = close + 2;
                        ParseAsTypeAt(ref j, out ty, out rec);
                    } else rec = null;

                    string akey = nameTok.Text + "(";
                    Sym arr;
                    if (redim || toGlobal) arr = FindArray(nameTok.Text);
                    else _lsyms.TryGetValue(akey, out arr);
                    if (arr == null) {
                        arr = Declare(akey, ty, rec, true, toGlobal, isStatic);
                    } else if (arr.Rec != rec || (arr.Type == VType.Str) != (ty == VType.Str)) {
                        throw new UBasicError("array " + nameTok.Raw + " is already declared with a different type", line);
                    }
                    if (shared) _shared.Add(akey);

                    int once = -1;
                    if (arr.Once && !redim) {
                        LoadSym(arr, 0, line);
                        once = _c.Emit(Op.JmpT, 0, 0, line);
                    }
                    LoadSym(arr, 0, line);
                    _c.Emit(Op.ArrFree, 0, 0, line);

                    _p++;                                 // (
                    int n = 0;
                    if (Cur.Kind == T.RParen) throw new UBasicError("array " + nameTok.Raw + " needs bounds", line);
                    while (true) {
                        // lower TO upper, or just upper.
                        bool hasTo = false;
                        int depth = 0;
                        for (int j = _p; j < close; j++) {
                            T k = _t[j].Kind;
                            if (k == T.LParen) depth++;
                            else if (k == T.RParen) depth--;
                            else if (depth == 0 && k == T.Comma) break;
                            else if (depth == 0 && IsKw(_t[j], "TO")) { hasTo = true; break; }
                        }
                        if (hasTo) {
                            CoerceTo(Expr(), VType.Int, line);
                            ExpectKw("TO");
                        } else _c.Emit(Op.PushI, _optionBase, 0, line);
                        CoerceTo(Expr(), VType.Int, line);
                        n++;
                        if (Accept(T.Comma)) continue;
                        break;
                    }
                    Expect(T.RParen, "')'");
                    if (AcceptKw("AS")) { VType t2; TypeInfo r2; ParseAsTypeAt(ref _p, out t2, out r2); }

                    _c.Emit(Op.ArrNew, arr.Desc, n, line);
                    StoreSym(arr, 0, line);
                    if (once >= 0) _c.Patch(once, _c.Here);
                } else {
                    OptAsType(ref ty, out rec, out asDecl);
                    string key = asDecl ? nameTok.Text : TKey(nameTok);
                    bool exists = toGlobal ? _gsyms.ContainsKey(key) : _lsyms.ContainsKey(key);
                    if (!exists) Declare(key, ty, rec, false, toGlobal, isStatic);
                    if (shared) _shared.Add(key);
                }
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        /// <summary>SHARED a, b(): make module-level names visible inside
        /// this one procedure.</summary>
        private void SharedStatement() {
            int line = Line;
            _p++;
            if (_cur == null) throw new UBasicError("SHARED is only allowed inside a SUB or FUNCTION", line);
            while (true) {
                Token nameTok = Adv();
                if (nameTok.Kind != T.Ident) throw new UBasicError("expected a name after SHARED", line);
                VType ty = nameTok.Sig;
                if (Accept(T.LParen)) {
                    Expect(T.RParen, "')'");
                    TypeInfo r; bool a;
                    OptAsType(ref ty, out r, out a);
                    Sym arr;
                    if (!_gsyms.TryGetValue(nameTok.Text + "(", out arr))
                        throw new UBasicError("no module-level array named " + nameTok.Raw, line);
                    _lsyms[nameTok.Text + "("] = arr;
                } else {
                    TypeInfo rec; bool asDecl;
                    OptAsType(ref ty, out rec, out asDecl);
                    string key = asDecl ? nameTok.Text : TKey(nameTok);
                    Sym s;
                    if (!_gsyms.TryGetValue(key, out s)) {
                        s = new Sym();
                        s.Type = rec != null ? VType.Record : ty; s.Rec = rec;
                        s.Slot = AddGlobalSlots(key, ty, rec);
                        _gsyms[key] = s;
                    }
                    _lsyms[key] = s;
                }
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        private void ConstStatement() {
            _p++;
            while (true) {
                Token nameTok = Adv();
                if (nameTok.Kind != T.Ident) throw Err("expected a constant name");
                Expect(T.Eq, "'='");
                int line = Line;
                ConstVal cv = ConstExpr();
                if (nameTok.HadSigil) {
                    if ((cv.Type == VType.Str) != (nameTok.Sig == VType.Str))
                        throw new UBasicError("CONST " + nameTok.Raw + ": type mismatch", line);
                    if (cv.Type != VType.Str) {
                        cv.Type = nameTok.Sig;
                        if (IsInt(cv.Type)) cv.Num = Math.Round(cv.Num);
                    }
                }
                _consts[nameTok.Text] = cv;
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        private int ConstInt() {
            int line = Line;
            ConstVal cv = ConstExpr();
            if (cv.Type == VType.Str) throw new UBasicError("expected a constant number", line);
            return (int)Math.Round(cv.Num);
        }

        // ---- constant expressions ----------------------------------------------
        // CONST values are folded at compile time. A tiny evaluator with the
        // same precedence as the real expression parser.

        private static ConstVal CNum(double v, VType t) {
            ConstVal c = new ConstVal();
            c.Num = v; c.Type = t;
            if (IsInt(t)) {
                c.Num = Math.Round(v);
                if (c.Num > 32767 || c.Num < -32768) c.Type = VType.Long;
                if (c.Num > int.MaxValue || c.Num < int.MinValue) c.Type = VType.Double;
            }
            return c;
        }
        private static VType CJoin(ConstVal a, ConstVal b) {
            if (IsInt(a.Type) && IsInt(b.Type)) return (a.Type == VType.Long || b.Type == VType.Long) ? VType.Long : VType.Int;
            return (a.Type == VType.Double || b.Type == VType.Double) ? VType.Double : VType.Single;
        }
        private double CN(ConstVal c, int line) {
            if (c.Type == VType.Str) throw new UBasicError("type mismatch in constant expression", line);
            return c.Num;
        }
        private int CI(ConstVal c, int line) { return (int)Math.Round(CN(c, line)); }

        private ConstVal ConstExpr() {
            ConstVal a = ConstAnd();
            while (IsKw("OR") || IsKw("XOR")) {
                bool x = Cur.Text == "XOR"; int line = Line; _p++;
                ConstVal b = ConstAnd();
                a = CNum(x ? (CI(a, line) ^ CI(b, line)) : (CI(a, line) | CI(b, line)), VType.Int);
            }
            return a;
        }
        private ConstVal ConstAnd() {
            ConstVal a = ConstNot();
            while (IsKw("AND")) {
                int line = Line; _p++;
                ConstVal b = ConstNot();
                a = CNum(CI(a, line) & CI(b, line), VType.Int);
            }
            return a;
        }
        private ConstVal ConstNot() {
            if (IsKw("NOT")) { int line = Line; _p++; return CNum(~CI(ConstNot(), line), VType.Int); }
            return ConstRel();
        }
        private ConstVal ConstRel() {
            ConstVal a = ConstAdd();
            while (Cur.Kind >= T.Eq && Cur.Kind <= T.Ge) {
                T k = Cur.Kind; int line = Line; _p++;
                ConstVal b = ConstAdd();
                int cmp;
                if (a.Type == VType.Str || b.Type == VType.Str) {
                    if (a.Type != b.Type) throw new UBasicError("type mismatch in constant expression", line);
                    cmp = string.CompareOrdinal(a.Str, b.Str);
                } else cmp = a.Num.CompareTo(b.Num);
                bool r = k == T.Eq ? cmp == 0 : k == T.Ne ? cmp != 0 : k == T.Lt ? cmp < 0 :
                         k == T.Le ? cmp <= 0 : k == T.Gt ? cmp > 0 : cmp >= 0;
                a = CNum(r ? -1 : 0, VType.Int);
            }
            return a;
        }
        private ConstVal ConstAdd() {
            ConstVal a = ConstMod();
            while (Cur.Kind == T.Plus || Cur.Kind == T.Minus) {
                bool plus = Cur.Kind == T.Plus; int line = Line; _p++;
                ConstVal b = ConstMod();
                if (a.Type == VType.Str && b.Type == VType.Str && plus) {
                    ConstVal s = new ConstVal(); s.Type = VType.Str; s.Str = a.Str + b.Str; a = s;
                } else a = CNum(plus ? CN(a, line) + CN(b, line) : CN(a, line) - CN(b, line), CJoin(a, b));
            }
            return a;
        }
        private ConstVal ConstMod() {
            ConstVal a = ConstIDiv();
            while (IsKw("MOD")) {
                int line = Line; _p++;
                ConstVal b = ConstIDiv();
                if (CI(b, line) == 0) throw new UBasicError("division by zero in constant expression", line);
                a = CNum(CI(a, line) % CI(b, line), VType.Int);
            }
            return a;
        }
        private ConstVal ConstIDiv() {
            ConstVal a = ConstMul();
            while (Cur.Kind == T.Backslash) {
                int line = Line; _p++;
                ConstVal b = ConstMul();
                if (CI(b, line) == 0) throw new UBasicError("division by zero in constant expression", line);
                a = CNum(CI(a, line) / CI(b, line), VType.Int);
            }
            return a;
        }
        private ConstVal ConstMul() {
            ConstVal a = ConstUnary();
            while (Cur.Kind == T.Star || Cur.Kind == T.Slash) {
                bool mul = Cur.Kind == T.Star; int line = Line; _p++;
                ConstVal b = ConstUnary();
                if (mul) a = CNum(CN(a, line) * CN(b, line), CJoin(a, b));
                else {
                    if (CN(b, line) == 0) throw new UBasicError("division by zero in constant expression", line);
                    VType jt = CJoin(a, b);
                    a = CNum(CN(a, line) / CN(b, line), jt == VType.Double || jt == VType.Long ? VType.Double : VType.Single);
                }
            }
            return a;
        }
        private ConstVal ConstUnary() {
            if (Cur.Kind == T.Minus) { int line = Line; _p++; ConstVal a = ConstUnary(); return CNum(-CN(a, line), a.Type); }
            if (Cur.Kind == T.Plus) { _p++; return ConstUnary(); }
            ConstVal v = ConstPrim();
            while (Cur.Kind == T.Caret) {
                int line = Line; _p++;
                ConstVal e = Cur.Kind == T.Minus ? ConstUnary() : ConstPrim();
                v = CNum(Math.Pow(CN(v, line), CN(e, line)), VType.Single);
            }
            return v;
        }
        private ConstVal ConstPrim() {
            int line = Line;
            Token t = Adv();
            if (t.Kind == T.IntLit) return CNum(t.IVal, t.Sig);
            if (t.Kind == T.SingleLit) return CNum(t.FVal, t.Sig);
            if (t.Kind == T.StrLit) { ConstVal s = new ConstVal(); s.Type = VType.Str; s.Str = t.Text; return s; }
            if (t.Kind == T.LParen) { ConstVal v = ConstExpr(); Expect(T.RParen, "')'"); return v; }
            if (t.Kind == T.Ident) {
                ConstVal cv;
                if (_consts.TryGetValue(t.Text, out cv)) return cv;
            }
            throw new UBasicError("a constant value is needed here, got '" + Describe(t) + "'", line);
        }

        // ---- control flow ------------------------------------------------------

        /// <summary>An expression used as a truth value. A float is true when
        /// it is not zero, rather than when it rounds to something non-zero.</summary>
        private void Cond(int line) {
            VType t = Expr();
            if (t == VType.Str || t == VType.Record)
                throw new UBasicError("type mismatch: a condition must be a number", line);
            if (IsFlt(t)) {
                _c.Emit(Op.PushF, _c.AddFloat(0.0), 0, line);
                _c.Emit(Op.NeF, 0, 0, line);
            }
        }

        private bool IsEndIf() { return IsKw("ENDIF") || (IsKw("END") && IsKw(Peek(1), "IF")); }
        private void SkipEndIf() { if (IsKw("ENDIF")) _p++; else _p += 2; }

        private void IfStatement() {
            int line = Line;
            _p++;
            Cond(line);
            if (!AcceptKw("THEN") && !IsKw("GOTO")) throw Err("expected THEN");

            List<int> endJumps = new List<int>();

            // Single-line form: IF c THEN stmt [: stmt ...] [ELSE stmt [: stmt ...]]
            // Everything after THEN on the line belongs to the THEN clause,
            // colon-separated statements included -- this is what QBasic does.
            if (Cur.Kind != T.EOL && Cur.Kind != T.EOF) {
                int jf = _c.Emit(Op.JmpF, 0, 0, line);
                SingleLineBody(line);
                if (IsKw("ELSE")) {
                    _p++;
                    int je = _c.Emit(Op.Jmp, 0, 0, line);
                    _c.Patch(jf, _c.Here);
                    SingleLineBody(line);
                    _c.Patch(je, _c.Here);
                } else {
                    _c.Patch(jf, _c.Here);
                }
                return;
            }

            // Block form
            while (true) {
                int jf = _c.Emit(Op.JmpF, 0, 0, line);
                SkipTerminators();
                while (!IsKw("ELSEIF") && !IsKw("ELSE") && !IsEndIf()) {
                    if (Cur.Kind == T.EOF) throw new UBasicError("missing END IF", line);
                    Statement();
                    SkipTerminators();
                }
                if (IsKw("ELSEIF")) {
                    endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                    _c.Patch(jf, _c.Here);
                    _p++;
                    Cond(Line);
                    ExpectKw("THEN");
                    continue;
                }
                if (IsKw("ELSE")) {
                    endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                    _c.Patch(jf, _c.Here);
                    _p++;
                    SkipTerminators();
                    while (!IsEndIf()) {
                        if (Cur.Kind == T.EOF) throw new UBasicError("missing END IF", line);
                        Statement();
                        SkipTerminators();
                    }
                    SkipEndIf();
                    break;
                }
                _c.Patch(jf, _c.Here);
                SkipEndIf();
                break;
            }
            for (int i = 0; i < endJumps.Count; i++) _c.Patch(endJumps[i], _c.Here);
        }

        /// <summary>One or more colon-separated statements up to end of line
        /// or an ELSE, used by the single-line IF form. A bare line number
        /// is an implied GOTO.</summary>
        private void SingleLineBody(int line) {
            if (Cur.Kind == T.IntLit) {
                LabelRef(_c.Emit(Op.Jmp, 0, 0, line), line);
                return;
            }
            Statement();
            while (Cur.Kind == T.Colon) {
                _p++;
                if (Cur.Kind == T.EOL || Cur.Kind == T.EOF || IsKw("ELSE")) break;
                Statement();
            }
        }

        private void ForStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind != T.Ident) throw Err("expected a loop variable");
            Token nameTok = MergeDotted(Adv());
            Sym v = GetScalar(nameTok);
            if (!IsInt(v.Type) && !IsFlt(v.Type)) throw new UBasicError("FOR variable must be a simple number", line);
            bool isInt = IsInt(v.Type);

            Expect(T.Eq, "'='");
            CoerceTo(Expr(), v.Type, line);
            StoreSym(v, 0, line);

            ExpectKw("TO");
            Sym lim = NewTemp(v.Type);
            CoerceTo(Expr(), v.Type, line);
            StoreSym(lim, 0, line);

            Sym stp = NewTemp(v.Type);
            if (AcceptKw("STEP")) CoerceTo(Expr(), v.Type, line);
            else EmitConst(v.Type, 1, line);
            StoreSym(stp, 0, line);

            LoopCtx ctx = new LoopCtx();
            ctx.Kind = "FOR";
            ctx.Top = _c.Here;
            _loops.Add(ctx);

            // Continue while (limit - i) * step >= 0. One test that is correct
            // for both directions, and for a step that is not a constant.
            LoadSym(lim, 0, line);
            LoadSym(v, 0, line);
            _c.Emit(isInt ? Op.SubI : Op.SubF, 0, 0, line);
            LoadSym(stp, 0, line);
            _c.Emit(isInt ? Op.MulI : Op.MulF, 0, 0, line);
            EmitConst(v.Type, 0, line);
            _c.Emit(isInt ? Op.GeI : Op.GeF, 0, 0, line);
            int exitJ = _c.Emit(Op.JmpF, 0, 0, line);

            SkipTerminators();
            while (!IsKw("NEXT") && !_nextComma) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing NEXT", line);
                Statement();
                SkipTerminators();
            }
            // NEXT i, j closes two loops: the inner one leaves the comma
            // consumed and this flag set for the outer one.
            if (_nextComma) _nextComma = false;
            else _p++;
            if (Cur.Kind == T.Ident) {
                MergeDotted(Adv());
                if (Accept(T.Comma)) _nextComma = true;
            }

            LoadSym(v, 0, line);
            LoadSym(stp, 0, line);
            _c.Emit(isInt ? Op.AddI : Op.AddF, 0, 0, line);
            StoreSym(v, 0, line);
            _c.Emit(Op.Jmp, ctx.Top, 0, line);
            _c.Patch(exitJ, _c.Here);
            FinishLoop(ctx);
        }

        private void EmitConst(VType t, int v, int line) {
            if (IsInt(t)) _c.Emit(Op.PushI, v, 0, line);
            else _c.Emit(Op.PushF, _c.AddFloat(v), 0, line);
        }

        private void WhileStatement() {
            int line = Line;
            _p++;
            LoopCtx ctx = new LoopCtx();
            ctx.Kind = "WHILE";
            ctx.Top = _c.Here;
            _loops.Add(ctx);

            Cond(line);
            int exitJ = _c.Emit(Op.JmpF, 0, 0, line);
            SkipTerminators();
            while (!IsKw("WEND")) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing WEND", line);
                Statement();
                SkipTerminators();
            }
            _p++;
            _c.Emit(Op.Jmp, ctx.Top, 0, line);
            _c.Patch(exitJ, _c.Here);
            FinishLoop(ctx);
        }

        private void DoStatement() {
            int line = Line;
            _p++;
            LoopCtx ctx = new LoopCtx();
            ctx.Kind = "DO";
            ctx.Top = _c.Here;
            _loops.Add(ctx);

            int exitJ = -1;
            if (AcceptKw("WHILE")) {
                Cond(line);
                exitJ = _c.Emit(Op.JmpF, 0, 0, line);
            } else if (AcceptKw("UNTIL")) {
                Cond(line);
                exitJ = _c.Emit(Op.JmpT, 0, 0, line);
            }

            SkipTerminators();
            while (!IsKw("LOOP")) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing LOOP", line);
                Statement();
                SkipTerminators();
            }
            int lline = Line;
            _p++;

            if (AcceptKw("WHILE")) {
                Cond(lline);
                _c.Emit(Op.JmpT, ctx.Top, 0, lline);
            } else if (AcceptKw("UNTIL")) {
                Cond(lline);
                _c.Emit(Op.JmpF, ctx.Top, 0, lline);
            } else {
                _c.Emit(Op.Jmp, ctx.Top, 0, lline);
            }
            if (exitJ >= 0) _c.Patch(exitJ, _c.Here);
            FinishLoop(ctx);
        }

        private void FinishLoop(LoopCtx ctx) {
            for (int i = 0; i < ctx.ExitJumps.Count; i++) _c.Patch(ctx.ExitJumps[i], _c.Here);
            _loops.RemoveAt(_loops.Count - 1);
        }

        private bool EndSelect() { return IsKw("END") && IsKw(Peek(1), "SELECT"); }

        private void SelectStatement() {
            int line = Line;
            _p++;
            ExpectKw("CASE");
            VType st = Expr();
            if (st == VType.Record) throw new UBasicError("SELECT CASE needs a number or a string", line);
            Sym sv = NewTemp(st);
            StoreSym(sv, 0, line);

            List<int> endJumps = new List<int>();
            SkipTerminators();

            while (!EndSelect()) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                ExpectKw("CASE");

                if (AcceptKw("ELSE")) {
                    SkipTerminators();
                    while (!IsKw("CASE") && !EndSelect()) {
                        if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                        Statement();
                        SkipTerminators();
                    }
                    endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                    continue;
                }

                // One or more comma-separated tests, ORed together:
                // a value, a range (lo TO hi), or IS <relop> value.
                List<int> matchJumps = new List<int>();
                while (true) {
                    int cl = Line;
                    LoadSym(sv, 0, cl);
                    if (AcceptKw("IS")) {
                        T k = Cur.Kind;
                        if (k < T.Eq || k > T.Ge) throw Err("expected a comparison after IS");
                        _p++;
                        EmitRel(k, st, Expr(), cl);
                    } else {
                        VType a = Expr();
                        if (AcceptKw("TO")) {
                            EmitRel(T.Ge, st, a, cl);
                            LoadSym(sv, 0, cl);
                            EmitRel(T.Le, st, Expr(), cl);
                            _c.Emit(Op.AndI, 0, 0, cl);
                        } else EmitRel(T.Eq, st, a, cl);
                    }
                    matchJumps.Add(_c.Emit(Op.JmpT, 0, 0, cl));
                    if (Accept(T.Comma)) continue;
                    break;
                }
                int noMatch = _c.Emit(Op.Jmp, 0, 0, Line);
                for (int i = 0; i < matchJumps.Count; i++) _c.Patch(matchJumps[i], _c.Here);

                SkipTerminators();
                while (!IsKw("CASE") && !EndSelect()) {
                    if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                    Statement();
                    SkipTerminators();
                }
                endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                _c.Patch(noMatch, _c.Here);
            }
            _p += 2;
            for (int i = 0; i < endJumps.Count; i++) _c.Patch(endJumps[i], _c.Here);
        }

        private void ExitStatement() {
            int line = Line;
            _p++;
            if (IsKw("SUB") || IsKw("FUNCTION") || IsKw("DEF")) {
                string k = Adv().Text;
                bool ok = _cur != null && (k == "SUB" ? !_cur.Fi.IsFunction : _cur.Fi.IsFunction);
                if (!ok) throw new UBasicError("EXIT " + k + " outside a " + k, line);
                _exitFix.Add(_c.Emit(Op.Jmp, 0, 0, line));
            } else if (IsKw("FOR") || IsKw("DO") || IsKw("WHILE")) {
                string k = Adv().Text;
                LoopCtx ctx = null;
                for (int i = _loops.Count - 1; i >= 0; i--) if (_loops[i].Kind == k) { ctx = _loops[i]; break; }
                if (ctx == null) throw new UBasicError("EXIT " + k + " outside a " + k + " loop", line);
                ctx.ExitJumps.Add(_c.Emit(Op.Jmp, 0, 0, line));
            } else throw Err("expected FOR, DO, WHILE, SUB or FUNCTION after EXIT");
            EndOfStatement();
        }

        /// <summary>ON n GOTO a, b, c / ON n GOSUB a, b, c. ON ERROR is
        /// accepted and ignored: nothing here raises a trappable error.</summary>
        private void OnStatement() {
            int line = Line;
            _p++;
            if (IsKw("ERROR")) { SkipStatement(); return; }
            if (Cur.Kind == T.Ident && (Cur.Text == "KEY" || Cur.Text == "TIMER" || Cur.Text == "PEN" ||
                Cur.Text == "STRIG" || Cur.Text == "COM") && Peek(1).Kind == T.LParen)
                throw new UBasicError("ON " + Cur.Text + " event trapping is not supported", line);
            if (IsKw("PLAY")) throw new UBasicError("ON PLAY event trapping is not supported", line);
            CoerceTo(Expr(), VType.Int, line);
            Sym tmp = NewTemp(VType.Int);
            StoreSym(tmp, 0, line);
            bool gosub = AcceptKw("GOSUB");
            if (!gosub) ExpectKw("GOTO");
            List<int> ends = new List<int>();
            int n = 1;
            while (true) {
                LoadSym(tmp, 0, line);
                _c.Emit(Op.PushI, n++, 0, line);
                _c.Emit(Op.EqI, 0, 0, line);
                int skip = _c.Emit(Op.JmpF, 0, 0, line);
                LabelRef(_c.Emit(gosub ? Op.Gosub : Op.Jmp, 0, 0, line), line);
                if (gosub) ends.Add(_c.Emit(Op.Jmp, 0, 0, line));
                _c.Patch(skip, _c.Here);
                if (Accept(T.Comma)) continue;
                break;
            }
            for (int i = 0; i < ends.Count; i++) _c.Patch(ends[i], _c.Here);
            EndOfStatement();
        }

        // ---- calls ---------------------------------------------------------------

        private static string HostName(Token t) { return t.Text + (t.HadSigil ? Suffix(t.Sig) : ""); }

        /// <summary>Is the argument at the cursor just a variable, an array
        /// element or a field -- something that is passed by reference?
        /// Anything else, including a variable in parentheses, is a value.</summary>
        private bool IsBareLVArg() {
            if (Cur.Kind != T.Ident) return false;
            int i = _p + 1;
            bool hasParen = false, dotted = false;
            if (At(i).Kind == T.LParen) {
                hasParen = true;
                int close = MatchParen(i);
                if (close < 0) return false;
                i = close + 1;
            }
            while (At(i).Kind == T.Dot && (At(i + 1).Kind == T.Ident || At(i + 1).Kind == T.Keyword)) { i += 2; dotted = true; }
            Token e = At(i);
            if (!(e.Kind == T.Comma || e.Kind == T.RParen || IsEnd(e))) return false;
            Token t = Cur;
            if (hasParen) return FindArray(t.Text) != null;
            if (dotted || FindScalar(t) != null) return true;
            if (_consts.ContainsKey(t.Text)) return false;
            if (_procs.ContainsKey(t.Text)) return false;
            if (Host.Find(HostName(t), 0) >= 0) return false;
            return true;
        }

        private const int ArgsParen = 0;   // inside one pair of parentheses, '(' already consumed
        private const int ArgsBare = 1;    // a bare list up to the end of the statement
        private const int ArgsNone = 2;    // a FUNCTION named without parentheses

        /// <summary>Emit a call to a SUB or FUNCTION with the name already
        /// consumed. Leaves the result on the stack for a FUNCTION.</summary>
        private void EmitCall(Proc p, Token nameTok, int form, int line) {
            LV[] byRef = new LV[p.Params.Count];
            int argc = 0;
            bool any = form == ArgsParen ? (Cur.Kind != T.RParen) : form == ArgsBare ? !AtEnd() : false;
            if (any) {
                while (true) {
                    if (argc >= p.Params.Count)
                        throw new UBasicError("too many arguments to " + nameTok.Raw, line);
                    Param pa = p.Params[argc];
                    if (pa.IsArray) {
                        Token a = Adv();
                        Sym arr = a.Kind == T.Ident ? FindArray(a.Text) : null;
                        if (arr == null) throw new UBasicError("argument " + (argc + 1) + " of " + nameTok.Raw +
                            " must be an array, got '" + Describe(a) + "'", line);
                        if (Accept(T.LParen)) Expect(T.RParen, "')'");
                        if (!pa.IsAny && (arr.Rec != pa.Rec || (arr.Type == VType.Str) != (pa.Type == VType.Str)))
                            throw new UBasicError("argument " + (argc + 1) + " of " + nameTok.Raw +
                                ": array type does not match the parameter", line);
                        LoadSym(arr, 0, line);
                    } else if (!p.ByVal && IsBareLVArg()) {
                        LV lv = ParseLV(true);
                        if (lv.Rec != pa.Rec) throw new UBasicError("argument " + (argc + 1) + " of " + nameTok.Raw +
                            ": TYPE does not match the parameter", line);
                        if (lv.Rec != null) {
                            for (int i = 0; i < lv.Rec.Size; i++) LVLoad(lv, i, line);
                        } else {
                            LVLoad(lv, 0, line);
                            CoerceTo(lv.Type, pa.Type, line);
                        }
                        byRef[argc] = lv;
                    } else {
                        if (pa.Rec != null) throw new UBasicError("argument " + (argc + 1) + " of " + nameTok.Raw +
                            " must be a TYPE variable", line);
                        CoerceTo(Expr(), pa.Type, line);
                    }
                    argc++;
                    if (Accept(T.Comma)) continue;
                    break;
                }
            }
            if (argc != p.Params.Count)
                throw new UBasicError(nameTok.Raw + " expects " + p.Params.Count +
                    " argument(s), got " + argc, line);
            _c.Emit(Op.Call, p.Index, p.Fi.ParamCount, line);

            // Copy by-reference arguments back. See FinishCopyOuts.
            for (int k = 0; k < byRef.Length; k++) {
                LV lv = byRef[k];
                if (lv == null) continue;
                Param pa = p.Params[k];
                CopyFix fix = new CopyFix();
                fix.Start = _c.Here; fix.P = p; fix.Param = k;
                for (int i = 0; i < pa.Size; i++) {
                    LVPrep(lv, line);
                    _c.Emit(Op.LoadRet, pa.Slot + i, 0, line);
                    if (lv.Rec == null) CoerceTo(pa.Type, lv.Type, line);
                    LVStore(lv, i, line, false);
                }
                fix.End = _c.Here;
                _copyFixes.Add(fix);
                if (!lv.Elem && lv.Base.ParamIdx >= 0 && _cur != null) {
                    Forward f = new Forward();
                    f.From = _cur; f.FromParam = lv.Base.ParamIdx; f.To = p; f.ToParam = k;
                    _forwards.Add(f);
                }
            }
        }

        private void CallStatement(bool callKeyword) {
            int line = Line;
            Token nameTok = Adv();
            Proc p;
            if (nameTok.Kind != T.Ident || !_procs.TryGetValue(nameTok.Text, out p))
                throw new UBasicError("unknown SUB '" + Describe(nameTok) + "'", line);

            // CALL Name(a, b) uses the parentheses as the argument list. Without
            // CALL, QBasic reads "Name (a), b" as passing (a) by value; the
            // uBasic dialect also accepts "Name(a, b)" as a call.
            bool paren = false;
            if (Cur.Kind == T.LParen) {
                int close = MatchParen(_p);
                paren = callKeyword || (!_qb && close >= 0 && IsEnd(At(close + 1)));
            }
            if (paren) {
                _p++;
                EmitCall(p, nameTok, ArgsParen, line);
                Expect(T.RParen, "')'");
            } else EmitCall(p, nameTok, ArgsBare, line);
            if (p.Fi.IsFunction) _c.Emit(Op.Pop, 0, 0, line);
            EndOfStatement();
        }

        // ---- graphics / sound / print statements -----------------------------

        private void EmitHost(string name, int argc, int line) {
            int h = Host.Find(name, argc);
            if (h < 0) throw new UBasicError("internal: no host '" + name + "/" + argc + "'", line);
            _c.Emit(Op.CallHost, h, argc, line);
        }

        private void PushI(int v, int line) { _c.Emit(Op.PushI, v, 0, line); }
        private void PushF0(int line) { _c.Emit(Op.PushF, _c.AddFloat(0.0), 0, line); }

        /// <summary>An optional integer argument: -1 when it is left out.</summary>
        private bool OptInt(int line) {
            if (HasArg()) { CoerceTo(Expr(), VType.Int, line); return true; }
            PushI(-1, line);
            return false;
        }

        /// <summary>Statements of the shape NAME [a][, b]: COLOR.</summary>
        private void TwoOptional(string host) {
            int line = Line;
            _p++;
            OptInt(line);
            if (Accept(T.Comma)) {
                OptInt(line);
                while (Accept(T.Comma)) { if (HasArg()) { Expr(); _c.Emit(Op.Pop, 0, 0, line); } }
            } else PushI(-1, line);
            EmitHost(host, 2, line);
            EndOfStatement();
        }

        private void LocateStatement() {
            int line = Line;
            _p++;
            OptInt(line);
            if (Accept(T.Comma)) {
                OptInt(line);
                // cursor visibility and shape: accepted, ignored
                while (Accept(T.Comma)) { if (HasArg()) { Expr(); _c.Emit(Op.Pop, 0, 0, line); } }
            } else PushI(-1, line);
            EmitHost("__locate", 2, line);
            EndOfStatement();
        }

        private void ClsStatement() {
            int line = Line;
            _p++;
            if (_qb) {
                OptInt(line);
                EmitHost("__clsq", 1, line);
            } else if (AtEnd()) {
                EmitHost("__cls0", 0, line);
            } else {
                CoerceTo(Expr(), VType.Int, line);
                EmitHost("__cls", 1, line);
            }
            EndOfStatement();
        }

        private void ScreenStatement() {
            int line = Line;
            _p++;
            if (!HasArg()) { SkipStatement(); return; }       // SCREEN , , page, page
            CoerceTo(Expr(), VType.Int, line);
            bool two = false;
            if (Accept(T.Comma)) {
                if (HasArg()) { CoerceTo(Expr(), VType.Int, line); two = true; }
                while (Accept(T.Comma)) { if (HasArg()) { Expr(); _c.Emit(Op.Pop, 0, 0, line); } }
            }
            EmitHost(two ? "__screen" : "__screen1", two ? 2 : 1, line);
            EndOfStatement();
        }

        private void WidthStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind == T.Hash || Cur.Kind == T.StrLit) { SkipStatement(); return; }   // file / printer width
            if (HasArg()) CoerceTo(Expr(), VType.Int, line); else PushI(0, line);
            if (Accept(T.Comma) && HasArg()) CoerceTo(Expr(), VType.Int, line); else PushI(0, line);
            EmitHost("__width", 2, line);
            EndOfStatement();
        }

        private void ViewStatement() {
            int line = Line;
            _p++;
            if (!AcceptKw("PRINT"))
                throw new UBasicError("VIEW (graphics viewport) is not supported; only VIEW PRINT", line);
            if (AtEnd()) { PushI(0, line); PushI(0, line); }
            else {
                CoerceTo(Expr(), VType.Int, line);
                ExpectKw("TO");
                CoerceTo(Expr(), VType.Int, line);
            }
            EmitHost("__viewprint", 2, line);
            EndOfStatement();
        }

        /// <summary>Parses "[STEP] (x, y)" and leaves x, y on the stack as
        /// integers. Returns true if STEP made it relative.</summary>
        private bool Coord(int line) {
            bool step = AcceptKw("STEP");
            Expect(T.LParen, "'('");
            CoerceTo(Expr(), VType.Int, line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Int, line);
            Expect(T.RParen, "')'");
            return step;
        }

        private void PSetStatement(bool preset) {
            int line = Line;
            _p++;
            int flags = preset ? 2 : 0;
            if (Coord(line)) flags |= 1;
            if (Accept(T.Comma)) OptInt(line); else PushI(-1, line);
            PushI(flags, line);
            EmitHost("__pset", 4, line);
            EndOfStatement();
        }

        private void LineStatement() {
            int line = Line;
            _p++;
            if (IsKw("INPUT")) { InputStatement(true); return; }
            int flags = 0;
            if (Cur.Kind == T.Minus) { PushI(0, line); PushI(0, line); flags |= 4; }   // LINE -(x, y)
            else if (Coord(line)) flags |= 1;
            Expect(T.Minus, "'-'");
            if (Coord(line)) flags |= 2;
            if (Accept(T.Comma)) {
                OptInt(line);
                if (Accept(T.Comma)) {
                    if (Cur.Kind == T.Ident && !Cur.HadSigil && (Cur.Text == "B" || Cur.Text == "BF") &&
                        (Peek(1).Kind == T.Comma || IsEnd(Peek(1)))) {
                        flags |= Cur.Text == "BF" ? 24 : 8;
                        _p++;
                    } else if (HasArg()) throw Err("expected B or BF");
                    if (Accept(T.Comma) && HasArg()) { Expr(); _c.Emit(Op.Pop, 0, 0, line); }   // line style
                }
            } else PushI(-1, line);
            PushI(flags, line);
            EmitHost("__line", 6, line);
            EndOfStatement();
        }

        private void CircleStatement() {
            int line = Line;
            _p++;
            int flags = Coord(line) ? 1 : 0;
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);   // radius
            bool rest = false;
            if (Accept(T.Comma)) { OptInt(line); rest = Accept(T.Comma); }
            else PushI(-1, line);

            // uBasic extension: CIRCLE (x, y), r, c, F draws it filled.
            if (rest && !_qb && IsIdent("F") && IsEnd(Peek(1))) {
                _p++;
                flags |= 16;
                rest = false;
            }
            if (rest) {
                if (HasArg()) { CoerceTo(Expr(), VType.Single, line); flags |= 2; } else PushF0(line);
                if (Accept(T.Comma)) {
                    if (HasArg()) { CoerceTo(Expr(), VType.Single, line); flags |= 4; } else PushF0(line);
                    if (Accept(T.Comma) && HasArg()) { CoerceTo(Expr(), VType.Single, line); flags |= 8; }
                    else PushF0(line);
                } else { PushF0(line); PushF0(line); }
            } else { PushF0(line); PushF0(line); PushF0(line); }
            PushI(flags, line);
            EmitHost("__circle", 8, line);
            EndOfStatement();
        }

        private void PaintStatement() {
            int line = Line;
            _p++;
            if (!_qb) {
                Coord(line);
                Expect(T.Comma, "','");
                CoerceTo(Expr(), VType.Int, line);
                EmitHost("__paintu", 3, line);
                EndOfStatement();
                return;
            }
            int flags = Coord(line) ? 1 : 0;
            if (Accept(T.Comma)) {
                OptInt(line);
                if (Accept(T.Comma) && HasArg()) { CoerceTo(Expr(), VType.Int, line); flags |= 2; }
                else PushI(0, line);
            } else { PushI(-1, line); PushI(0, line); }
            PushI(flags, line);
            EmitHost("__paint", 5, line);
            EndOfStatement();
        }

        /// <summary>The array operand of GET / PUT: name or name(index).</summary>
        private void ImageArray(int line) {
            Token a = Adv();
            Sym arr = a.Kind == T.Ident ? FindArray(a.Text) : null;
            if (arr == null) throw new UBasicError("expected an array name, got '" + Describe(a) + "'", line);
            LoadSym(arr, 0, line);
            if (Accept(T.LParen)) {
                if (Cur.Kind != T.RParen) CoerceTo(Expr(), VType.Int, line);
                else PushI(int.MinValue, line);
                Expect(T.RParen, "')'");
            } else PushI(int.MinValue, line);
        }

        private void GetStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind != T.LParen && !IsKw("STEP"))
                throw new UBasicError("file GET is not supported; only graphics GET (x1, y1)-(x2, y2), array", line);
            Coord(line);
            Expect(T.Minus, "'-'");
            Coord(line);
            Expect(T.Comma, "','");
            ImageArray(line);
            EmitHost("__get", 6, line);
            EndOfStatement();
        }

        private void PutStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind != T.LParen && !IsKw("STEP"))
                throw new UBasicError("file PUT is not supported; only graphics PUT (x, y), array", line);
            Coord(line);
            Expect(T.Comma, "','");
            ImageArray(line);
            int action = 4;                               // XOR is the default
            if (Accept(T.Comma)) {
                if (AcceptKw("PSET")) action = 0;
                else if (AcceptKw("PRESET")) action = 1;
                else if (AcceptKw("AND")) action = 2;
                else if (AcceptKw("OR")) action = 3;
                else if (AcceptKw("XOR")) action = 4;
                else throw Err("expected PSET, PRESET, AND, OR or XOR");
            }
            PushI(action, line);
            EmitHost("__put", 5, line);
            EndOfStatement();
        }

        private void PaletteStatement() {
            int line = Line;
            _p++;
            if (AtEnd()) { EmitHost("__palreset", 0, line); return; }
            if (IsKw("USING")) throw new UBasicError("PALETTE USING is not supported", line);
            CoerceTo(Expr(), VType.Int, line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Int, line);
            if (Accept(T.Comma)) {
                // uBasic: PALETTE index, r, g, b
                CoerceTo(Expr(), VType.Int, line);
                Expect(T.Comma, "','");
                CoerceTo(Expr(), VType.Int, line);
                EmitHost("__palette", 4, line);
            } else EmitHost("__palq", 2, line);
            EndOfStatement();
        }

        private void RandomizeStatement() {
            int line = Line;
            _p++;
            if (AtEnd()) EmitHost("__randomize0", 0, line);
            else { CoerceTo(Expr(), VType.Single, line); EmitHost("__randomize", 1, line); }
            EndOfStatement();
        }

        // SOUND always blocks, as in QBasic. PLAY blocks only while the music
        // is in foreground mode (MF), which is runtime state inside the string --
        // so PLAY always emits the wait and the device reports "not busy" when
        // the queued notes were tagged background (MB).
        private void SoundStatement() {
            int line = Line;
            _p++;
            CoerceTo(Expr(), VType.Single, line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);
            EmitHost("__sound", 2, line);
            _c.Emit(Op.AwaitSound, 0, 0, line);
            EndOfStatement();
        }

        private void BeepStatement() {
            int line = Line;
            _p++;
            EmitHost("__beep", 0, line);
            _c.Emit(Op.AwaitSound, 0, 0, line);
            EndOfStatement();
        }

        private void PlayStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind == T.Keyword && (Cur.Text == "ON" || Cur.Text == "STOP") ||
                (IsIdent("OFF"))) { SkipStatement(); return; }       // PLAY ON/OFF/STOP event switches
            CoerceTo(Expr(), VType.Str, line);
            EmitHost("__play", 1, line);
            _c.Emit(Op.AwaitSound, 0, 0, line);
            EndOfStatement();
        }

        private void SleepStatement() {
            int line = Line;
            _p++;
            if (AtEnd()) PushF0(line);
            else CoerceTo(Expr(), VType.Single, line);
            EmitHost("__sleep", 1, line);
            _c.Emit(Op.AwaitSleep, 0, 0, line);
            EndOfStatement();
        }

        private void PrintStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind == T.Hash) throw new UBasicError("PRINT # (files) is not supported", line);
            bool using_ = false;
            if (AcceptKw("USING")) {
                CoerceTo(Expr(), VType.Str, line);
                EmitHost("__usingfmt", 1, line);
                Expect(T.Semi, "';' after the PRINT USING format");
                using_ = true;
            }
            bool suppressNewline = false;

            // Stops at ELSE so that IF c THEN PRINT "a" ELSE PRINT "b" works:
            // without this the ELSE is parsed as another thing to print.
            while (!AtEnd()) {
                if (Accept(T.Semi)) { suppressNewline = true; continue; }
                if (Accept(T.Comma)) {
                    if (!using_) EmitHost("__printtab", 0, line);
                    suppressNewline = true;
                    continue;
                }
                if (Cur.Kind == T.Ident && !Cur.HadSigil && (Cur.Text == "TAB" || Cur.Text == "SPC") &&
                    Peek(1).Kind == T.LParen && FindArray(Cur.Text) == null) {
                    string fn = Cur.Text == "TAB" ? "__tab" : "__spc";
                    _p += 2;
                    CoerceTo(Expr(), VType.Int, line);
                    Expect(T.RParen, "')'");
                    EmitHost(fn, 1, line);
                    suppressNewline = false;
                    continue;
                }

                VType t = Expr();
                if (t == VType.Record) throw new UBasicError("cannot PRINT a whole TYPE variable", line);
                if (using_) {
                    if (t == VType.Str) EmitHost("__usingstr", 1, line);
                    else { CoerceTo(t, VType.Single, line); EmitHost("__usingnum", 1, line); }
                }
                else if (t == VType.Str) EmitHost("__prints", 1, line);
                else if (IsInt(t)) EmitHost("__printi", 1, line);
                else EmitHost(t == VType.Double ? "__printd" : "__printf", 1, line);
                suppressNewline = false;
            }
            if (using_) EmitHost("__usingend", 0, line);
            if (!suppressNewline) EmitHost("__printnl", 0, line);
            EndOfStatement();
        }

        private void PushStr(string s, int line) {
            int idx = _c.StringConsts.Count;
            _c.StringConsts.Add(s);
            _c.Emit(Op.PushS, idx, 0, line);
        }

        /// <summary>INPUT and LINE INPUT. The VM edits the line across as
        /// many frames as the typing takes; the variables are then filled
        /// from its comma-separated fields.</summary>
        private void InputStatement(bool lineInput) {
            int line = Line;
            _p++;
            if (Cur.Kind == T.Hash) throw new UBasicError("INPUT # (files) is not supported", line);
            bool stay = Accept(T.Semi);
            bool question = !lineInput;
            if (Cur.Kind == T.StrLit) {
                PushStr(Adv().Text, line);
                EmitHost("__prints", 1, line);
                if (Accept(T.Comma)) question = false;
                else Expect(T.Semi, "';' or ',' after the prompt");
            }
            if (question) { PushStr("? ", line); EmitHost("__prints", 1, line); }
            _c.Emit(Op.InputLine, stay ? 1 : 0, 0, line);

            if (lineInput) {
                LV lv = ParseLV(false);
                if (lv.Type != VType.Str) throw new UBasicError("LINE INPUT needs a string variable", line);
                EmitHost("__inpline", 0, line);
                LVStore(lv, 0, line, true);
                EndOfStatement();
                return;
            }

            // Count the variables first: a lone string takes the whole line.
            int total = 1, depth = 0;
            for (int j = _p; !IsEnd(At(j)); j++) {
                T k = At(j).Kind;
                if (k == T.LParen) depth++;
                else if (k == T.RParen) depth--;
                else if (k == T.Comma && depth == 0) total++;
            }
            int n = 0;
            while (true) {
                LV lv = ParseLV(false);
                if (lv.Rec != null) throw new UBasicError("cannot INPUT a whole TYPE variable", line);
                PushI(n, line);
                if (lv.Type == VType.Str) { PushI(total, line); EmitHost("__inpfield_s", 2, line); }
                else { EmitHost("__inpfield_f", 1, line); CoerceTo(VType.Double, lv.Type, line); }
                LVStore(lv, 0, line, true);
                n++;
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        private void ReadStatement() {
            int line = Line;
            _p++;
            while (true) {
                LV lv = ParseLV(false);
                if (lv.Rec != null) throw new UBasicError("cannot READ a whole TYPE variable", line);
                if (lv.Type == VType.Str) EmitHost("__reads", 0, line);
                else { EmitHost("__readf", 0, line); CoerceTo(VType.Double, lv.Type, line); }
                LVStore(lv, 0, line, true);
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        private void RestoreStatement() {
            int line = Line;
            _p++;
            int at = 0;
            if (!AtEnd()) {
                Token lbl = Adv();
                string key = lbl.Kind == T.IntLit ? lbl.IVal.ToString() : lbl.Kind == T.Ident ? lbl.Text : null;
                if (key == null || !_labelData.TryGetValue(key, out at))
                    throw new UBasicError("undefined label '" + Describe(lbl) + "'", line);
            }
            PushI(at, line);
            EmitHost("__restore", 1, line);
            EndOfStatement();
        }

        private void SwapStatement() {
            int line = Line;
            _p++;
            LV a = ParseLV(true);
            Expect(T.Comma, "','");
            LV b = ParseLV(true);
            if (a.Rec != b.Rec || (a.Type == VType.Str) != (b.Type == VType.Str))
                throw new UBasicError("SWAP needs two variables of the same type", line);
            int size = LVSize(a);
            for (int i = 0; i < size; i++) {
                VType ta = a.Rec != null ? a.Rec.Slots[i] : a.Type;
                Sym tmp = NewTemp(ta);
                LVLoad(a, i, line);
                StoreSym(tmp, 0, line);
                LVPrep(a, line);
                LVLoad(b, i, line);
                if (a.Rec == null) CoerceTo(b.Type, a.Type, line);
                LVStore(a, i, line, true);
                LVPrep(b, line);
                LoadSym(tmp, 0, line);
                if (a.Rec == null) CoerceTo(a.Type, b.Type, line);
                LVStore(b, i, line, true);
            }
            EndOfStatement();
        }

        private void EraseStatement() {
            int line = Line;
            _p++;
            while (true) {
                Token a = Adv();
                Sym arr = a.Kind == T.Ident ? FindArray(a.Text) : null;
                if (arr == null) throw new UBasicError("expected an array name, got '" + Describe(a) + "'", line);
                LoadSym(arr, 0, line);
                EmitHost("__erase", 1, line);
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        // ---- expressions -----------------------------------------------------

        private void CoerceTo(VType from, VType to, int line) {
            if (from == to) return;
            if (from == VType.Record || to == VType.Record)
                throw new UBasicError("type mismatch: a whole TYPE variable cannot be used here", line);
            if (from == VType.Str || to == VType.Str)
                throw new UBasicError("type mismatch: cannot use a " +
                    (from == VType.Str ? "string" : "number") + " here", line);
            if (IsInt(from) && IsFlt(to)) _c.Emit(Op.I2F, 0, 0, line);
            else if (IsFlt(from) && IsInt(to)) _c.Emit(Op.F2I, 0, 0, line);
        }

        // Precedence, loosest first, as in QBasic:
        // IMP, EQV, XOR, OR, AND, NOT, comparison, + -, MOD, \, * /, unary -, ^
        private VType Expr() {
            VType t = EqvExpr();
            while (IsKw("IMP")) {
                int line = Line; _p++;
                ToLogic(t, line);
                _c.Emit(Op.NotI, 0, 0, line);
                VType r = EqvExpr();
                ToLogic(r, line);
                _c.Emit(Op.OrI, 0, 0, line);
                t = LogicType(t, r);
            }
            return t;
        }

        private VType EqvExpr() {
            VType t = XorExpr();
            while (IsKw("EQV")) {
                int line = Line; _p++;
                ToLogic(t, line);
                VType r = XorExpr();
                ToLogic(r, line);
                _c.Emit(Op.XorI, 0, 0, line);
                _c.Emit(Op.NotI, 0, 0, line);
                t = LogicType(t, r);
            }
            return t;
        }

        private void ToLogic(VType t, int line) {
            if (t == VType.Str || t == VType.Record)
                throw new UBasicError("type mismatch: AND/OR/NOT need numbers", line);
            if (IsFlt(t)) _c.Emit(Op.F2I, 0, 0, line);
        }
        private static VType LogicType(VType a, VType b) {
            return (a == VType.Int && b == VType.Int) ? VType.Int : VType.Long;
        }

        private VType XorExpr() {
            VType t = OrExpr();
            while (IsKw("XOR")) {
                int line = Line; _p++;
                ToLogic(t, line);
                VType r = OrExpr();
                ToLogic(r, line);
                _c.Emit(Op.XorI, 0, 0, line);
                t = LogicType(t, r);
            }
            return t;
        }

        private VType OrExpr() {
            VType t = AndExpr();
            while (IsKw("OR")) {
                int line = Line; _p++;
                ToLogic(t, line);
                VType r = AndExpr();
                ToLogic(r, line);
                _c.Emit(Op.OrI, 0, 0, line);
                t = LogicType(t, r);
            }
            return t;
        }

        private VType AndExpr() {
            VType t = NotExpr();
            while (IsKw("AND")) {
                int line = Line; _p++;
                ToLogic(t, line);
                VType r = NotExpr();
                ToLogic(r, line);
                _c.Emit(Op.AndI, 0, 0, line);
                t = LogicType(t, r);
            }
            return t;
        }

        private VType NotExpr() {
            if (IsKw("NOT")) {
                int line = Line; _p++;
                VType t = NotExpr();
                ToLogic(t, line);
                _c.Emit(Op.NotI, 0, 0, line);
                return t == VType.Int ? VType.Int : VType.Long;
            }
            return RelExpr();
        }

        private VType RelExpr() {
            VType t = AddExpr();
            while (Cur.Kind >= T.Eq && Cur.Kind <= T.Ge) {
                T k = Cur.Kind;
                int line = Line; _p++;
                VType r = AddExpr();
                EmitRel(k, t, r, line);
                t = VType.Int;
            }
            return t;
        }

        /// <summary>Compare the two operands on the stack.</summary>
        private void EmitRel(T k, VType l, VType r, int line) {
            VType common = Unify(l, r, line);
            Op op;
            if (common == VType.Str) {
                op = k == T.Eq ? Op.EqS : k == T.Ne ? Op.NeS : k == T.Lt ? Op.LtS :
                     k == T.Le ? Op.LeS : k == T.Gt ? Op.GtS : Op.GeS;
            } else if (IsInt(common)) {
                op = k == T.Eq ? Op.EqI : k == T.Ne ? Op.NeI : k == T.Lt ? Op.LtI :
                     k == T.Le ? Op.LeI : k == T.Gt ? Op.GtI : Op.GeI;
            } else {
                op = k == T.Eq ? Op.EqF : k == T.Ne ? Op.NeF : k == T.Lt ? Op.LtF :
                     k == T.Le ? Op.LeF : k == T.Gt ? Op.GtF : Op.GeF;
            }
            _c.Emit(op, 0, 0, line);
        }

        /// <summary>Bring the two operands already on the stack to a common
        /// type. I2FUnder exists precisely for the case where the deeper
        /// operand is the one needing promotion.</summary>
        private VType Unify(VType l, VType r, int line) {
            if (l == VType.Record || r == VType.Record)
                throw new UBasicError("type mismatch: a whole TYPE variable cannot be used here", line);
            if (l == VType.Str || r == VType.Str) {
                if (l == r) return VType.Str;
                throw new UBasicError("type mismatch between string and number", line);
            }
            if (IsInt(l) && IsInt(r)) return (l == VType.Long || r == VType.Long) ? VType.Long : VType.Int;
            if (IsInt(l)) _c.Emit(Op.I2FUnder, 0, 0, line);
            if (IsInt(r)) _c.Emit(Op.I2F, 0, 0, line);
            return FloatType(l, r);
        }

        private static VType FloatType(VType l, VType r) {
            return (l == VType.Double || r == VType.Double || l == VType.Long || r == VType.Long)
                ? VType.Double : VType.Single;
        }

        private VType AddExpr() {
            VType t = ModExpr();
            while (Cur.Kind == T.Plus || Cur.Kind == T.Minus) {
                bool plus = Cur.Kind == T.Plus;
                int line = Line; _p++;
                VType r = ModExpr();
                if (t == VType.Str && r == VType.Str) {
                    if (!plus) throw new UBasicError("cannot subtract strings", line);
                    _c.Emit(Op.Concat, 0, 0, line);
                    continue;
                }
                VType common = Unify(t, r, line);
                _c.Emit(IsInt(common) ? (plus ? Op.AddI : Op.SubI)
                                      : (plus ? Op.AddF : Op.SubF), 0, 0, line);
                t = common;
            }
            return t;
        }

        /// <summary>MOD and \ are integer-only. The right operand is on top
        /// of the stack and the left is underneath it, so each side needs
        /// its own conversion opcode.</summary>
        private VType IntOp(VType t, VType r, Op op, int line) {
            if (!(IsInt(t) || IsFlt(t)) || !(IsInt(r) || IsFlt(r)))
                throw new UBasicError("MOD and \\ need numbers", line);
            if (IsFlt(r)) _c.Emit(Op.F2I, 0, 0, line);
            if (IsFlt(t)) _c.Emit(Op.F2IUnder, 0, 0, line);
            _c.Emit(op, 0, 0, line);
            return (t == VType.Int && r == VType.Int) ? VType.Int : VType.Long;
        }

        private VType ModExpr() {
            VType t = IDivExpr();
            while (IsKw("MOD")) {
                int line = Line; _p++;
                t = IntOp(t, IDivExpr(), Op.ModI, line);
            }
            return t;
        }

        private VType IDivExpr() {
            VType t = MulExpr();
            while (Cur.Kind == T.Backslash) {
                int line = Line; _p++;
                t = IntOp(t, MulExpr(), Op.IDiv, line);
            }
            return t;
        }

        private VType MulExpr() {
            VType t = UnaryExpr();
            while (Cur.Kind == T.Star || Cur.Kind == T.Slash) {
                T k = Cur.Kind;
                int line = Line; _p++;
                VType r = UnaryExpr();
                if (k == T.Slash) {
                    // QBasic's "/" is always floating point.
                    if (IsInt(t) && IsInt(r)) { _c.Emit(Op.I2FUnder, 0, 0, line); _c.Emit(Op.I2F, 0, 0, line); }
                    else Unify(t, r, line);
                    if (t == VType.Str || r == VType.Str) throw new UBasicError("cannot divide strings", line);
                    _c.Emit(Op.DivF, 0, 0, line);
                    t = FloatType(t, r);
                    continue;
                }
                VType common = Unify(t, r, line);
                if (common == VType.Str) throw new UBasicError("cannot multiply strings", line);
                _c.Emit(IsInt(common) ? Op.MulI : Op.MulF, 0, 0, line);
                t = common;
            }
            return t;
        }

        private VType UnaryExpr() {
            if (Cur.Kind == T.Minus) {
                int line = Line; _p++;
                VType t = UnaryExpr();
                if (!IsInt(t) && !IsFlt(t)) throw new UBasicError("cannot negate a string", line);
                _c.Emit(IsInt(t) ? Op.NegI : Op.NegF, 0, 0, line);
                return t;
            }
            if (Cur.Kind == T.Plus) { _p++; return UnaryExpr(); }
            return PowExpr();
        }

        private VType PowExpr() {
            VType t = Primary();
            while (Cur.Kind == T.Caret) {
                int line = Line; _p++;
                CoerceTo(t, VType.Single, line);
                VType r = PowOperand();
                CoerceTo(r, VType.Single, line);
                _c.Emit(Op.PowF, 0, 0, line);
                t = FloatType(t, r);
            }
            return t;
        }

        private VType PowOperand() {
            if (Cur.Kind == T.Minus) {
                int line = Line; _p++;
                VType t = PowOperand();
                if (!IsInt(t) && !IsFlt(t)) throw new UBasicError("cannot negate a string", line);
                _c.Emit(IsInt(t) ? Op.NegI : Op.NegF, 0, 0, line);
                return t;
            }
            if (Cur.Kind == T.Plus) { _p++; return PowOperand(); }
            return Primary();
        }

        private VType PushConst(ConstVal cv, int line) {
            if (cv.Type == VType.Str) { PushStr(cv.Str, line); return VType.Str; }
            if (IsInt(cv.Type)) _c.Emit(Op.PushI, (int)cv.Num, 0, line);
            else _c.Emit(Op.PushF, _c.AddFloat(cv.Num), 0, line);
            return cv.Type;
        }

        private VType LoadVariable(int line) {
            LV lv = ParseLV(false);
            if (lv.Rec != null)
                throw new UBasicError("a whole TYPE variable cannot be used in an expression; name one of its fields", line);
            LVLoad(lv, 0, line);
            return lv.Type;
        }

        private VType Primary() {
            int line = Line;

            if (Cur.Kind == T.IntLit) { Token t = Adv(); _c.Emit(Op.PushI, t.IVal, 0, line); return t.Sig; }
            if (Cur.Kind == T.SingleLit) {
                Token t = Adv();
                _c.Emit(Op.PushF, _c.AddFloat(t.FVal), 0, line);
                return t.Sig;
            }
            if (Cur.Kind == T.StrLit) { PushStr(Adv().Text, line); return VType.Str; }
            if (Accept(T.LParen)) {
                VType t = Expr();
                Expect(T.RParen, "')'");
                return t;
            }

            if (Cur.Kind == T.Ident) {
                Token nameTok = Cur;
                string bare = nameTok.Text;
                Proc p;

                if (Peek(1).Kind == T.LParen) {
                    if (FindArray(bare) != null) return LoadVariable(line);
                    if (_procs.TryGetValue(bare, out p)) {
                        if (!p.Fi.IsFunction) throw new UBasicError(nameTok.Raw + " is a SUB and has no value", line);
                        _p += 2;
                        EmitCall(p, nameTok, ArgsParen, line);
                        Expect(T.RParen, "')'");
                        return p.Fi.ReturnType;
                    }
                    string hn = HostName(nameTok);
                    if (bare == "LBOUND" || bare == "UBOUND") { _p++; return BoundCall(bare == "UBOUND", line); }
                    if (Host.Exists(hn) && !hn.StartsWith("__")) { _p++; return HostCall(hn, nameTok, line); }
                    throw new UBasicError("unknown array or function '" + nameTok.Raw + "'", line);
                }

                if (Peek(1).Kind == T.Dot || FindScalar(nameTok) != null) return LoadVariable(line);

                ConstVal cv;
                if (_consts.TryGetValue(bare, out cv) &&
                    (!nameTok.HadSigil || (cv.Type == VType.Str) == (nameTok.Sig == VType.Str))) {
                    _p++;
                    return PushConst(cv, line);
                }
                if (_procs.TryGetValue(bare, out p) && p.Fi.IsFunction) {
                    _p++;
                    EmitCall(p, nameTok, ArgsNone, line);
                    return p.Fi.ReturnType;
                }
                string hostName = HostName(nameTok);
                if (Host.Find(hostName, 0) >= 0 && !hostName.StartsWith("__")) {
                    _p++;
                    return HostCall(hostName, nameTok, line);
                }
                // Plain variable read. Reading an undeclared name yields its
                // zero value, as QBasic does.
                return LoadVariable(line);
            }

            if (Cur.Kind == T.Keyword && Cur.Text == "SCREEN" && Peek(1).Kind == T.LParen)
                throw new UBasicError("the SCREEN() function is not supported", line);
            throw Err("expected a value");
        }

        private VType BoundCall(bool upper, int line) {
            Expect(T.LParen, "'('");
            Token a = Adv();
            Sym arr = a.Kind == T.Ident ? FindArray(a.Text) : null;
            if (arr == null) throw new UBasicError("expected an array name, got '" + Describe(a) + "'", line);
            LoadSym(arr, 0, line);
            if (Accept(T.Comma)) CoerceTo(Expr(), VType.Int, line);
            else PushI(1, line);
            Expect(T.RParen, "')'");
            _c.Emit(Op.ArrBound, upper ? 1 : 0, 0, line);
            return VType.Int;
        }

        /// <summary>Call a built-in function. The overload is chosen by
        /// argument count, which is found by looking ahead, so that each
        /// argument can be converted as it is compiled.</summary>
        private VType HostCall(string hostName, Token nameTok, int line) {
            int argc = 0;
            bool paren = Cur.Kind == T.LParen;
            if (paren) {
                int close = MatchParen(_p);
                if (close < 0) throw Err("expected ')'");
                if (close > _p + 1) {
                    argc = 1;
                    int depth = 0;
                    for (int j = _p + 1; j < close; j++) {
                        T k = _t[j].Kind;
                        if (k == T.LParen) depth++;
                        else if (k == T.RParen) depth--;
                        else if (k == T.Comma && depth == 0) argc++;
                    }
                }
            }

            // A few built-ins pick their implementation from an argument's
            // type rather than from the argument count.
            if (hostName == "RND" && _qb && argc == 1) hostName = "__rnd";

            int h = Host.Find(hostName, argc);
            if (h < 0) {
                int min = Host.MinArgc(hostName);
                throw new UBasicError(nameTok.Raw + " does not take " + argc +
                    " argument(s)" + (min >= 0 ? " (expected " + min + ")" : ""), line);
            }
            HostFn fn = Host.All[h];
            if (!fn.HasRet) throw new UBasicError(nameTok.Raw + " has no value", line);

            VType first = VType.Single;
            bool anyDouble = false;
            if (paren) {
                _p++;
                for (int i = 0; i < argc; i++) {
                    if (i > 0) Expect(T.Comma, "','");
                    VType have = Expr();
                    VType want = fn.Params[i];
                    if (hostName == "STRING$" && i == 1 && have != VType.Str) {
                        h = Host.Find("__stringc", 2); fn = Host.All[h]; want = VType.Int;
                    } else if (hostName == "STR$" && have == VType.Double) {
                        h = Host.Find("__strd", 1); fn = Host.All[h];
                    }
                    if ((have == VType.Str) != (want == VType.Str) || have == VType.Record)
                        throw new UBasicError("argument " + (i + 1) + " of " + nameTok.Raw +
                            " should be a " + (want == VType.Str ? "string" : "number"), line);
                    CoerceTo(have, want, line);
                    if (i == 0) first = have;
                    if (have == VType.Double || have == VType.Long) anyDouble = true;
                }
                Expect(T.RParen, "')'");
            }

            _c.Emit(Op.CallHost, h, argc, line);
            if (fn.RetRule == 1) return anyDouble ? VType.Double : VType.Single;
            if (fn.RetRule == 2) {
                if (IsInt(first)) { _c.Emit(Op.F2I, 0, 0, line); return first; }
                return first;
            }
            return fn.Ret;
        }
    }
}
