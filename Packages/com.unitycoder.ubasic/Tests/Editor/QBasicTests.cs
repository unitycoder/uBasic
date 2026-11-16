using NUnit.Framework;

namespace Unitycoder.UBasic.Tests {

    /// <summary>Classic QBasic behaviour: the features NIBBLES.BAS and
    /// GORILLA.BAS depend on. Programs signal success by plotting a pixel in
    /// SCREEN 13, which is then read back.</summary>
    public class QBasicTests {

        private static VM Start(string src, out Screen scr) {
            Chunk chunk = Compiler.Compile(src, Dialect.QBasic);
            scr = new Screen(64, 32);
            return new VM(chunk, scr);
        }

        private static Screen Run(string src, string keys = null) {
            Screen scr;
            VM vm = Start(src, out scr);
            if (keys != null) foreach (char c in keys) vm.PushKey(c);
            for (int i = 0; i < 2000; i++) {
                vm.Time = i / 60.0;
                RunState st = vm.Run(100000);
                if (st == RunState.Halted) return scr;
                Assert.AreNotEqual(RunState.Faulted, st, "runtime error: " + vm.FaultMessage);
            }
            Assert.Fail("program did not finish");
            return scr;
        }

        /// <summary>Runs body, then expects the expression to be true.</summary>
        private static void Check(string body, string expr, string because, string keys = null) {
            Screen s = Run("SCREEN 13\n" + body + "\nIF " + expr + " THEN PSET (0, 0), 15\nEND\n", keys);
            Assert.AreEqual(15, s.Point(0, 0), because + "  [" + expr + "]");
        }

        [Test]
        public void DialectIsDetected() {
            Assert.AreEqual(Dialect.QBasic, Compiler.Detect("DEFINT A-Z\nPRINT 1\n"));
            Assert.AreEqual(Dialect.QBasic, Compiler.Detect("10 PRINT 1\n20 GOTO 10\n"));
            Assert.AreEqual(Dialect.UBasic, Compiler.Detect("DO\nk$ = INKEY$\nWAIT\nLOOP\n"), "a bare WAIT wins");
            Assert.AreEqual(Dialect.UBasic, Compiler.Detect("SCREEN 320, 200\nGOSUB a\nEND\na: RETURN\n"));
            Assert.AreEqual(Dialect.UBasic, Compiler.Detect("PRINT 1\n"));
        }

        [Test]
        public void QBasicStartsInTextModeAndSwitchesModes() {
            Screen s = Run("PRINT \"x\"\nEND\n");
            Assert.AreEqual(0, s.Mode); Assert.AreEqual(80, s.Cols); Assert.AreEqual(25, s.Rows);
            s = Run("SCREEN 9\nEND\n");
            Assert.AreEqual(640, s.W); Assert.AreEqual(350, s.H); Assert.AreEqual(25, s.Rows);
            s = Run("SCREEN 13\nPSET (5, 5), 200\nEND\n");
            Assert.AreEqual(200, s.Point(5, 5), "256 colours in SCREEN 13");
        }

        [Test]
        public void NumericTypes() {
            Check("DEFINT A-Z\nx = 2.5: y = 3.5", "x = 2 AND y = 4", "assignment to INTEGER rounds half to even");
            Check("a& = 100000: b& = a& * 3", "b& = 300000", "LONG holds 32 bits");
            Check("d# = 1 / 3", "d# * 3 = 1 AND d# > .3333333333", "DOUBLE precision");
            Check("a% = 5: a& = 6: a! = 7: a# = 8", "a% + a& + a! + a# = 26", "one variable per sigil");
            Check("DIM n AS LONG, s AS STRING\nn = 70000: s = \"q\"", "n = 70000 AND s = \"q\"", "DIM ... AS");
            Check("", "&HFF = 255 AND &H8000 = -32768 AND 1D2 = 100", "hex and D-exponent literals");
            Check("", "2 + 6 MOD 4 * 2 = 8 AND 7 \\ 2 * 2 = 1", "MOD and \\ bind looser than *");
        }

        [Test]
        public void GosubReturnAndOnGosub() {
            Check("n = 0\nGOSUB add\nGOSUB add\nON 2 GOSUB add, three\nGOTO done\n" +
                  "add: n = n + 1: RETURN\nthree: n = n + 3: RETURN\ndone:", "n = 5", "GOSUB / RETURN / ON GOSUB");
        }

        [Test]
        public void LineNumbers() {
            Check("10 n = 0\n20 n = n + 1\n30 IF n < 5 THEN 20\n40 IF n = 5 GOTO 60\n50 n = 99\n60 REM", "n = 5", "numbered lines");
        }

        [Test]
        public void TypesAndArraysOfTypes() {
            Check("TYPE Pt\n  x AS INTEGER\n  nm AS STRING * 4\nEND TYPE\n" +
                  "DIM a(1 TO 3) AS Pt, p AS Pt\na(2).x = 7: a(2).nm = \"hi\"\np = a(2)\na(2).x = 1",
                  "p.x = 7 AND p.nm = \"hi\" AND a(2).x = 1 AND LBOUND(a) = 1 AND UBOUND(a) = 3", "TYPE and record copy");
        }

        [Test]
        public void RedimAndDynamicBounds() {
            Check("n = 4\nDIM a&(n)\na&(4) = 1\nREDIM a&(-2 TO n * 2)\na&(-2) = 5: a&(8) = 6",
                  "a&(-2) + a&(8) + a&(4) = 11", "REDIM reallocates and clears");
            Screen scr;
            VM vm = Start("DIM a(3)\na(4) = 1\nEND\n", out scr);
            Assert.AreEqual(RunState.Faulted, vm.Run(1000), "out-of-range subscript is an error");
        }

        [Test]
        public void ParametersAreByReference() {
            const string subs =
                "SUB Twice (n)\n  n = n * 2\nEND SUB\n" +
                "SUB Fill (a(), v)\n  FOR i = LBOUND(a) TO UBOUND(a): a(i) = v: NEXT\nEND SUB\n" +
                "SUB Outer (m)\n  Twice m\nEND SUB\n" +
                "FUNCTION Peek2 (n)\n  Peek2 = n + shared1\nEND FUNCTION\n";
            Screen s = Run("DEFINT A-Z\nDIM SHARED shared1\nDIM a(1 TO 3)\nSCREEN 13\n" +
                "x = 5: Twice x\ny = 5: Twice (y)\nz = 3: Outer z\na(2) = 4: i = 2: Twice a(i)\n" +
                "CALL Fill(a(), 9)\nshared1 = 1: q = Peek2(shared1)\n" +
                "IF x = 10 AND y = 5 AND z = 6 AND a(1) = 9 AND a(3) = 9 AND q = 2 AND shared1 = 1 THEN PSET (0, 0), 15\n" +
                "END\n" + subs);
            Assert.AreEqual(15, s.Point(0, 0), "by-reference scalars, elements and arrays; (y) by value");
        }

        [Test]
        public void DataReadRestore() {
            Check("READ a, b$, c!\nRESTORE two\nREAD d\nDATA 1, hello there, 2.5\ntwo: DATA -42",
                  "a = 1 AND b$ = \"hello there\" AND c! = 2.5 AND d = -42", "DATA / READ / RESTORE label");
        }

        [Test]
        public void SelectCaseRangesAndIs() {
            Check("FOR i = 1 TO 10\nSELECT CASE i\nCASE 1, 2: n = n + 1\nCASE 3 TO 5: n = n + 10\n" +
                  "CASE IS >= 9: n = n + 100\nCASE ELSE: n = n + 1000\nEND SELECT\nNEXT\n" +
                  "SELECT CASE \"m\"\nCASE \"a\" TO \"z\": k = 1\nEND SELECT",
                  "n = 3232 AND k = 1", "CASE lists, ranges, IS and strings");
        }

        [Test]
        public void DefFnAndConstExpressions() {
            Check("CONST T = -1, F = NOT T, BIG = 2 * 16 + 1\nDEF FNd (x) = x * 2 + k\nk = 1",
                  "FNd(4) = 9 AND F = 0 AND BIG = 33", "DEF FN and constant folding");
        }

        [Test]
        public void InkeyReturnsBufferedKeys() {
            Screen scr;
            VM vm = Start("SCREEN 13\nDO: k$ = INKEY$: LOOP UNTIL k$ <> \"\"\n" +
                          "DO: e$ = INKEY$: LOOP UNTIL e$ <> \"\"\n" +
                          "IF k$ = \"a\" AND e$ = CHR$(0) + \"H\" THEN PSET (0, 0), 15\nEND\n", out scr);
            Assert.AreEqual(RunState.Waiting, vm.Run(100000), "an idle INKEY$ loop gives the frame back");
            vm.PushKey('a');
            vm.PushExtendedKey(72);
            RunState st = RunState.Waiting;
            for (int i = 0; i < 10 && st != RunState.Halted; i++) st = vm.Run(100000);
            Assert.AreEqual(RunState.Halted, st);
            Assert.AreEqual(15, scr.Point(0, 0), "plain and extended keys");
        }

        [Test]
        public void InputWaitsForEnter() {
            Screen scr;
            VM vm = Start("SCREEN 13\nINPUT \"n\"; a, b$\nLINE INPUT c$\n" +
                          "IF a = 12 AND b$ = \"xy\" AND c$ = \"p, q\" THEN PSET (0, 0), 15\nEND\n", out scr);
            Assert.AreEqual(RunState.Waiting, vm.Run(100000), "INPUT blocks with no keys");
            foreach (char c in "13\b2, xy\rp, q\r") vm.PushKey(c);
            RunState st = RunState.Waiting;
            for (int i = 0; i < 10 && st != RunState.Halted; i++) st = vm.Run(100000);
            Assert.AreEqual(15, scr.Point(0, 0), "fields, backspace and LINE INPUT");
        }

        [Test]
        public void GetPutRoundTripAndRealEgaData() {
            // 30x30 in EGA needs exactly 4 + 30 * 4 * 4 = 484 bytes = 121 LONGs,
            // which is what GORILLA.BAS dimensions.
            Screen s = Run("SCREEN 9\nDIM g&(120)\nLINE (10, 10)-(39, 39), 5, BF\nPSET (12, 11), 14\n" +
                           "GET (10, 10)-(39, 39), g&\nPUT (100, 100), g&, PSET\nPUT (200, 100), g&\nPUT (200, 100), g&\nEND\n");
            Assert.AreEqual(5, s.Point(100, 100)); Assert.AreEqual(14, s.Point(102, 101));
            Assert.AreEqual(5, s.Point(129, 129)); Assert.AreEqual(0, s.Point(130, 130));
            Assert.AreEqual(0, s.Point(205, 105), "XOR twice restores the background");

            // The left-facing banana from GORILLA.BAS, as shipped in its DATA.
            s = Run("SCREEN 9\nDIM b&(8)\nFOR i = 0 TO 8: READ b&(i): NEXT\nPUT (50, 50), b&, PSET\nEND\n" +
                    "DATA 458758,202116096,471604224,943208448,943208448,943208448,471604224,202116096,0\n");
            Assert.AreEqual(14, s.Point(54, 50), "yellow pixel from planar EGA data");
            Assert.AreEqual(0, s.Point(50, 50));
            Assert.AreEqual(14, s.Point(52, 53));
        }

        [Test]
        public void TextScreenBehaviour() {
            Screen s = Run("COLOR 14, 1\nCLS\nLOCATE 25, 80\nPRINT CHR$(219);\nLOCATE 1, 1\nPRINT USING \"#,###,#00\"; 5\nEND\n");
            Assert.AreEqual(14, s.Point(639, 399), "printing in the last cell does not scroll");
            Assert.AreEqual(1, s.Point(0, 399), "CLS fills with the background colour");
            s = Run("CLS\nLOCATE 3, 1: PRINT \"abc\"\nVIEW PRINT 2 TO 4\nCLS 2\nEND\n");
            for (int x = 0; x < 24; x++) for (int y = 32; y < 48; y++) Assert.AreEqual(0, s.Point(x, y), "CLS 2 clears the text viewport");
        }

        [Test]
        public void PaintStopsAtBorderAndCircleArcs() {
            Screen s = Run("SCREEN 12\nCIRCLE (100, 100), 30, 3\nPSET (100, 100), 9\nPAINT (95, 100), 3\nEND\n");
            Assert.AreEqual(3, s.Point(100, 100), "PAINT fills over other colours up to the border");
            Assert.AreEqual(0, s.Point(140, 100), "and does not leak out of the circle");
            s = Run("SCREEN 12\nCIRCLE (100, 100), 30, 3, 0, 3.14159\nEND\n");
            Assert.AreEqual(3, s.Point(100, 70), "upper half drawn");
            Assert.AreEqual(0, s.Point(100, 130), "lower half not drawn");
        }

        [Test]
        public void TimerCalibrationLoopsAreConsistent() {
            // NIBBLES.BAS times an empty loop, then uses that to build delays.
            Check("s# = TIMER\nFOR i# = 1 TO 1000: NEXT\ne# = TIMER - s#\n" +
                  "n& = .05 / e# * 1000\ns# = TIMER\nFOR i# = 1 TO n&: NEXT\nd# = TIMER - s#",
                  "e# > 0 AND d# > .04 AND d# < .06", "a calibrated busy loop lasts what it was calibrated for");
        }

        [Test]
        public void StringFunctions() {
            Check("", "STR$(5) = \" 5\" AND LTRIM$(STR$(-2.5)) = \"-2.5\" AND VAL(\" 12abc\") = 12 AND " +
                      "STRING$(3, 65) = \"AAA\" AND STRING$(2, \"xy\") = \"xx\" AND ASC(CHR$(219)) = 219 AND " +
                      "INSTR(3, \"abcabc\", \"b\") = 5 AND RTRIM$(\"a  \") = \"a\" AND HEX$(255) = \"FF\"", "string built-ins");
            Check("m$ = \"HELLO\": MID$(m$, 2, 3) = \"ipp\"", "m$ = \"HippO\"", "MID$ statement");
            Check("DIM s$(2)\nx$ = s$(1)", "x$ = \"\" AND LEN(u$) = 0", "unset strings are empty");
        }
    }
}
