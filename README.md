<img src="https://img.shields.io/badge/VibeCoded-100%25-green" alt="AI Generated Content"/>

# uBasic

<img width="678" height="423" alt="image" src="https://github.com/user-attachments/assets/3fc89591-50cb-44f6-a36e-be08bc6d67bb" />

A QBasic-shaped scripting language for Unity, with a palette-indexed texture as
its screen. Lexer, bytecode compiler and VM in ~1700 lines of engine-free C#,
plus a ~280-line Unity layer.

Nothing here uses `Reflection.Emit`, dynamic assembly loading, or runtime
reflection, so it works unchanged under IL2CPP — iOS, consoles and WebGL
included — and there is nothing for the managed linker to strip out from under
you.

## Setup

1. Copy `Runtime/` and `Unity/` into your project (`Unity/Editor/` must live
   under a folder named `Editor`).
2. Add a `UBasicRunner` component to an empty GameObject.
3. Press play. The default program in the inspector draws a bouncing circle.
4. Drop a `.ubas` file into the project and assign it to `SourceFile` — the
   importer compile-checks it on every save and reports errors with line
   numbers, so you catch mistakes without entering play mode.

`Restart()` recompiles and resets at runtime. That is the hot-reload hook: swap
`Source` or the `TextAsset` and call it.

## Language

Case-insensitive. `'` and `REM` start a comment, `_` at end of line continues it,
`:` separates statements.

### Types

Three, decided at compile time by the sigil, exactly as in QBasic:

| Sigil | Type      | Example        |
|-------|-----------|----------------|
| `%`   | INTEGER   | `count%`       |
| `!`   | SINGLE    | `speed!`       |
| `$`   | STRING    | `name$`        |
| none  | SINGLE    | `x` = `x!`     |

`DIM x AS INTEGER` works too. Booleans are integers: true is `-1`, false is `0`,
and `AND` / `OR` / `NOT` / `XOR` are bitwise, so they double as both.

### Declarations

```basic
DIM i%, speed!, name$
DIM total AS SINGLE
DIM SHARED score%              ' visible inside SUBs and FUNCTIONs
DIM grid%(15, 9)               ' 0..15 by 0..9, bounds must be constant
CONST MAXW% = 320, TITLE$ = "uBasic"
```

Undeclared variables spring into existence with a zero value, as in QBasic.
Inside a procedure a bare name is **local** unless it was declared `DIM SHARED`.

Predefined: `TRUE%`, `FALSE%`, `PI!`, `KEY_LEFT%`, `KEY_RIGHT%`, `KEY_UP%`,
`KEY_DOWN%`, `KEY_ESC%`, `KEY_SPACE%`, `KEY_ENTER%`.

### Control flow

```basic
IF a > b THEN PRINT "bigger" : x% = 1 ELSE x% = 2

IF a > b THEN
  ...
ELSEIF a = b THEN
  ...
ELSE
  ...
END IF

FOR i% = 0 TO 10 STEP 2 ... NEXT i%
WHILE cond ... WEND
DO ... LOOP
DO WHILE cond ... LOOP
DO ... LOOP UNTIL cond

SELECT CASE n% MOD 3
  CASE 0        ...
  CASE 1, 2     ...
  CASE ELSE     ...
END SELECT

EXIT FOR | EXIT DO | EXIT WHILE | EXIT SUB | EXIT FUNCTION
GOTO label        ' label: on its own line, within the same procedure
```

### Procedures

```basic
SUB DrawBox(x%, y%, c%)
  LINE (x%, y%)-(x% + 20, y% + 12), c%, B
END SUB

FUNCTION Dist!(ax!, ay!, bx!, by!)
  Dist! = SQR((ax! - bx!) ^ 2 + (ay! - by!) ^ 2)
END FUNCTION

DrawBox 10, 10, 14          ' bare call
CALL DrawBox(10, 10, 14)    ' or with CALL
d! = Dist(0, 0, 3, 4)       ' sigil optional at the call site when unambiguous
```

Assigning to the function's own name sets the return value. Arguments are passed
by value. Recursion works; runaway recursion faults at 512 frames rather than
taking the editor down.

### Graphics

The screen is a palette-indexed framebuffer. Colours are `0`–`15` (the standard
16-colour palette); `PALETTE` redefines any entry.

```basic
SCREEN 320, 200                    ' resize the framebuffer
CLS                                ' clear to current background
CLS 1                              ' clear to colour 1
PSET (x, y), c
LINE (x1, y1)-(x2, y2), c          ' line
LINE (x1, y1)-(x2, y2), c, B       ' box outline
LINE (x1, y1)-(x2, y2), c, BF      ' filled box
CIRCLE (x, y), r, c                ' outline
CIRCLE (x, y), r, c, F             ' filled (uBasic extension)
PAINT (x, y), c                    ' flood fill
PALETTE i, r, g, b                 ' 0..255 components
```

Everything clips silently — plotting off-screen is a no-op, never an error.

### Text

Text is blitted from a built-in 6×8 bitmap font, so a character cell is 6×8
pixels and a 320×200 screen is 53×25 characters. There is no font asset and no
`TextMeshPro` dependency.

```basic
COLOR 14          ' foreground only
COLOR 14, 1       ' foreground, background
LOCATE row, col   ' 0-based
PRINT "score"; s%          ' ";" = no gap
PRINT a$, b$               ' "," = next 14-column zone
PRINT "no newline";        ' trailing ";" suppresses the newline
```

Numbers print QBasic-style, with a leading space standing in for the sign.
Printing past the bottom row scrolls.

### Builtins

**Math** `ABS SGN INT CINT CSNG SQR SIN COS TAN ATN ATN2 EXP LOG MIN MAX RND`
(`RND` alone gives 0–1, `RND(n)` gives an integer 0…n-1)

**Strings** `LEN LEFT$ RIGHT$ MID$ INSTR UCASE$ LCASE$ TRIM$ SPACE$ STRING$
CHR$ ASC STR$ VAL` (`MID$` is 1-based, like QBasic)

**Screen** `POINT(x, y)` `SCRW` `SCRH` `TIMER`

**Input** `KEY(code)` held, `KEYHIT(code)` pressed this frame, `MOUSEX` `MOUSEY`
`MOUSEB(n)`. A key code is the ASCII code — `KEY(ASC("A"))` — plus `KEY_LEFT%`
and friends for the arrows.

**Other** `RANDOMIZE seed`, `WAIT`, `END`

### WAIT

`WAIT` suspends the program and resumes it at the same instruction next frame.
A whole game is one loop:

```basic
DO
  CLS 0
  ' update and draw
  WAIT
LOOP
```

This is the point of the bytecode design. VM state is `(pc, operand stack,
locals arena, frame stack)` and nothing more, so suspending is just returning
from `Run` — no continuations, no coroutine plumbing, no C# iterators threaded
through the interpreter. The same property means a program is a flat struct you
could serialise into a save file, or snapshot for deterministic rollback.

A program that loops without ever reaching `WAIT` burns its instruction budget
and gets a console warning instead of hanging the editor.

## Design notes

**Pipeline.** Source → tokens → bytecode, in two passes with no AST. Pass one
scans for `SUB`/`FUNCTION` headers so calls resolve before their bodies are
seen; pass two is a single recursive descent that emits instructions directly.

**Untyped slots.** Because every variable's type is fixed by its sigil, the
compiler knows the type of every stack cell. Runtime values are an 8-byte
untyped union, never a tagged variant:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct Slot {
    [FieldOffset(0)] public int   I;
    [FieldOffset(0)] public float F;
    [FieldOffset(0)] public int   S;   // handle into StringHeap
}
```

No boxing, no type checks in the dispatch loop, no per-operation allocation.
`AddI` and `AddF` are separate opcodes the compiler picks. Stacks, locals and
arrays are preallocated `Slot[]`.

**Strings.** The one thing that does allocate. `StringHeap` is a free-listed
`string[]` with a precise mark-sweep. Since uBasic has no closures and no
reference containers, every live string is reachable from exactly three places —
the pinned constant pool, globals typed `Str`, and active frames' locals typed
`Str`. Collection only runs at a statement boundary with an empty operand stack,
so the root set is exact: no conservative scanning, no write barriers. In the
bouncing-balls sample, 600 frames of string building stays bounded at a few
hundred live strings instead of leaking ~3000.

**Host binding.** `Host.cs` is a flat table of fixed-arity signatures with
declared parameter types. The compiler resolves the overload and splices in any
conversions, so a call is an array index and a delegate invoke.

## Limitations

Deliberate, and worth keeping that way:

- **No closures, no dynamic containers.** This is what buys the flat state and
  the exact GC. Event wiring and anything you'd write with a lambda is worse.
  Use a named `SUB` instead.
- **Arrays are global and fixed-size.** Bounds must be compile-time constants;
  1-D and 2-D only. No `REDIM`, no local arrays.
- **No `TYPE` records** yet. This is the first thing I'd add, and it fits the
  slot model cleanly (a record is a contiguous run of slots).
- **No `GOSUB`, no line numbers.** Labelled `GOTO` within a procedure only.
- **Arguments are by value.** No QBasic `BYREF` semantics.
- **One program per runner.** No modules or `#include`.

## Headless testing

`Tools/TestHost.cs` compiles and runs a program outside Unity and writes the
framebuffer to a PNG — the whole language is testable in CI without the editor.

```bash
mcs -out:ubasic.exe Runtime/*.cs Tools/TestHost.cs
mono ubasic.exe Samples/smoke.ubas out.png          # static, runs to END
mono ubasic.exe Samples/balls.ubas out.png 600      # 600 frames of animation
```

`Tools/font_gen.py` regenerates `Runtime/Font6x8.cs` from readable ASCII art and
emits a preview sheet — edit the glyph art there, not the hex.

## Samples

| File | Shows |
|------|-------|
| `smoke.ubas` | Every primitive, text, arrays, `SELECT CASE`, a `FUNCTION`, `GOTO` |
| `balls.ubas` | `WAIT`-driven animation, arrays of floats, string building under GC |
| `batball.ubas` | `KEY` input, block `IF`, `EXIT DO`, a full little game loop |
