using System;
using System.Collections.Generic;

namespace UBasic {

    /// <summary>Compiles uBasic source straight to bytecode. No AST: pass 1
    /// collects SUB/FUNCTION signatures so calls can be resolved before their
    /// bodies are seen, pass 2 emits code in a single recursive descent.</summary>
    public class Compiler {

        private class ConstVal { public VType Type; public Slot Val; public string Str; }

        private class LoopCtx {
            public List<int> ExitJumps = new List<int>();
            public List<int> ContinueJumps = new List<int>();
            public int Top;
            public string Kind;      // "FOR" | "WHILE" | "DO"
        }

        private List<Token> _t;
        private int _p;
        private Chunk _c;

        private readonly Dictionary<string, int> _globals = new Dictionary<string, int>();
        private readonly HashSet<string> _shared = new HashSet<string>();
        private readonly Dictionary<string, ConstVal> _consts = new Dictionary<string, ConstVal>();
        private readonly Dictionary<string, int> _arrays = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _funcs = new Dictionary<string, int>();

        // current procedure (null at module level)
        private FuncInfo _fn;
        private Dictionary<string, int> _locals;
        private List<VType> _localTypes;
        private readonly List<LoopCtx> _loops = new List<LoopCtx>();
        private Dictionary<string, int> _labels;
        private List<KeyValuePair<int, Token>> _labelFix;
        private int _tmpSeq;

        // ---- token helpers -------------------------------------------------

        private Token Cur { get { return _t[_p]; } }
        private Token Peek(int n) { int i = _p + n; return i < _t.Count ? _t[i] : _t[_t.Count - 1]; }
        private Token Adv() { return _t[_p++]; }
        private int Line { get { return Cur.Line; } }

        private bool IsKw(string k) { return Cur.Kind == T.Keyword && Cur.Text == k; }
        private bool IsKw(Token t, string k) { return t.Kind == T.Keyword && t.Text == k; }

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
        private UBasicError Err(string msg) {
            string got = Cur.Kind == T.EOF ? "end of file"
                       : Cur.Kind == T.EOL ? "end of line"
                       : (Cur.Raw ?? Cur.Text ?? Cur.Kind.ToString());
            return new UBasicError(msg + ", got '" + got + "'", Line);
        }

        private static char Suffix(VType t) {
            return t == VType.Int ? '%' : (t == VType.Str ? '$' : '!');
        }
        private static string VarKey(Token t) {
            return t.Raw.ToUpperInvariant() + Suffix(t.Sig);
        }
        private static string VarKey(string rawUpper, VType ty) {
            return rawUpper + Suffix(ty);
        }

        private void SkipTerminators() {
            while (Cur.Kind == T.EOL || Cur.Kind == T.Colon) _p++;
        }
        private void EndOfStatement() {
            if (Cur.Kind == T.EOL || Cur.Kind == T.Colon || Cur.Kind == T.EOF) return;
            throw Err("unexpected token after statement");
        }

        // ---- entry ---------------------------------------------------------

        public static Chunk Compile(string source) {
            Compiler co = new Compiler();
            return co.Run(source);
        }

        private Chunk Run(string source) {
            _t = new Lexer(source).Tokenize();
            _c = new Chunk();
            DefinePredefinedConsts();
            ScanSignatures();
            CompileModule();
            return _c;
        }

        private void DefinePredefinedConsts() {
            DefConst("TRUE%", VType.Int, Slot.FromInt(-1));
            DefConst("FALSE%", VType.Int, Slot.FromInt(0));
            DefConst("PI!", VType.Single, Slot.FromFloat(3.14159265f));
            DefConst("KEY_LEFT%", VType.Int, Slot.FromInt(1));
            DefConst("KEY_RIGHT%", VType.Int, Slot.FromInt(2));
            DefConst("KEY_UP%", VType.Int, Slot.FromInt(3));
            DefConst("KEY_DOWN%", VType.Int, Slot.FromInt(4));
            DefConst("KEY_ESC%", VType.Int, Slot.FromInt(27));
            DefConst("KEY_SPACE%", VType.Int, Slot.FromInt(32));
            DefConst("KEY_ENTER%", VType.Int, Slot.FromInt(13));
        }
        private void DefConst(string key, VType t, Slot v) {
            ConstVal c = new ConstVal(); c.Type = t; c.Val = v;
            _consts[key] = c;
        }

        // ---- pass 1: collect procedure signatures --------------------------

        private void ScanSignatures() {
            for (int i = 0; i < _t.Count; i++) {
                Token tk = _t[i];
                if (!IsKw(tk, "SUB") && !IsKw(tk, "FUNCTION")) continue;
                // "END SUB" / "EXIT SUB" are not declarations
                if (i > 0 && (IsKw(_t[i - 1], "END") || IsKw(_t[i - 1], "EXIT"))) continue;

                bool isFn = tk.Text == "FUNCTION";
                int j = i + 1;
                if (_t[j].Kind != T.Ident) throw new UBasicError(
                    "expected a name after " + tk.Text, _t[j].Line);
                Token nameTok = _t[j++];

                FuncInfo fi = new FuncInfo();
                fi.Name = VarKey(nameTok);
                fi.IsFunction = isFn;
                fi.ReturnType = nameTok.Sig;

                List<VType> ps = new List<VType>();
                if (_t[j].Kind == T.LParen) {
                    j++;
                    if (_t[j].Kind != T.RParen) {
                        while (true) {
                            if (_t[j].Kind != T.Ident) throw new UBasicError(
                                "expected a parameter name", _t[j].Line);
                            VType pt = _t[j].Sig;
                            j++;
                            if (IsKw(_t[j], "AS")) { j++; pt = ParseAsType(_t[j]); j++; }
                            ps.Add(pt);
                            if (_t[j].Kind == T.Comma) { j++; continue; }
                            break;
                        }
                    }
                    if (_t[j].Kind != T.RParen) throw new UBasicError(
                        "expected ')' in parameter list", _t[j].Line);
                }
                fi.ParamTypes = ps.ToArray();
                fi.ParamCount = ps.Count;

                if (_funcs.ContainsKey(fi.Name))
                    throw new UBasicError("duplicate definition of " + nameTok.Raw, nameTok.Line);
                _funcs[fi.Name] = _c.Funcs.Count;
                _c.Funcs.Add(fi);
            }
        }

        private static VType ParseAsType(Token t) {
            if (t.Kind != T.Keyword) throw new UBasicError("expected a type name after AS", t.Line);
            if (t.Text == "INTEGER") return VType.Int;
            if (t.Text == "SINGLE") return VType.Single;
            if (t.Text == "STRING") return VType.Str;
            throw new UBasicError("unknown type '" + t.Text + "'", t.Line);
        }

        // ---- pass 2 --------------------------------------------------------

        private void CompileModule() {
            _p = 0;
            _fn = null;
            _labels = new Dictionary<string, int>();
            _labelFix = new List<KeyValuePair<int, Token>>();
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
                else SkipStatementTokens();
                SkipTerminators();
            }
        }

        private void SkipStatementTokens() {
            while (Cur.Kind != T.EOL && Cur.Kind != T.Colon && Cur.Kind != T.EOF) _p++;
        }

        private void SkipProcedure() {
            string kind = Cur.Text;
            _p++;
            while (Cur.Kind != T.EOF) {
                if (IsKw("END") && IsKw(Peek(1), kind)) { _p += 2; return; }
                _p++;
            }
            throw new UBasicError("missing END " + kind, Line);
        }

        private void CompileProcedure() {
            string kind = Adv().Text;                   // SUB | FUNCTION
            Token nameTok = Adv();
            FuncInfo fi = _c.Funcs[_funcs[VarKey(nameTok)]];

            _fn = fi;
            _locals = new Dictionary<string, int>();
            _localTypes = new List<VType>();
            _labels = new Dictionary<string, int>();
            _labelFix = new List<KeyValuePair<int, Token>>();

            // Parameters occupy locals 0..n-1, in order.
            if (Cur.Kind == T.LParen) {
                _p++;
                if (Cur.Kind != T.RParen) {
                    while (true) {
                        Token pt = Adv();
                        VType ty = pt.Sig;
                        if (AcceptKw("AS")) { ty = ParseAsType(Cur); _p++; }
                        AddLocal(VarKey(pt.Raw.ToUpperInvariant(), ty), ty);
                        if (Accept(T.Comma)) continue;
                        break;
                    }
                }
                Expect(T.RParen, "')'");
            }

            if (fi.IsFunction) {
                fi.ResultSlot = AddLocal(fi.Name, fi.ReturnType);
            }

            fi.Addr = _c.Here;
            SkipTerminators();
            while (!(IsKw("END") && IsKw(Peek(1), kind))) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing END " + kind, Line);
                Statement();
                SkipTerminators();
            }
            _p += 2;                                     // END SUB / END FUNCTION

            _c.Emit(fi.IsFunction ? Op.RetVal : Op.Ret, 0, 0, Line);
            fi.LocalCount = _localTypes.Count;
            fi.LocalTypes = _localTypes.ToArray();
            ResolveLabels();

            _fn = null;
            _locals = null;
            _localTypes = null;
        }

        private void ResolveLabels() {
            for (int i = 0; i < _labelFix.Count; i++) {
                string name = _labelFix[i].Value.Raw.ToUpperInvariant();
                int addr;
                if (!_labels.TryGetValue(name, out addr))
                    throw new UBasicError("undefined label '" + _labelFix[i].Value.Raw + "'",
                        _labelFix[i].Value.Line);
                _c.Patch(_labelFix[i].Key, addr);
            }
            _labelFix.Clear();
        }

        // ---- variable resolution -------------------------------------------

        private int AddLocal(string key, VType t) {
            int slot = _localTypes.Count;
            _locals[key] = slot;
            _localTypes.Add(t);
            return slot;
        }

        private int AddGlobal(string key, VType t) {
            int slot = _c.GlobalTypes.Count;
            _globals[key] = slot;
            _c.GlobalTypes.Add(t);
            _c.GlobalNames.Add(key);
            return slot;
        }

        private struct VarRef { public bool IsLocal; public int Slot; public VType Type; }

        /// <summary>Find a SUB/FUNCTION by name. QBasic insists on the sigil at
        /// every call site; uBasic accepts a bare name when it is unambiguous.</summary>
        private int FindProc(Token t) {
            int idx;
            if (_funcs.TryGetValue(VarKey(t), out idx)) return idx;
            if (t.HadSigil) return -1;
            string b = t.Raw.ToUpperInvariant();
            int found = -1;
            string[] sfx = { "%", "!", "$" };
            for (int i = 0; i < sfx.Length; i++) {
                if (!_funcs.TryGetValue(b + sfx[i], out idx)) continue;
                if (found >= 0) return -1;   // ambiguous, require the sigil
                found = idx;
            }
            return found;
        }

        /// <summary>QBasic scoping: inside a procedure a bare name is local
        /// unless it was declared DIM SHARED at module level.</summary>
        private VarRef ResolveVar(string key, VType ty, bool create) {
            VarRef r = new VarRef();
            r.Type = ty;
            if (_fn != null) {
                int s;
                if (_locals.TryGetValue(key, out s)) { r.IsLocal = true; r.Slot = s; r.Type = _localTypes[s]; return r; }
                if (_shared.Contains(key) && _globals.TryGetValue(key, out s)) {
                    r.IsLocal = false; r.Slot = s; r.Type = _c.GlobalTypes[s]; return r;
                }
                if (!create) { r.Slot = -1; return r; }
                r.IsLocal = true; r.Slot = AddLocal(key, ty); return r;
            }
            int g;
            if (_globals.TryGetValue(key, out g)) { r.IsLocal = false; r.Slot = g; r.Type = _c.GlobalTypes[g]; return r; }
            if (!create) { r.Slot = -1; return r; }
            r.IsLocal = false; r.Slot = AddGlobal(key, ty); return r;
        }

        private int NewTemp(VType t) {
            string key = "$t" + (_tmpSeq++);
            if (_fn != null) return AddLocal(key, t);
            return AddGlobal(key, t);
        }
        private void LoadSlot(VarRef v, int line) {
            _c.Emit(v.IsLocal ? Op.LoadL : Op.LoadG, v.Slot, 0, line);
        }
        private void StoreSlot(VarRef v, int line) {
            _c.Emit(v.IsLocal ? Op.StoreL : Op.StoreG, v.Slot, 0, line);
        }

        // ---- statements -----------------------------------------------------

        private void Statement() {
            if (Cur.Kind == T.EOL || Cur.Kind == T.Colon) return;
            int line = Line;

            // label:
            if (Cur.Kind == T.Ident && Peek(1).Kind == T.Colon && !Cur.HadSigil) {
                string lname = Cur.Raw.ToUpperInvariant();
                if (!_labels.ContainsKey(lname)) {
                    _labels[lname] = _c.Here;
                    _p += 2;
                    return;
                }
            }

            if (Cur.Kind == T.Keyword) {
                switch (Cur.Text) {
                    case "LET":       _p++; AssignStatement(); return;
                    case "DIM":       DimStatement(); return;
                    case "CONST":     ConstStatement(); return;
                    case "IF":        IfStatement(); return;
                    case "FOR":       ForStatement(); return;
                    case "WHILE":     WhileStatement(); return;
                    case "DO":        DoStatement(); return;
                    case "SELECT":    SelectStatement(); return;
                    case "EXIT":      ExitStatement(); return;
                    case "GOTO":      GotoStatement(); return;
                    case "CALL":      _p++; CallStatement(true); return;
                    case "PRINT":     PrintStatement(); return;
                    case "CLS":       ClsStatement(); return;
                    case "SCREEN":    FixedProc("__screen", 2); return;
                    case "PSET":      PSetStatement(); return;
                    case "LINE":      LineStatement(); return;
                    case "CIRCLE":    CircleStatement(); return;
                    case "PAINT":     PaintStatement(); return;
                    case "COLOR":     ColorStatement(); return;
                    case "LOCATE":    FixedProc("__locate", 2); return;
                    case "PALETTE":   FixedProc("__palette", 4); return;
                    case "RANDOMIZE": FixedProc("__randomize", 1); return;
                    case "WAIT":      _p++; _c.Emit(Op.Wait, 0, 0, line); EndOfStatement(); return;
                    case "END":       _p++; _c.Emit(Op.Halt, 0, 0, line); EndOfStatement(); return;
                }
                throw Err("unexpected keyword '" + Cur.Text + "'");
            }

            if (Cur.Kind == T.Ident) {
                int pidx = FindProc(Cur);
                if (pidx >= 0 && !_c.Funcs[pidx].IsFunction) {
                    CallStatement(false);
                    return;
                }
                AssignStatement();
                return;
            }
            throw Err("expected a statement");
        }

        private void AssignStatement() {
            int line = Line;
            Token nameTok = Adv();
            if (nameTok.Kind != T.Ident) throw new UBasicError("expected a variable name", line);
            string key = VarKey(nameTok);

            if (Cur.Kind == T.LParen && _arrays.ContainsKey(key)) {
                int ai = _arrays[key];
                ArrayInfo info = _c.Arrays[ai];
                _p++;
                CoerceTo(Expr(), VType.Int, line);
                bool two = false;
                if (Accept(T.Comma)) { CoerceTo(Expr(), VType.Int, line); two = true; }
                Expect(T.RParen, "')'");
                if (two != (info.D1 >= 0))
                    throw new UBasicError("wrong number of subscripts for " + nameTok.Raw, line);
                Expect(T.Eq, "'='");
                CoerceTo(Expr(), info.Type, line);
                _c.Emit(two ? Op.ArrSet2 : Op.ArrSet1, ai, 0, line);
                EndOfStatement();
                return;
            }

            VarRef v = ResolveVar(key, nameTok.Sig, true);
            Expect(T.Eq, "'='");
            CoerceTo(Expr(), v.Type, line);
            StoreSlot(v, line);
            EndOfStatement();
        }

        private void DimStatement() {
            int line = Line;
            _p++;
            bool shared = AcceptKw("SHARED");
            while (true) {
                Token nameTok = Adv();
                if (nameTok.Kind != T.Ident) throw new UBasicError("expected a name after DIM", line);
                VType ty = nameTok.Sig;

                if (Cur.Kind == T.LParen) {
                    _p++;
                    int d0 = ConstInt();
                    int d1 = -1;
                    if (Accept(T.Comma)) d1 = ConstInt();
                    Expect(T.RParen, "')'");
                    if (AcceptKw("AS")) { ty = ParseAsType(Cur); _p++; }

                    string akey = VarKey(nameTok.Raw.ToUpperInvariant(), ty);
                    if (_arrays.ContainsKey(akey))
                        throw new UBasicError("array " + nameTok.Raw + " already declared", line);
                    ArrayInfo info = new ArrayInfo();
                    info.Name = akey; info.Type = ty; info.D0 = d0; info.D1 = d1;
                    info.Length = (d0 + 1) * (d1 >= 0 ? (d1 + 1) : 1);
                    _arrays[akey] = _c.Arrays.Count;
                    _c.Arrays.Add(info);
                } else {
                    if (AcceptKw("AS")) { ty = ParseAsType(Cur); _p++; }
                    string key = VarKey(nameTok.Raw.ToUpperInvariant(), ty);
                    if (shared || _fn == null) {
                        if (!_globals.ContainsKey(key)) AddGlobal(key, ty);
                        if (shared) _shared.Add(key);
                    } else {
                        if (!_locals.ContainsKey(key)) AddLocal(key, ty);
                    }
                }
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        /// <summary>Array bounds must be compile-time constants: it keeps
        /// allocation entirely out of the running program.</summary>
        private int ConstInt() {
            int line = Line;
            int sign = 1;
            if (Accept(T.Minus)) sign = -1;
            if (Cur.Kind == T.IntLit) return sign * Adv().IVal;
            if (Cur.Kind == T.Ident) {
                ConstVal cv;
                if (_consts.TryGetValue(VarKey(Cur), out cv) && cv.Type != VType.Str) {
                    _p++;
                    return sign * (cv.Type == VType.Int ? cv.Val.I : (int)cv.Val.F);
                }
            }
            throw new UBasicError("array bounds must be a constant integer", line);
        }

        private void ConstStatement() {
            _p++;
            while (true) {
                Token nameTok = Adv();
                if (nameTok.Kind != T.Ident) throw Err("expected a constant name");
                Expect(T.Eq, "'='");
                ConstVal cv = new ConstVal();
                int line = Line;
                if (Cur.Kind == T.StrLit) {
                    cv.Type = VType.Str; cv.Str = Adv().Text;
                } else {
                    int sign = 1;
                    if (Accept(T.Minus)) sign = -1;
                    else Accept(T.Plus);
                    if (Cur.Kind == T.IntLit) { cv.Type = VType.Int; cv.Val = Slot.FromInt(sign * Adv().IVal); }
                    else if (Cur.Kind == T.SingleLit) { cv.Type = VType.Single; cv.Val = Slot.FromFloat(sign * Adv().FVal); }
                    else throw new UBasicError("CONST needs a literal value", line);
                }
                _consts[VarKey(nameTok)] = cv;
                if (Accept(T.Comma)) continue;
                break;
            }
            EndOfStatement();
        }

        private void IfStatement() {
            int line = Line;
            _p++;
            CoerceTo(Expr(), VType.Int, line);
            ExpectKw("THEN");

            List<int> endJumps = new List<int>();

            // Single-line form: IF c THEN stmt [: stmt ...] [ELSE stmt [: stmt ...]]
            // Everything after THEN on the line belongs to the THEN clause,
            // colon-separated statements included -- this is what QBasic does.
            if (Cur.Kind != T.EOL && Cur.Kind != T.Colon && Cur.Kind != T.EOF) {
                int jf = _c.Emit(Op.JmpF, 0, 0, line);
                SingleLineBody();
                if (IsKw("ELSE")) {
                    _p++;
                    int je = _c.Emit(Op.Jmp, 0, 0, line);
                    _c.Patch(jf, _c.Here);
                    SingleLineBody();
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
                while (!IsKw("ELSEIF") && !IsKw("ELSE") && !(IsKw("END") && IsKw(Peek(1), "IF"))) {
                    if (Cur.Kind == T.EOF) throw new UBasicError("missing END IF", line);
                    Statement();
                    SkipTerminators();
                }
                if (IsKw("ELSEIF")) {
                    endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                    _c.Patch(jf, _c.Here);
                    _p++;
                    CoerceTo(Expr(), VType.Int, Line);
                    ExpectKw("THEN");
                    continue;
                }
                if (IsKw("ELSE")) {
                    endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                    _c.Patch(jf, _c.Here);
                    _p++;
                    SkipTerminators();
                    while (!(IsKw("END") && IsKw(Peek(1), "IF"))) {
                        if (Cur.Kind == T.EOF) throw new UBasicError("missing END IF", line);
                        Statement();
                        SkipTerminators();
                    }
                    _p += 2;
                    break;
                }
                _c.Patch(jf, _c.Here);
                _p += 2;                                  // END IF
                break;
            }
            for (int i = 0; i < endJumps.Count; i++) _c.Patch(endJumps[i], _c.Here);
        }

        /// <summary>One or more colon-separated statements up to end of line
        /// or an ELSE, used by the single-line IF form.</summary>
        private void SingleLineBody() {
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
            Token nameTok = Adv();
            if (nameTok.Kind != T.Ident) throw new UBasicError("expected a loop variable", line);
            VarRef v = ResolveVar(VarKey(nameTok), nameTok.Sig, true);
            if (v.Type == VType.Str) throw new UBasicError("FOR variable cannot be a string", line);

            Expect(T.Eq, "'='");
            CoerceTo(Expr(), v.Type, line);
            StoreSlot(v, line);

            ExpectKw("TO");
            int limitSlot = NewTemp(v.Type);
            VarRef lim = MakeRef(limitSlot, v.Type);
            CoerceTo(Expr(), v.Type, line);
            StoreSlot(lim, line);

            int stepSlot = NewTemp(v.Type);
            VarRef stp = MakeRef(stepSlot, v.Type);
            if (AcceptKw("STEP")) CoerceTo(Expr(), v.Type, line);
            else EmitConst(v.Type, 1, line);
            StoreSlot(stp, line);

            LoopCtx ctx = new LoopCtx();
            ctx.Kind = "FOR";
            ctx.Top = _c.Here;
            _loops.Add(ctx);

            // Continue while (limit - i) * step >= 0. One test that is correct
            // for both directions, and for a step that is not a constant.
            LoadSlot(lim, line);
            LoadSlot(v, line);
            _c.Emit(v.Type == VType.Int ? Op.SubI : Op.SubF, 0, 0, line);
            LoadSlot(stp, line);
            _c.Emit(v.Type == VType.Int ? Op.MulI : Op.MulF, 0, 0, line);
            EmitConst(v.Type, 0, line);
            _c.Emit(v.Type == VType.Int ? Op.GeI : Op.GeF, 0, 0, line);
            int exitJ = _c.Emit(Op.JmpF, 0, 0, line);

            SkipTerminators();
            while (!IsKw("NEXT")) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing NEXT", line);
                Statement();
                SkipTerminators();
            }
            _p++;
            if (Cur.Kind == T.Ident) _p++;               // optional NEXT i

            int contTarget = _c.Here;
            LoadSlot(v, line);
            LoadSlot(stp, line);
            _c.Emit(v.Type == VType.Int ? Op.AddI : Op.AddF, 0, 0, line);
            StoreSlot(v, line);
            _c.Emit(Op.Jmp, ctx.Top, 0, line);
            _c.Patch(exitJ, _c.Here);

            for (int i = 0; i < ctx.ExitJumps.Count; i++) _c.Patch(ctx.ExitJumps[i], _c.Here);
            for (int i = 0; i < ctx.ContinueJumps.Count; i++) _c.Patch(ctx.ContinueJumps[i], contTarget);
            _loops.RemoveAt(_loops.Count - 1);
        }

        private VarRef MakeRef(int slot, VType t) {
            VarRef r = new VarRef();
            r.Slot = slot; r.Type = t; r.IsLocal = _fn != null;
            return r;
        }
        private void EmitConst(VType t, int v, int line) {
            if (t == VType.Int) _c.Emit(Op.PushI, v, 0, line);
            else _c.Emit(Op.PushF, BitConv.FloatToInt(v), 0, line);
        }

        private void WhileStatement() {
            int line = Line;
            _p++;
            LoopCtx ctx = new LoopCtx();
            ctx.Kind = "WHILE";
            ctx.Top = _c.Here;
            _loops.Add(ctx);

            CoerceTo(Expr(), VType.Int, line);
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
            FinishLoop(ctx, ctx.Top);
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
                CoerceTo(Expr(), VType.Int, line);
                exitJ = _c.Emit(Op.JmpF, 0, 0, line);
            } else if (AcceptKw("UNTIL")) {
                CoerceTo(Expr(), VType.Int, line);
                exitJ = _c.Emit(Op.JmpT, 0, 0, line);
            }

            SkipTerminators();
            while (!IsKw("LOOP")) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing LOOP", line);
                Statement();
                SkipTerminators();
            }
            _p++;

            int contTarget = _c.Here;
            if (AcceptKw("WHILE")) {
                CoerceTo(Expr(), VType.Int, Line);
                _c.Emit(Op.JmpT, ctx.Top, 0, line);
            } else if (AcceptKw("UNTIL")) {
                CoerceTo(Expr(), VType.Int, Line);
                _c.Emit(Op.JmpF, ctx.Top, 0, line);
            } else {
                _c.Emit(Op.Jmp, ctx.Top, 0, line);
            }
            if (exitJ >= 0) _c.Patch(exitJ, _c.Here);
            FinishLoop(ctx, contTarget);
        }

        private void FinishLoop(LoopCtx ctx, int contTarget) {
            for (int i = 0; i < ctx.ExitJumps.Count; i++) _c.Patch(ctx.ExitJumps[i], _c.Here);
            for (int i = 0; i < ctx.ContinueJumps.Count; i++) _c.Patch(ctx.ContinueJumps[i], contTarget);
            _loops.RemoveAt(_loops.Count - 1);
        }

        private void SelectStatement() {
            int line = Line;
            _p++;
            ExpectKw("CASE");
            VType st = Expr();
            if (st == VType.Single) { /* ok */ }
            int tmp = NewTemp(st);
            VarRef sv = MakeRef(tmp, st);
            StoreSlot(sv, line);

            List<int> endJumps = new List<int>();
            SkipTerminators();
            bool sawElse = false;

            while (!(IsKw("END") && IsKw(Peek(1), "SELECT"))) {
                if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                ExpectKw("CASE");

                if (AcceptKw("ELSE")) {
                    sawElse = true;
                    SkipTerminators();
                    while (!(IsKw("END") && IsKw(Peek(1), "SELECT"))) {
                        if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                        Statement();
                        SkipTerminators();
                    }
                    break;
                }

                // One or more comma-separated values, ORed together.
                List<int> matchJumps = new List<int>();
                while (true) {
                    LoadSlot(sv, Line);
                    CoerceTo(Expr(), st, Line);
                    _c.Emit(st == VType.Int ? Op.EqI : st == VType.Str ? Op.EqS : Op.EqF, 0, 0, Line);
                    matchJumps.Add(_c.Emit(Op.JmpT, 0, 0, Line));
                    if (Accept(T.Comma)) continue;
                    break;
                }
                int noMatch = _c.Emit(Op.Jmp, 0, 0, Line);
                for (int i = 0; i < matchJumps.Count; i++) _c.Patch(matchJumps[i], _c.Here);

                SkipTerminators();
                while (!IsKw("CASE") && !(IsKw("END") && IsKw(Peek(1), "SELECT"))) {
                    if (Cur.Kind == T.EOF) throw new UBasicError("missing END SELECT", line);
                    Statement();
                    SkipTerminators();
                }
                endJumps.Add(_c.Emit(Op.Jmp, 0, 0, Line));
                _c.Patch(noMatch, _c.Here);
            }
            if (sawElse) { /* fall through to END SELECT */ }
            _p += 2;
            for (int i = 0; i < endJumps.Count; i++) _c.Patch(endJumps[i], _c.Here);
        }

        private void ExitStatement() {
            int line = Line;
            _p++;
            if (AcceptKw("SUB")) {
                if (_fn == null || _fn.IsFunction) throw new UBasicError("EXIT SUB outside a SUB", line);
                _c.Emit(Op.Ret, 0, 0, line);
            } else if (AcceptKw("FUNCTION")) {
                if (_fn == null || !_fn.IsFunction) throw new UBasicError("EXIT FUNCTION outside a FUNCTION", line);
                _c.Emit(Op.RetVal, 0, 0, line);
            } else if (AcceptKw("FOR") || AcceptKw("DO") || AcceptKw("WHILE")) {
                if (_loops.Count == 0) throw new UBasicError("EXIT outside a loop", line);
                _loops[_loops.Count - 1].ExitJumps.Add(_c.Emit(Op.Jmp, 0, 0, line));
            } else throw Err("expected FOR, DO, WHILE, SUB or FUNCTION after EXIT");
            EndOfStatement();
        }

        private void GotoStatement() {
            int line = Line;
            _p++;
            Token lbl = Adv();
            if (lbl.Kind != T.Ident) throw new UBasicError("expected a label after GOTO", line);
            int at = _c.Emit(Op.Jmp, 0, 0, line);
            _labelFix.Add(new KeyValuePair<int, Token>(at, lbl));
            EndOfStatement();
        }

        private void CallStatement(bool parenForm) {
            int line = Line;
            Token nameTok = Adv();
            int fidx = FindProc(nameTok);
            if (fidx < 0)
                throw new UBasicError("unknown SUB '" + nameTok.Raw + "'", line);
            FuncInfo fi = _c.Funcs[fidx];

            bool paren = Accept(T.LParen);
            int argc = 0;
            if (paren) {
                if (Cur.Kind != T.RParen) {
                    while (true) {
                        if (argc >= fi.ParamCount)
                            throw new UBasicError("too many arguments to " + nameTok.Raw, line);
                        CoerceTo(Expr(), fi.ParamTypes[argc], line);
                        argc++;
                        if (Accept(T.Comma)) continue;
                        break;
                    }
                }
                Expect(T.RParen, "')'");
            } else if (Cur.Kind != T.EOL && Cur.Kind != T.Colon && Cur.Kind != T.EOF) {
                while (true) {
                    if (argc >= fi.ParamCount)
                        throw new UBasicError("too many arguments to " + nameTok.Raw, line);
                    CoerceTo(Expr(), fi.ParamTypes[argc], line);
                    argc++;
                    if (Accept(T.Comma)) continue;
                    break;
                }
            }
            if (argc != fi.ParamCount)
                throw new UBasicError(nameTok.Raw + " expects " + fi.ParamCount +
                    " argument(s), got " + argc, line);
            _c.Emit(Op.Call, fidx, argc, line);
            if (fi.IsFunction) _c.Emit(Op.Pop, 0, 0, line);
            EndOfStatement();
        }

        // ---- graphics / print statements -------------------------------------

        private void FixedProc(string host, int n) {
            int line = Line;
            _p++;
            for (int i = 0; i < n; i++) {
                if (i > 0) Expect(T.Comma, "','");
                CoerceTo(Expr(), VType.Single, line);
            }
            EmitHost(host, n, line);
            EndOfStatement();
        }

        private void EmitHost(string name, int argc, int line) {
            int h = Host.Find(name, argc);
            if (h < 0) throw new UBasicError("internal: no host '" + name + "/" + argc + "'", line);
            _c.Emit(Op.CallHost, h, argc, line);
        }

        private void ClsStatement() {
            int line = Line;
            _p++;
            if (Cur.Kind == T.EOL || Cur.Kind == T.Colon || Cur.Kind == T.EOF) {
                EmitHost("__cls0", 0, line);
            } else {
                CoerceTo(Expr(), VType.Single, line);
                EmitHost("__cls", 1, line);
            }
            EndOfStatement();
        }

        private void ColorStatement() {
            int line = Line;
            _p++;
            CoerceTo(Expr(), VType.Single, line);
            if (Accept(T.Comma)) {
                CoerceTo(Expr(), VType.Single, line);
                EmitHost("__color", 2, line);
            } else {
                EmitHost("__colorfg", 1, line);
            }
            EndOfStatement();
        }

        /// <summary>Parses the QBasic "(x, y)" coordinate group.</summary>
        private void Coord(int line) {
            Expect(T.LParen, "'('");
            CoerceTo(Expr(), VType.Single, line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);
            Expect(T.RParen, "')'");
        }

        private void PSetStatement() {
            int line = Line;
            _p++;
            Coord(line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);
            EmitHost("__pset", 3, line);
            EndOfStatement();
        }

        private void LineStatement() {
            int line = Line;
            _p++;
            Coord(line);
            Expect(T.Minus, "'-'");
            Coord(line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);
            string host = "__line";
            if (Accept(T.Comma)) {
                if (AcceptKw("BF")) host = "__boxf";
                else if (AcceptKw("B")) host = "__box";
                else throw Err("expected B or BF");
            }
            EmitHost(host, 5, line);
            EndOfStatement();
        }

        private void CircleStatement() {
            int line = Line;
            _p++;
            Coord(line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);   // radius
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);   // colour
            string host = "__circle";
            if (Accept(T.Comma)) {
                if (AcceptKw("F")) host = "__circlef";
                else throw Err("expected F");
            }
            EmitHost(host, 4, line);
            EndOfStatement();
        }

        private void PaintStatement() {
            int line = Line;
            _p++;
            Coord(line);
            Expect(T.Comma, "','");
            CoerceTo(Expr(), VType.Single, line);
            EmitHost("__paint", 3, line);
            EndOfStatement();
        }

        private void PrintStatement() {
            int line = Line;
            _p++;
            bool suppressNewline = false;

            while (Cur.Kind != T.EOL && Cur.Kind != T.Colon && Cur.Kind != T.EOF) {
                if (Accept(T.Semi)) { suppressNewline = true; continue; }
                if (Accept(T.Comma)) { EmitHost("__printtab", 0, line); suppressNewline = true; continue; }

                VType t = Expr();
                if (t == VType.Str) EmitHost("__prints", 1, line);
                else if (t == VType.Int) EmitHost("__printi", 1, line);
                else EmitHost("__printf", 1, line);
                suppressNewline = false;
            }
            if (!suppressNewline) EmitHost("__printnl", 0, line);
            EndOfStatement();
        }

        // ---- expressions -----------------------------------------------------

        private void CoerceTo(VType from, VType to, int line) {
            if (from == to) return;
            if (from == VType.Str || to == VType.Str)
                throw new UBasicError("type mismatch: cannot use a " +
                    (from == VType.Str ? "string" : "number") + " here", line);
            _c.Emit(from == VType.Int ? Op.I2F : Op.F2I, 0, 0, line);
        }

        private VType Expr() { return OrExpr(); }

        private VType OrExpr() {
            VType t = AndExpr();
            while (IsKw("OR") || IsKw("XOR")) {
                bool isXor = Cur.Text == "XOR";
                int line = Line; _p++;
                CoerceTo(t, VType.Int, line);
                VType r = AndExpr();
                CoerceTo(r, VType.Int, line);
                _c.Emit(isXor ? Op.XorI : Op.OrI, 0, 0, line);
                t = VType.Int;
            }
            return t;
        }

        private VType AndExpr() {
            VType t = NotExpr();
            while (IsKw("AND")) {
                int line = Line; _p++;
                CoerceTo(t, VType.Int, line);
                VType r = NotExpr();
                CoerceTo(r, VType.Int, line);
                _c.Emit(Op.AndI, 0, 0, line);
                t = VType.Int;
            }
            return t;
        }

        private VType NotExpr() {
            if (IsKw("NOT")) {
                int line = Line; _p++;
                VType t = NotExpr();
                CoerceTo(t, VType.Int, line);
                _c.Emit(Op.NotI, 0, 0, line);
                return VType.Int;
            }
            return RelExpr();
        }

        private VType RelExpr() {
            VType t = AddExpr();
            while (Cur.Kind == T.Eq || Cur.Kind == T.Ne || Cur.Kind == T.Lt ||
                   Cur.Kind == T.Le || Cur.Kind == T.Gt || Cur.Kind == T.Ge) {
                T k = Cur.Kind;
                int line = Line; _p++;
                VType r = AddExpr();
                VType common = Unify(t, r, line);
                Op op;
                if (common == VType.Str) {
                    op = k == T.Eq ? Op.EqS : k == T.Ne ? Op.NeS : k == T.Lt ? Op.LtS :
                         k == T.Le ? Op.LeS : k == T.Gt ? Op.GtS : Op.GeS;
                } else if (common == VType.Int) {
                    op = k == T.Eq ? Op.EqI : k == T.Ne ? Op.NeI : k == T.Lt ? Op.LtI :
                         k == T.Le ? Op.LeI : k == T.Gt ? Op.GtI : Op.GeI;
                } else {
                    op = k == T.Eq ? Op.EqF : k == T.Ne ? Op.NeF : k == T.Lt ? Op.LtF :
                         k == T.Le ? Op.LeF : k == T.Gt ? Op.GtF : Op.GeF;
                }
                _c.Emit(op, 0, 0, line);
                t = VType.Int;
            }
            return t;
        }

        /// <summary>Bring the two operands already on the stack to a common
        /// type. I2FUnder exists precisely for the case where the deeper
        /// operand is the one needing promotion.</summary>
        private VType Unify(VType l, VType r, int line) {
            if (l == r) return l;
            if (l == VType.Str || r == VType.Str)
                throw new UBasicError("type mismatch between string and number", line);
            if (l == VType.Int) { _c.Emit(Op.I2FUnder, 0, 0, line); return VType.Single; }
            _c.Emit(Op.I2F, 0, 0, line);
            return VType.Single;
        }

        private VType AddExpr() {
            VType t = MulExpr();
            while (Cur.Kind == T.Plus || Cur.Kind == T.Minus) {
                bool plus = Cur.Kind == T.Plus;
                int line = Line; _p++;
                VType r = MulExpr();
                if (t == VType.Str && r == VType.Str) {
                    if (!plus) throw new UBasicError("cannot subtract strings", line);
                    _c.Emit(Op.Concat, 0, 0, line);
                    t = VType.Str;
                    continue;
                }
                VType common = Unify(t, r, line);
                _c.Emit(common == VType.Int ? (plus ? Op.AddI : Op.SubI)
                                            : (plus ? Op.AddF : Op.SubF), 0, 0, line);
                t = common;
            }
            return t;
        }

        private VType MulExpr() {
            VType t = UnaryExpr();
            while (Cur.Kind == T.Star || Cur.Kind == T.Slash ||
                   Cur.Kind == T.Backslash || IsKw("MOD")) {
                T k = Cur.Kind;
                bool isMod = IsKw("MOD");
                int line = Line; _p++;
                VType r = UnaryExpr();

                if (isMod || k == T.Backslash) {
                    // Integer-only operators: force both sides down to INTEGER.
                    if (t == VType.Single) { _c.Emit(Op.F2I, 0, 0, line); }
                    if (r == VType.Single) { _c.Emit(Op.F2I, 0, 0, line); }
                    else if (t == VType.Single && r == VType.Int) { }
                    CoerceTo(r == VType.Single ? VType.Int : r, VType.Int, line);
                    _c.Emit(isMod ? Op.ModI : Op.IDiv, 0, 0, line);
                    t = VType.Int;
                    continue;
                }
                if (k == T.Slash) {
                    // QBasic's "/" is always floating point.
                    if (t == VType.Int && r == VType.Int) { _c.Emit(Op.I2FUnder, 0, 0, line); _c.Emit(Op.I2F, 0, 0, line); }
                    else Unify(t, r, line);
                    _c.Emit(Op.DivF, 0, 0, line);
                    t = VType.Single;
                    continue;
                }
                VType common = Unify(t, r, line);
                _c.Emit(common == VType.Int ? Op.MulI : Op.MulF, 0, 0, line);
                t = common;
            }
            return t;
        }

        private VType UnaryExpr() {
            if (Cur.Kind == T.Minus) {
                int line = Line; _p++;
                VType t = UnaryExpr();
                if (t == VType.Str) throw new UBasicError("cannot negate a string", line);
                _c.Emit(t == VType.Int ? Op.NegI : Op.NegF, 0, 0, line);
                return t;
            }
            if (Cur.Kind == T.Plus) { _p++; return UnaryExpr(); }
            return PowExpr();
        }

        private VType PowExpr() {
            VType t = Primary();
            if (Cur.Kind == T.Caret) {
                int line = Line; _p++;
                CoerceTo(t, VType.Single, line);
                VType r = UnaryExpr();
                CoerceTo(r, VType.Single, line);
                _c.Emit(Op.PowF, 0, 0, line);
                return VType.Single;
            }
            return t;
        }

        private VType Primary() {
            int line = Line;

            if (Cur.Kind == T.IntLit) { _c.Emit(Op.PushI, Adv().IVal, 0, line); return VType.Int; }
            if (Cur.Kind == T.SingleLit) {
                _c.Emit(Op.PushF, BitConv.FloatToInt(Adv().FVal), 0, line);
                return VType.Single;
            }
            if (Cur.Kind == T.StrLit) {
                int idx = _c.StringConsts.Count;
                _c.StringConsts.Add(Adv().Text);
                _c.Emit(Op.PushS, idx, 0, line);
                return VType.Str;
            }
            if (Accept(T.LParen)) {
                VType t = Expr();
                Expect(T.RParen, "')'");
                return t;
            }

            if (Cur.Kind == T.Ident) {
                Token nameTok = Cur;
                string key = VarKey(nameTok);

                ConstVal cv;
                if (_consts.TryGetValue(key, out cv)) {
                    _p++;
                    if (cv.Type == VType.Str) {
                        int idx = _c.StringConsts.Count;
                        _c.StringConsts.Add(cv.Str);
                        _c.Emit(Op.PushS, idx, 0, line);
                        return VType.Str;
                    }
                    _c.Emit(cv.Type == VType.Int ? Op.PushI : Op.PushF,
                            cv.Type == VType.Int ? cv.Val.I : BitConv.FloatToInt(cv.Val.F), 0, line);
                    return cv.Type;
                }

                int ai;
                if (_arrays.TryGetValue(key, out ai)) {
                    _p++;
                    ArrayInfo info = _c.Arrays[ai];
                    Expect(T.LParen, "'('");
                    CoerceTo(Expr(), VType.Int, line);
                    bool two = false;
                    if (Accept(T.Comma)) { CoerceTo(Expr(), VType.Int, line); two = true; }
                    Expect(T.RParen, "')'");
                    if (two != (info.D1 >= 0))
                        throw new UBasicError("wrong number of subscripts for " + nameTok.Raw, line);
                    _c.Emit(two ? Op.ArrGet2 : Op.ArrGet1, ai, 0, line);
                    return info.Type;
                }

                int fidx = FindProc(nameTok);
                if (fidx >= 0) {
                    FuncInfo fi = _c.Funcs[fidx];
                    if (!fi.IsFunction) throw new UBasicError(
                        nameTok.Raw + " is a SUB and has no value", line);
                    // Inside a FUNCTION, the function's own name is its result variable.
                    if (_fn == fi) { _p++; _c.Emit(Op.LoadL, fi.ResultSlot, 0, line); return fi.ReturnType; }
                    _p++;
                    int argc = 0;
                    if (Accept(T.LParen)) {
                        if (Cur.Kind != T.RParen) {
                            while (true) {
                                if (argc >= fi.ParamCount) throw new UBasicError(
                                    "too many arguments to " + nameTok.Raw, line);
                                CoerceTo(Expr(), fi.ParamTypes[argc], line);
                                argc++;
                                if (Accept(T.Comma)) continue;
                                break;
                            }
                        }
                        Expect(T.RParen, "')'");
                    }
                    if (argc != fi.ParamCount) throw new UBasicError(
                        nameTok.Raw + " expects " + fi.ParamCount + " argument(s), got " + argc, line);
                    _c.Emit(Op.Call, fidx, argc, line);
                    return fi.ReturnType;
                }

                string hostName = nameTok.Raw.ToUpperInvariant() + (nameTok.HadSigil ? Suffix(nameTok.Sig).ToString() : "");
                if (Host.Exists(hostName)) {
                    _p++;
                    return HostCall(hostName, nameTok, line);
                }

                // Plain variable read. Reading an undeclared name yields its
                // zero value, as QBasic does -- but a name used with a
                // subscript list was meant to be an array or a function, and
                // silently turning that into a variable hides typos.
                _p++;
                if (Cur.Kind == T.LParen)
                    throw new UBasicError("unknown array or function '" + nameTok.Raw + "'", line);
                VarRef v = ResolveVar(key, nameTok.Sig, true);
                LoadSlot(v, line);
                return v.Type;
            }

            throw Err("expected a value");
        }

        private VType HostCall(string hostName, Token nameTok, int line) {
            List<VType> argTypes = new List<VType>();
            List<int> argStart = new List<int>();

            if (Cur.Kind == T.LParen) {
                _p++;
                if (Cur.Kind != T.RParen) {
                    while (true) {
                        argStart.Add(_c.Here);
                        argTypes.Add(Expr());
                        if (Accept(T.Comma)) continue;
                        break;
                    }
                }
                Expect(T.RParen, "')'");
            }

            int h = Host.Find(hostName, argTypes.Count);
            if (h < 0) {
                int min = Host.MinArgc(hostName);
                throw new UBasicError(nameTok.Raw + " does not take " + argTypes.Count +
                    " argument(s)" + (min >= 0 ? " (expected " + min + ")" : ""), line);
            }
            HostFn fn = Host.All[h];
            if (!fn.HasRet) throw new UBasicError(nameTok.Raw + " has no value", line);

            // The overload is only known once the arguments are parsed, so any
            // conversions are spliced in afterwards. Walking backwards keeps the
            // recorded start offsets valid as the code shifts.
            for (int i = argTypes.Count - 1; i >= 0; i--) {
                VType have = argTypes[i], want = fn.Params[i];
                if (have == want) continue;
                if (have == VType.Str || want == VType.Str)
                    throw new UBasicError("argument " + (i + 1) + " of " + nameTok.Raw +
                        " should be a " + (want == VType.Str ? "string" : "number"), line);
                int at = (i == argTypes.Count - 1) ? _c.Here : argStart[i + 1];
                _c.Insert(at, have == VType.Int ? Op.I2F : Op.F2I, line);
            }

            _c.Emit(Op.CallHost, h, argTypes.Count, line);
            return fn.Ret;
        }
    }

    /// <summary>Float/int bit reinterpretation without BitConverter allocations.</summary>
    public static class BitConv {
        public static int FloatToInt(float f) { Slot s = new Slot(); s.F = f; return s.I; }
        public static float IntToFloat(int i) { Slot s = new Slot(); s.I = i; return s.F; }
    }
}
