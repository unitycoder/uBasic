using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UBasic {

    public enum T {
        EOF, EOL, Ident, IntLit, SingleLit, StrLit, Keyword,
        Plus, Minus, Star, Slash, Backslash, Caret,
        LParen, RParen, Comma, Semi, Colon, Eq, Ne, Lt, Le, Gt, Ge
    }

    public struct Token {
        public T Kind;
        public string Text;     // identifier/keyword text, uppercased
        public string Raw;      // original spelling, for error messages
        public int IVal;
        public float FVal;
        public VType Sig;       // sigil type for identifiers
        public bool HadSigil;
        public int Line;
    }

    public class Lexer {
        private static readonly HashSet<string> Keywords = new HashSet<string> {
            "AND","AS","B","BF","CALL","CASE","CIRCLE","CLS","COLOR","CONST","DIM","DO",
            "ELSE","ELSEIF","END","EXIT","F","FOR","FUNCTION","GOTO","IF","INTEGER",
            "LET","LINE","LOCATE","LOOP","MOD","NEXT","NOT","OR","PAINT","PALETTE",
            "PRINT","PSET","RANDOMIZE","REM","SCREEN","SELECT","SHARED","SINGLE",
            "STEP","STRING","SUB","THEN","TO","UNTIL","WAIT","WEND","WHILE","XOR"
        };

        private readonly string _src;
        private int _p;
        private int _line = 1;

        public Lexer(string src) { _src = src ?? ""; }

        private char Cur { get { return _p < _src.Length ? _src[_p] : '\0'; } }
        private char Nxt { get { return _p + 1 < _src.Length ? _src[_p + 1] : '\0'; } }

        public List<Token> Tokenize() {
            List<Token> outp = new List<Token>();
            while (true) {
                Token t = Next();
                outp.Add(t);
                if (t.Kind == T.EOF) break;
            }
            return outp;
        }

        private Token Mk(T k) { Token t = new Token(); t.Kind = k; t.Line = _line; return t; }

        private Token Next() {
            // Skip spaces, comments and the `_` line continuation.
            while (true) {
                char c = Cur;
                if (c == ' ' || c == '\t' || c == '\r') { _p++; continue; }
                if (c == '_' && (Nxt == '\n' || Nxt == '\r')) {
                    _p++;
                    while (Cur == '\r') _p++;
                    if (Cur == '\n') { _p++; _line++; }
                    continue;
                }
                if (c == '\'') { while (Cur != '\n' && Cur != '\0') _p++; continue; }
                if ((c == 'R' || c == 'r') && MatchWordAhead("REM")) {
                    while (Cur != '\n' && Cur != '\0') _p++;
                    continue;
                }
                break;
            }

            if (_p >= _src.Length) return Mk(T.EOF);

            char ch = Cur;

            if (ch == '\n') { Token t = Mk(T.EOL); _p++; _line++; return t; }

            if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(Nxt))) return Number();
            if (ch == '&' && (Nxt == 'H' || Nxt == 'h')) return HexNumber();
            if (ch == '"') return Str();
            if (char.IsLetter(ch) || ch == '_') return Word();

            _p++;
            switch (ch) {
                case '+': return Mk(T.Plus);
                case '-': return Mk(T.Minus);
                case '*': return Mk(T.Star);
                case '/': return Mk(T.Slash);
                case '\\': return Mk(T.Backslash);
                case '^': return Mk(T.Caret);
                case '(': return Mk(T.LParen);
                case ')': return Mk(T.RParen);
                case ',': return Mk(T.Comma);
                case ';': return Mk(T.Semi);
                case ':': return Mk(T.Colon);
                case '=': return Mk(T.Eq);
                case '<':
                    if (Cur == '>') { _p++; return Mk(T.Ne); }
                    if (Cur == '=') { _p++; return Mk(T.Le); }
                    return Mk(T.Lt);
                case '>':
                    if (Cur == '=') { _p++; return Mk(T.Ge); }
                    return Mk(T.Gt);
            }
            throw new UBasicError("unexpected character '" + ch + "'", _line);
        }

        private bool MatchWordAhead(string kw) {
            if (_p + kw.Length > _src.Length) return false;
            for (int i = 0; i < kw.Length; i++)
                if (char.ToUpperInvariant(_src[_p + i]) != kw[i]) return false;
            int after = _p + kw.Length;
            if (after < _src.Length) {
                char a = _src[after];
                if (char.IsLetterOrDigit(a) || a == '_' || a == '%' || a == '!' || a == '$')
                    return false;
            }
            return true;
        }

        private Token Number() {
            int start = _p;
            bool isFloat = false;
            while (char.IsDigit(Cur)) _p++;
            if (Cur == '.') { isFloat = true; _p++; while (char.IsDigit(Cur)) _p++; }
            if (Cur == 'E' || Cur == 'e') {
                int save = _p;
                _p++;
                if (Cur == '+' || Cur == '-') _p++;
                if (char.IsDigit(Cur)) { isFloat = true; while (char.IsDigit(Cur)) _p++; }
                else _p = save;
            }
            string text = _src.Substring(start, _p - start);

            // An explicit sigil overrides the inferred literal type.
            if (Cur == '!') { _p++; isFloat = true; }
            else if (Cur == '%') { _p++; isFloat = false; }

            if (!isFloat) {
                int iv;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) {
                    Token t = Mk(T.IntLit); t.IVal = iv; t.Raw = text; return t;
                }
                isFloat = true;
            }
            Token f = Mk(T.SingleLit);
            f.FVal = float.Parse(text, CultureInfo.InvariantCulture);
            f.Raw = text;
            return f;
        }

        private Token HexNumber() {
            _p += 2;
            int start = _p;
            while (Uri.IsHexDigit(Cur)) _p++;
            if (_p == start) throw new UBasicError("malformed &H literal", _line);
            Token t = Mk(T.IntLit);
            t.IVal = unchecked((int)Convert.ToUInt32(_src.Substring(start, _p - start), 16));
            return t;
        }

        private Token Str() {
            _p++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true) {
                if (_p >= _src.Length || Cur == '\n')
                    throw new UBasicError("unterminated string", _line);
                if (Cur == '"') {
                    if (Nxt == '"') { sb.Append('"'); _p += 2; continue; } // "" -> "
                    _p++;
                    break;
                }
                sb.Append(Cur);
                _p++;
            }
            Token t = Mk(T.StrLit);
            t.Text = sb.ToString();
            return t;
        }

        private Token Word() {
            int start = _p;
            while (char.IsLetterOrDigit(Cur) || Cur == '_') _p++;
            string raw = _src.Substring(start, _p - start);

            VType sig = VType.Single;   // QBasic default when no sigil is given
            bool had = false;
            if (Cur == '%') { sig = VType.Int; had = true; _p++; }
            else if (Cur == '!') { sig = VType.Single; had = true; _p++; }
            else if (Cur == '$') { sig = VType.Str; had = true; _p++; }

            string up = raw.ToUpperInvariant();
            Token t = Mk(Keywords.Contains(up) && !had ? T.Keyword : T.Ident);
            t.Text = up + (had ? (sig == VType.Int ? "%" : sig == VType.Str ? "$" : "!") : "");
            t.Raw = raw;
            t.Sig = sig;
            t.HadSigil = had;
            return t;
        }
    }
}
