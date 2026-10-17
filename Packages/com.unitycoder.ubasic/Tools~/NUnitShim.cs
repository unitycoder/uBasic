// Tiny stand-in for the slice of NUnit that Tests/Editor uses.
//
// It exists so the package's real Unity test files can also be compiled and
// run by Mono in CI, with no Unity installation. It is NOT part of the package
// (Tools~ is hidden from Unity); inside the editor the genuine NUnit that ships
// with the Unity Test Framework is used instead.
//
// Only the members the tests actually call are implemented -- if a new test
// reaches for something else, this will fail to compile, which is the point.

using System;

namespace NUnit.Framework {

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public delegate void TestDelegate();

    public class AssertionException : Exception {
        public AssertionException(string message) : base(message) { }
    }

    /// <summary>Supports the one constraint form the tests use:
    /// Is.EqualTo(x).Within(tolerance).</summary>
    public class Constraint {
        public double Expected;
        public double Tolerance;
        public Constraint Within(double t) { Tolerance = t; return this; }
        public bool Matches(double actual) { return Math.Abs(actual - Expected) <= Tolerance; }
        public override string ToString() {
            return Tolerance > 0
                ? Expected + " +/- " + Tolerance
                : Expected.ToString();
        }
    }

    public static class Is {
        public static Constraint EqualTo(double v) {
            Constraint c = new Constraint();
            c.Expected = v;
            return c;
        }
    }

    public static class Assert {

        private static string With(string message, string detail) {
            return string.IsNullOrEmpty(message) ? detail : detail + "  --  " + message;
        }

        public static void AreEqual(object expected, object actual, string message = null) {
            if (Equals(expected, actual)) return;
            throw new AssertionException(With(message,
                "expected <" + Fmt(expected) + "> but was <" + Fmt(actual) + ">"));
        }

        public static void AreNotEqual(object notExpected, object actual, string message = null) {
            if (!Equals(notExpected, actual)) return;
            throw new AssertionException(With(message, "did not expect <" + Fmt(actual) + ">"));
        }

        public static void IsTrue(bool condition, string message = null) {
            if (condition) return;
            throw new AssertionException(With(message, "expected true"));
        }

        public static void IsFalse(bool condition, string message = null) {
            if (!condition) return;
            throw new AssertionException(With(message, "expected false"));
        }

        public static void Greater(double actual, double floor, string message = null) {
            if (actual > floor) return;
            throw new AssertionException(With(message,
                "expected greater than " + floor + " but was " + actual));
        }

        public static void Less(double actual, double ceiling, string message = null) {
            if (actual < ceiling) return;
            throw new AssertionException(With(message,
                "expected less than " + ceiling + " but was " + actual));
        }

        public static void That(double actual, Constraint c, string message = null) {
            if (c.Matches(actual)) return;
            throw new AssertionException(With(message,
                "expected " + c + " but was " + actual));
        }

        public static T Throws<T>(TestDelegate code, string message = null) where T : Exception {
            try {
                code();
            } catch (T hit) {
                return hit;
            } catch (Exception other) {
                throw new AssertionException(With(message,
                    "expected " + typeof(T).Name + " but got " + other.GetType().Name +
                    ": " + other.Message));
            }
            throw new AssertionException(With(message,
                "expected " + typeof(T).Name + " but nothing was thrown"));
        }

        public static void Fail(string message) {
            throw new AssertionException(message ?? "failed");
        }

        private static string Fmt(object o) { return o == null ? "null" : o.ToString(); }
    }
}
