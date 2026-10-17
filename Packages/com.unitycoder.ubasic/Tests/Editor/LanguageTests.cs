using NUnit.Framework;

namespace Unitycoder.UBasic.Tests {

    /// <summary>Compiler and VM behaviour, exercised without any engine code.
    /// Programs run against a small off-screen framebuffer and are read back
    /// through POINT, so assertions are on observable results.</summary>
    public class LanguageTests {

        /// <summary>Compile and run to completion; returns the screen.</summary>
        private static Screen Run(string src, int w = 64, int h = 32) {
            Chunk chunk = Compiler.Compile(src);
            Screen scr = new Screen(w, h);
            VM vm = new VM(chunk, scr);
            vm.Sound.HostBusy = false;
            for (int i = 0; i < 500; i++) {
                RunState st = vm.Run(100000);
                if (st == RunState.Halted) return scr;
                Assert.AreNotEqual(RunState.Faulted, st, "runtime error: " + vm.FaultMessage);
            }
            Assert.Fail("program did not finish");
            return scr;
        }

        /// <summary>Plot colour c at (0,0) only if the expression is true, so a
        /// language-level assertion becomes a pixel read.</summary>
        private static void AssertTrue(string expr, string because) {
            Screen s = Run("SCREEN 64, 32\nCLS 0\nIF " + expr + " THEN PSET (0, 0), 15\nEND\n");
            Assert.AreEqual(15, s.Point(0, 0), because + "  [" + expr + "]");
        }

        [Test]
        public void ArithmeticAndPrecedence() {
            AssertTrue("2 + 3 * 4 = 14", "multiplication binds tighter than addition");
            AssertTrue("3 ^ 4 = 81", "power operator");
            AssertTrue("17 MOD 5 = 2", "integer MOD");
            AssertTrue("7 \\ 2 = 3", "integer division truncates");
            AssertTrue("7 / 2 = 3.5", "slash is always floating point, as in QBasic");
        }

        [Test]
        public void FloatOnLeftOfIntegerOnlyOperator() {
            // Regression: MOD and \ convert both operands to INTEGER, but the
            // left operand sits *under* the right one on the stack. Converting
            // the wrong slot corrupted the stack and faulted with "MOD by zero".
            AssertTrue("(40.0 / 20) MOD 6 = 2", "float left operand of MOD");
            AssertTrue("(9.5 / 2) \\ 2 = 2", "float left operand of integer divide");
        }

        [Test]
        public void SigilsSelectDistinctTypes() {
            AssertTrue("CINT(2.6) = 3", "CINT rounds away from zero");
            AssertTrue("INT(2.6) = 2", "INT floors");
            AssertTrue("ABS(-3) = 3", "ABS");
        }

        [Test]
        public void StringsAndBuiltins() {
            AssertTrue("LEN(\"hello\") = 5", "LEN");
            AssertTrue("MID$(\"HELLO WORLD\", 7, 5) = \"WORLD\"", "MID$ is 1-based");
            AssertTrue("LEFT$(\"abcdef\", 3) = \"abc\"", "LEFT$");
            AssertTrue("\"ab\" + \"cd\" = \"abcd\"", "concatenation");
            AssertTrue("UCASE$(\"abC\") = \"ABC\"", "UCASE$");
            AssertTrue("INSTR(\"hello\", \"ll\") = 3", "INSTR is 1-based, 0 when absent");
        }

        [Test]
        public void BooleansAreIntegers() {
            AssertTrue("(1 = 1) = -1", "true is -1, as in QBasic");
            AssertTrue("(1 = 2) = 0", "false is 0");
            AssertTrue("NOT 0 = -1", "NOT is bitwise and doubles as logical");
        }

        [Test]
        public void SingleLineIfOwnsTheRestOfTheLine() {
            // Regression: `IF c THEN a : b` must run `b` only when c is true.
            // Treating `: b` as a separate statement made it unconditional.
            Screen s = Run(
                "SCREEN 64, 32\nCLS 0\nDIM n%\nn% = 0\n" +
                "IF 1 = 2 THEN n% = 1 : n% = 2\n" +
                "IF n% = 0 THEN PSET (0, 0), 15\nEND\n");
            Assert.AreEqual(15, s.Point(0, 0),
                "statements after the colon belong to the THEN clause");
        }

        [Test]
        public void FunctionsRecurseAndReturn() {
            Screen s = Run(
                "SCREEN 64, 32\nCLS 0\n" +
                "IF Fib(10) = 55 THEN PSET (0, 0), 15\nEND\n" +
                "FUNCTION Fib%(n%)\n" +
                "  IF n% < 2 THEN Fib% = n% : EXIT FUNCTION\n" +
                "  Fib% = Fib(n% - 1) + Fib(n% - 2)\n" +
                "END FUNCTION\n");
            Assert.AreEqual(15, s.Point(0, 0), "recursive FUNCTION with EXIT FUNCTION");
        }

        [Test]
        public void ForLoopCountsDownWithNegativeStep() {
            Screen s = Run(
                "SCREEN 64, 32\nCLS 0\nDIM i%, n%\nn% = 0\n" +
                "FOR i% = 10 TO 1 STEP -3\n  n% = n% + 1\nNEXT i%\n" +
                "IF n% = 4 THEN PSET (0, 0), 15\nEND\n");
            Assert.AreEqual(15, s.Point(0, 0), "10,7,4,1 is four iterations");
        }

        [Test]
        public void ArraysAreTwoDimensionalAndInclusive() {
            Screen s = Run(
                "SCREEN 64, 32\nCLS 0\nDIM g%(4, 4)\nDIM x%, y%\n" +
                "FOR y% = 0 TO 4\n  FOR x% = 0 TO 4\n    g%(x%, y%) = x% * y%\n  NEXT x%\nNEXT y%\n" +
                "IF g%(3, 4) = 12 THEN PSET (0, 0), 15\nEND\n");
            Assert.AreEqual(15, s.Point(0, 0), "DIM g(4,4) covers indices 0..4");
        }

        [Test]
        public void DrawingClipsInsteadOfFaulting() {
            Screen s = Run("SCREEN 64, 32\nCLS 0\nPSET (9999, 9999), 15\nPSET (1, 1), 12\nEND\n");
            Assert.AreEqual(12, s.Point(1, 1), "in-bounds plot still works");
            Assert.AreEqual(-1, s.Point(9999, 9999), "off-screen reads report -1");
        }

        [Test]
        public void WaitSuspendsAndResumesInPlace() {
            Chunk chunk = Compiler.Compile(
                "SCREEN 64, 32\nCLS 0\nDIM i%\nFOR i% = 1 TO 3\n  WAIT\nNEXT i%\nEND\n");
            Screen scr = new Screen(64, 32);
            VM vm = new VM(chunk, scr);
            int waits = 0;
            for (int f = 0; f < 50; f++) {
                RunState st = vm.Run(100000);
                if (st == RunState.Waiting) { waits++; continue; }
                if (st == RunState.Halted) break;
                Assert.Fail("unexpected state " + st + " " + vm.FaultMessage);
            }
            Assert.AreEqual(3, waits, "each WAIT should suspend exactly once");
        }

        [Test]
        public void StringHeapStaysBoundedUnderChurn() {
            Chunk chunk = Compiler.Compile(
                "DIM i%, s$\nFOR i% = 1 TO 5000\n  s$ = \"x\" + STR$(i%)\nNEXT i%\nEND\n");
            Screen scr = new Screen(64, 32);
            VM vm = new VM(chunk, scr);
            vm.GcThreshold = 64;
            for (int f = 0; f < 200; f++) if (vm.Run(100000) == RunState.Halted) break;
            Assert.Less(vm.Strings.Count, 500,
                "the mark-sweep should reclaim dead strings rather than leaking 10000");
        }

        [Test]
        public void CompileErrorsCarryLineNumbers() {
            UBasicError e = Assert.Throws<UBasicError>(() =>
                Compiler.Compile("DIM a%\n\nPRINT \"x\" + 1\nEND\n"));
            Assert.AreEqual(3, e.Line, "the error should point at the offending line");
        }

        [Test]
        public void UnknownFunctionIsAnErrorNotASilentVariable() {
            Assert.Throws<UBasicError>(() => Compiler.Compile("PRINT NOSUCH(1)\nEND\n"),
                "a subscripted unknown name is a typo, not an implicit variable");
        }
    }
}
