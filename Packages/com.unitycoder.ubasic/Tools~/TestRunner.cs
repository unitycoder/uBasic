// Headless runner for the package's Unity test files.
//
// Finds every [Test] method in the compiled assembly, runs it, and reports.
// Combined with NUnitShim.cs this lets CI execute Tests/Editor/*.cs under Mono
// with no Unity installation -- the same files the Unity Test Runner executes
// inside the editor.
//
// Build: mcs -out:tests.exe Runtime/*.cs 'Tools~/NUnitShim.cs' \
//            'Tools~/TestRunner.cs' Tests/Editor/*.cs

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

public static class PackageTestRunner {

    public static int Main(string[] argv) {
        int passed = 0;
        List<string> failures = new List<string>();

        Type[] types = Assembly.GetExecutingAssembly().GetTypes();
        Array.Sort(types, (a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        foreach (Type t in types) {
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(methods, (a, b) => string.CompareOrdinal(a.Name, b.Name));

            object instance = null;
            foreach (MethodInfo m in methods) {
                if (m.GetCustomAttributes(typeof(TestAttribute), false).Length == 0) continue;
                if (m.GetParameters().Length != 0) continue;

                if (instance == null) {
                    try { instance = Activator.CreateInstance(t); }
                    catch (Exception e) {
                        failures.Add(t.Name + ": cannot construct -- " + e.Message);
                        break;
                    }
                }

                string label = t.Name + "." + m.Name;
                try {
                    m.Invoke(instance, null);
                    passed++;
                    Console.WriteLine("  ok   " + label);
                } catch (TargetInvocationException tie) {
                    Exception inner = tie.InnerException ?? tie;
                    failures.Add(label + "\n         " + inner.Message);
                    Console.WriteLine("  FAIL " + label);
                    Console.WriteLine("       " + inner.Message);
                }
            }
        }

        Console.WriteLine();
        if (failures.Count == 0) {
            Console.WriteLine(passed + " test(s) passed");
            return 0;
        }
        Console.WriteLine(passed + " passed, " + failures.Count + " FAILED:");
        foreach (string f in failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
