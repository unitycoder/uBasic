# uBasic

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

### Sound

```basic
BEEP                       ' short 800 Hz blip
SOUND freq, ticks          ' ticks: one second is about 18.2
PLAY "T120 O4 L8 CDEFG"    ' MML song string
```

`SOUND` blocks until the note finishes, as in QBasic — including the classic
idiom where an inaudible tone is used as a timer:

```basic
SOUND 32000, 18.2          ' one second pause, no sound
```

That works, but it needs deliberate handling rather than falling out for free.
On real hardware 32 kHz was simply above hearing; sampled at 48 kHz it would
alias down to a piercing 16 kHz screech, so anything above 20 kHz is rendered as
silence on purpose. Frequencies below QBasic's 37 Hz floor are clamped rather
than raising an error, matching how `PSET` clips instead of faulting.

**PLAY (MML)**

| | |
|---|---|
| `A`–`G` | note, with optional `#`/`+` sharp or `-` flat |
| `N n` | note by number, 0–84 (0 is a rest) |
| `O n` | octave 0–6 (A in octave 4 is 440 Hz) |
| `>` `<` | octave up / down |
| `L n` | default note length (1, 2, 4, 8, 16, 32, 64) |
| `T n` | tempo in quarter notes per minute, 32–255 |
| `P n` / `R n` | rest of length n |
| `.` | dotted — extends the note by half, stackable |
| `ML` `MN` `MS` | legato / normal / staccato articulation |
| `MB` `MF` | background (returns at once) / foreground (blocks) |

A length may follow any note directly: `C4` is a quarter-note C, `A8.` a dotted
eighth. Octave, tempo, length and articulation persist across `PLAY` calls, as in
QBasic. Unrecognised characters are skipped rather than faulting.

`MB` is what you want for game music — it queues the notes and returns
immediately so the program keeps drawing:

```basic
PLAY "MB T120 O4 L8 MS EEG>C<GE"
DO
  ' ... music plays underneath
  WAIT
LOOP
```

**Timing.** Within one `PLAY` call, notes are sample-accurate — the only gaps
are the articulation rests. Between separate *blocking* statements the program
resumes on a frame boundary, so expect up to one frame of slack there. Put a
whole phrase in one `PLAY` string rather than one note per statement.

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

**Audio without a filter callback.** There is no `OnAudioFilterRead` anywhere,
which is what makes sound work on WebGL — Unity does not support the filter
callback there. Instead each cached `AudioClip` holds a whole number of cycles of
a square wave and is played **looping**, so a clip encodes a *frequency* and
nothing else. Duration is decided by when playback stops. One clip therefore
serves every note length, and the entire chromatic range QBasic can address is
about 84 clips of roughly 8 KB — generated up front by `PrewarmNotes` so no note
ever costs an allocation mid-game.

Using several cycles per clip rather than one is what keeps tuning honest.
Rounding a single cycle to whole samples puts A4 about 4 cents sharp; a 4096-
sample chunk holds every note in the scale inside a quarter of a cent.

Notes are placed on `AudioSettings.dspTime` across a small pool of
`AudioSource`s, so note timing is independent of frame rate. Set
`ScheduledAudio` false to fall back to plain `Play`/`Stop` if a platform
mishandles `PlayScheduled`.

All of that timing lives in `SoundScheduler`, which is engine-free and takes its
clock as a parameter — `UnityAudioAdapter` only creates clips and calls
`AudioSource`. That split exists because it was learned the hard way: the
scheduling logic originally sat inside the Unity adapter where it could not be
run headlessly, and a bug that reported "busy" forever while idle survived a
full test pass and blocked the program permanently on its first `SOUND`. Both
the adapter and the test harness now drive the same class, and
`--selftest` covers the idle case directly.

If a program ever does stall at a `SOUND` or `PLAY`, the runner logs a warning
after ten seconds rather than hanging silently.

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
- **Sound is monophonic**, one square-wave voice, as on a PC speaker. The
  blocking behaviour of `SOUND` and the one-note-at-a-time rule are the same
  design decision; breaking one breaks the other.
- **No `PLAY` `VARPTR$` substring execution.** It would drag a pointer concept
  into a language that deliberately has none.
- **WebGL needs a user gesture** before any audio plays — a browser rule, not a
  uBasic one. Have the player click something before the first `PLAY`.

## Headless testing

`Tools/TestHost.cs` compiles and runs a program outside Unity and writes the
framebuffer to a PNG — the whole language is testable in CI without the editor.

```bash
mcs -out:ubasic.exe Runtime/*.cs Tools/TestHost.cs
mono ubasic.exe Samples/smoke.ubas out.png          # static, runs to END
mono ubasic.exe Samples/balls.ubas out.png 600      # 600 frames of animation
mono ubasic.exe Samples/sound.ubas out.png 1200     # also writes out.wav
mono ubasic.exe --selftest                          # scheduler regression tests
```

Programs that make sound also emit a 16-bit WAV alongside the PNG. The harness
stands in for the Unity audio adapter, laying queued notes on a virtual timeline
and reporting busy while it runs ahead of the frame clock — so the blocking path
and the MML parser are exercised for real, and note frequencies are verifiable
in CI without the editor.

`Tools/font_gen.py` regenerates `Runtime/Font6x8.cs` from readable ASCII art and
emits a preview sheet — edit the glyph art there, not the hex.

## Samples

| File | Shows |
|------|-------|
| `smoke.ubas` | Every primitive, text, arrays, `SELECT CASE`, a `FUNCTION`, `GOTO` |
| `balls.ubas` | `WAIT`-driven animation, arrays of floats, string building under GC |
| `batball.ubas` | `KEY` input, block `IF`, `EXIT DO`, a full little game loop |
| `sound.ubas` | `BEEP`, `SOUND` sweep, the silent-delay idiom, MML scale, `MB` music under animation |
