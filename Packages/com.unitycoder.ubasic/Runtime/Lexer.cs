using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Unitycoder.UBasic {

    public enum T {
        EOF, EOL, Ident, IntLit, SingleLit, StrLit, Keyword,
        Plus, Minus, Star, Slash, Backslash, Caret,
        LParen, RParen, Comma, Semi, Colon, Eq, Ne, Lt, Le, Gt, Ge,
        Dot, Hash,
        /// <summary>The raw text of one DATA statement.</summary>
        Data
    }

    public struct Token {
        public T Kind;
        public string Text;     // identifier/keyword text, uppercased
        public string Raw;      // original spelling, for error messages
        public int IVal;
        public double FVal;
        public VType Sig;       // type from the sigil, or the DEFtype default
        public bool HadSigil;
        public int Line;
    }

    public class Lexer {
        // Only words QBasic itself reserves, so that classic programs are
        // free to call a variable b, f, count or radius.
        private static readonly HashSet<string> Keywords = new HashSet<string> {
            "AND","ANY","AS","BEEP","CALL","CASE","CIRCLE","CLEAR","CLS","COLOR","COMMON",
            "CONST","DATA","DECLARE","DEF","DEFDBL","DEFINT","DEFLNG","DEFSNG","DEFSTR",
            "DIM","DO","DOUBLE","ELSE","ELSEIF","END","ENDIF","EQV","ERASE","ERROR","EXIT",
            "FOR","FUNCTION","GET","GOSUB","GOTO","IF","IMP","INPUT","INTEGER","IS","LET",
            "LINE","LOCATE","LONG","LOOP","MOD","NEXT","NOT","ON","OPTION","OR","OUT",
            "PAINT","PALETTE","PLAY","POKE","PRESET","PRINT","PSET","PUT","RANDOMIZE",
            "READ","REDIM","REM","RESTORE","RESUME","RETURN","SCREEN","SELECT","SHARED",
            "SINGLE","SLEEP","SOUND","STATIC","STEP","STOP","STRING","SUB","SWAP","SYSTEM",
            "THEN","TO","TYPE","UNTIL","USING","VIEW","WAIT","WEND","WHILE","WIDTH","XOR"
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
                if (c == ' ' || c == '\t' || c == '\r' || c == '\x1A' || c == '﻿') { _p++; continue; }
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

            if (IsDigit(ch) || (ch == '.' && IsDigit(Nxt))) return Number();
            if (ch == '&' && (Nxt == 'H' || Nxt == 'h' || Nxt == 'O' || Nxt == 'o')) return RadixNumber();
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
                case '.': return Mk(T.Dot);
                case '#': return Mk(T.Hash);
                case '=':
                    if (Cur == '<') { _p++; return Mk(T.Le); }
                    if (Cur == '>') { _p++; return Mk(T.Ge); }
                    return Mk(T.Eq);
                case '<':
                    if (Cur == '>') { _p++; return Mk(T.Ne); }
                    if (Cur == '=') { _p++; return Mk(T.Le); }
                    return Mk(T.Lt);
                case '>':
                    if (Cur == '=') { _p++; return Mk(T.Ge); }
                    if (Cur == '<') { _p++; return Mk(T.Ne); }
                    return Mk(T.Gt);
                case '?': {
                    Token t = Mk(T.Keyword); t.Text = "PRINT"; t.Raw = "?"; return t;
                }
            }
            throw new UBasicError("unexpected character '" + ch + "'", _line);
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        private bool MatchWordAhead(string kw) {
            if (_p + kw.Length > _src.Length) return false;
            for (int i = 0; i < kw.Length; i++)
                if (char.ToUpperInvariant(_src[_p + i]) != kw[i]) return false;
            int after = _p + kw.Length;
            if (after < _src.Length) {
                char a = _src[after];
                if (char.IsLetterOrDigit(a) || a == '_' || a == '.' || a == '%' || a == '!' ||
                    a == '$' || a == '&' || a == '#')
                    return false;
            }
            // "rem" in the middle of a dotted name (a.rem) is not a comment.
            if (_p > 0 && _src[_p - 1] == '.') return false;
            return true;
        }

        private Token Number() {
            int start = _p;
            bool isFloat = false, isDouble = false;
            while (IsDigit(Cur)) _p++;
            if (Cur == '.') { isFloat = true; _p++; while (IsDigit(Cur)) _p++; }
            if (Cur == 'E' || Cur == 'e' || Cur == 'D' || Cur == 'd') {
                int save = _p;
                bool d = Cur == 'D' || Cur == 'd';
                _p++;
                if (Cur == '+' || Cur == '-') _p++;
                if (IsDigit(Cur)) {
                    isFloat = true; isDouble = d;
                    while (IsDigit(Cur)) _p++;
                } else _p = save;
            }
            string text = _src.Substring(start, _p - start);
            string parse = text.Replace('D', 'E').Replace('d', 'E');

            // An explicit sigil overrides the inferred literal type.
            bool forceInt = false, forceLong = false;
            if (Cur == '!') { _p++; isFloat = true; isDouble = false; }
            else if (Cur == '#') { _p++; isFloat = true; isDouble = true; }
            else if (Cur == '%') { _p++; forceInt = !isFloat; }
            else if (Cur == '&' && Nxt != 'H' && Nxt != 'h' && Nxt != 'O' && Nxt != 'o') {
                _p++; forceLong = !isFloat;
            }

            if (!isFloat) {
                int iv;
                if (int.TryParse(parse, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) {
                    Token t = Mk(T.IntLit); t.IVal = iv; t.Raw = text;
                    t.Sig = (forceLong || (!forceInt && (iv > 32767 || iv < -32768))) ? VType.Long : VType.Int;
                    return t;
                }
                isFloat = true; isDouble = true;      // too big for a LONG
            }
            Token f = Mk(T.SingleLit);
            f.FVal = double.Parse(parse, NumberStyles.Float, CultureInfo.InvariantCulture);
            f.Raw = text;
            // QBasic treats a literal with more than 7 digits as DOUBLE.
            if (!isDouble) {
                int digits = 0;
                for (int i = 0; i < text.Length; i++) {
                    if (text[i] == 'E' || text[i] == 'e') break;
                    if (IsDigit(text[i])) digits++;
                }
                if (digits > 7) isDouble = true;
            }
            f.Sig = isDouble ? VType.Double : VType.Single;
            return f;
        }

        private Token RadixNumber() {
            bool hex = Nxt == 'H' || Nxt == 'h';
            _p += 2;
            int start = _p;
            if (hex) while (Uri.IsHexDigit(Cur)) _p++;
            else while (Cur >= '0' && Cur <= '7') _p++;
            if (_p == start) throw new UBasicError("malformed &H / &O literal", _line);
            string digits = _src.Substring(start, _p - start);
            uint u;
            try { u = Convert.ToUInt32(digits, hex ? 16 : 8); }
            catch (Exception) { throw new UBasicError("&H / &O literal out of range", _line); }
            Token t = Mk(T.IntLit);
            bool forceLong = false;
            if (Cur == '&') { _p++; forceLong = true; }
            else if (Cur == '%') _p++;
            // As in QBasic, a literal that fits 16 bits is a signed INTEGER.
            if (!forceLong && u <= 0xFFFF) { t.IVal = unchecked((short)u); t.Sig = VType.Int; }
            else { t.IVal = unchecked((int)u); t.Sig = VType.Long; }
            t.Raw = "&" + (hex ? "H" : "O") + digits;
            return t;
        }

        private Token Str() {
            _p++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true) {
                // QBasic lets a string run to end of line without a closing quote.
                if (_p >= _src.Length || Cur == '\n') break;
                if (Cur == '\r' && Nxt == '\n') break;
                if (Cur == '"') {
                    if (Nxt == '"') { sb.Append('"'); _p += 2; continue; } // "" -> "
                    _p++;
                    break;
                }
                sb.Append(Cp437.FromUnicode(Cur));
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
            string up = raw.ToUpperInvariant();

            if (up == "DATA" && Cur != '$' && Cur != '%' && Cur != '!' && Cur != '#' && Cur != '&')
                return DataLine();

            VType sig = VType.Single;   // QBasic default when no sigil is given
            bool had = false;
            if (Cur == '%') { sig = VType.Int; had = true; _p++; }
            else if (Cur == '&') { sig = VType.Long; had = true; _p++; }
            else if (Cur == '!') { sig = VType.Single; had = true; _p++; }
            else if (Cur == '#') { sig = VType.Double; had = true; _p++; }
            else if (Cur == '$') { sig = VType.Str; had = true; _p++; }

            Token t = Mk(Keywords.Contains(up) && !had ? T.Keyword : T.Ident);
            t.Text = up;
            t.Raw = raw;
            t.Sig = sig;
            t.HadSigil = had;
            return t;
        }

        /// <summary>DATA is not tokenised: its items are raw text, split on
        /// commas, and may be unquoted strings. The statement ends at end of
        /// line or at a colon outside quotes.</summary>
        private Token DataLine() {
            StringBuilder sb = new StringBuilder();
            bool inQ = false;
            while (_p < _src.Length && Cur != '\n') {
                char c = Cur;
                if (c == '"') inQ = !inQ;
                else if (c == ':' && !inQ) break;
                if (c != '\r') sb.Append(Cp437.FromUnicode(c));
                _p++;
            }
            Token t = Mk(T.Data);
            t.Text = sb.ToString();
            t.Raw = "DATA";
            return t;
        }

        /// <summary>Split the raw text of a DATA statement into items.</summary>
        public static void SplitData(string raw, List<string> into) {
            int i = 0, n = raw.Length;
            while (true) {
                while (i < n && (raw[i] == ' ' || raw[i] == '\t')) i++;
                StringBuilder sb = new StringBuilder();
                if (i < n && raw[i] == '"') {
                    i++;
                    while (i < n && raw[i] != '"') sb.Append(raw[i++]);
                    if (i < n) i++;
                    while (i < n && raw[i] != ',') i++;
                    into.Add(sb.ToString());
                } else {
                    while (i < n && raw[i] != ',') sb.Append(raw[i++]);
                    into.Add(sb.ToString().TrimEnd());
                }
                if (i >= n) break;
                i++;   // the comma
            }
        }
    }
}
