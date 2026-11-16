# uBasic

A QBasic-shaped scripting language for Unity, with a palette-indexed texture as
its screen. Lexer, bytecode compiler and VM in ~5,700 lines of engine-free C#,
plus a ~640-line Unity layer.

It runs its own small dialect built around `WAIT` and a per-frame game loop,
and it runs classic QBasic programs as they are: Microsoft's NIBBLES.BAS and
GORILLA.BAS load and play unmodified (bring your own copies).

Nothing here uses `Reflection.Emit`, dynamic assembly loading, or runtime
reflection, so it works unchanged under IL2CPP — iOS, consoles and WebGL
included — and there is nothing for the managed linker to strip out from under
you. The audio path deliberately avoids `OnAudioFilterRead`, which WebGL does
not support, so sound works there too.

## Install

Package Manager → **+** → *Install package from git URL…*

```
https://github.com/unitycoder/uBasic.git?path=/Packages/com.unitycoder.ubasic
```

Or add it to `Packages/manifest.json` directly:

```json
{
  "dependencies": {
    "com.unitycoder.ubasic": "https://github.com/unitycoder/uBasic.git?path=/Packages/com.unitycoder.ubasic"
  }
}
```

Pin a release by appending a tag: `…/ubasic.git#v0.2.0`.

Then: add a **uBasic Runner** component to an empty GameObject and press play.
The default program in the inspector draws a bouncing circle.

Package Manager → uBasic → **Samples** → *Demo Programs* imports four `.ubas`
programs to start from.

To run a classic program, drop the `.BAS` file into the project, assign it to
`SourceFile` and press play. See [Classic QBasic programs](#classic-qbasic-programs).

### DOCS

https://github.com/unitycoder/uBasic/wiki/uBasic-API-Reference

### Live PlayGround

https://unitycoder-com.itch.io/ubasic-webgl-playground

### Example Scripts

https://github.com/unitycoder/uBasic/wiki/Example-Scripts

## Syntax Highlighting for Visual Studio and VSCode

https://github.com/unitycoder/uBasicSyntaxHighlight

## Tools

- Draw images for uBasic https://tools.unitycoder.com/uBasic-Paint/
- Convert 3D models for uBasic https://tools.unitycoder.com/uBasic3DModelImporter/
- SoundFX Generator for uBasic https://tools.unitycoder.com/uBasicBeepForge/
- Music Tracker for uBasic https://tools.unitycoder.com/uBasicMusicTracker/

## Package layout

| Path | Assembly | What it is |
|------|----------|------------|
| `Runtime/` | `Unitycoder.UBasic` | The language. **`noEngineReferences`** — a `using UnityEngine` here is a compile error. |
| `Runtime/Unity/` | `Unitycoder.UBasic.Unity` | `MonoBehaviour`, texture upload, input, audio. |
| `Editor/` | `Unitycoder.UBasic.Editor` | `.ubas` / `.bas` importer, custom inspector. |
| `Tests/Editor/` | `Unitycoder.UBasic.Tests` | 39 tests, editor-only. |
| `Samples~/Demos/` | — | Example programs, imported on demand. |
| `Tools~/` | — | Headless harness, font generator. Hidden from Unity. |

That split is load-bearing, not cosmetic. An earlier bug came from timing logic
living in the Unity-only file where it could not be run headlessly; it passed a
full test run and still blocked every program on its first `SOUND`. The asmdef
now makes that drift impossible to compile.

## Writing programs

Drop a `.ubas` or `.bas` file anywhere in your project and assign it to
`SourceFile`. The importer compile-checks it on every save and reports errors
with line numbers on the asset's inspector, so mistakes surface without
entering play mode. A `.BAS` saved by DOS QBasic is decoded from code page 437,
so its block and box-drawing characters survive.

`Restart()` recompiles and resets at runtime — that is the hot-reload hook: swap
`Source` or the `TextAsset` and call it.

## Classic QBasic programs

`UBasicRunner.Dialect` decides how a source file is read. On `Auto`:

- a bare `WAIT` or a `SCREEN w, h` anywhere → **uBasic**, everything below in
  this README applies as written
- otherwise, anything only classic programs use (`DECLARE`, `DEFINT`, `GOSUB`,
  `DATA`, `INKEY$`, `INPUT`, `TYPE`, line numbers…) → **QBasic**
- otherwise → uBasic

The two differ only where uBasic had already made its own choice:

| | uBasic | QBasic |
|---|---|---|
| start-up screen | `ScreenWidth` × `ScreenHeight`, 6×8 font | `SCREEN 0`, 80×25 text |
| `LOCATE` | 0-based | 1-based |
| `CLS n` | clear to colour n | 0/1 whole screen, 2 text viewport |
| `RND(n)` | whole number 0…n-1 | QBasic's (n is a mode, not a range) |
| `PAINT (x, y), c` | flood the colour under the point | fill out to the border colour |
| `Name(a, b)` without `CALL` | a call | `Name (a)` passes `a` by value |

Everything else is shared, so a uBasic program can use `GOSUB`, `TYPE`,
`INKEY$`, `DATA` and the rest.

What a classic program gets:

- **Screen modes** `SCREEN 0, 1, 2, 7, 8, 9, 11, 12, 13` with their real
  resolutions, text cells, colour depths and palettes; `WIDTH`, `VIEW PRINT`,
  `COLOR` and `PALETTE` with each mode's own meaning. `QBasicAspect` on the
  runner shows them at 4:3, which is what makes `CIRCLE` round in 640×350.
- **Graphics** `PSET`/`PRESET`, `LINE` with `B`/`BF` and `STEP`, `CIRCLE` with
  arcs and aspect, border-fill `PAINT`, `POINT`, and `GET`/`PUT` in QBasic's
  actual image layouts — sprites shipped as `DATA` draw correctly.
- **Text** the full CP437 character set, `PRINT USING`, `TAB`, `SPC`, `CSRLIN`,
  `POS`.
- **Keyboard** `INKEY$` (extended keys as `CHR$(0)` + scan code), `INPUT`,
  `LINE INPUT`. From a web page around a WebGL build, `UBasicRunner.SendKey`
  feeds the same buffer.
- **Language** all five types and `DEFINT` & co., `TYPE` records, dynamic
  arrays with `REDIM`, by-reference parameters, `GOSUB`/`RETURN`, `ON … GOTO`,
  line numbers, `DATA`/`READ`/`RESTORE`, `DEF FN`, `SELECT CASE` ranges, `SWAP`.
- **DOS leftovers** `DEF SEG`, `POKE`, `OUT`, `ON ERROR`, `KEY OFF` are accepted
  and ignored; `PEEK` returns 0.

**Timing.** Classic programs never yield; they busy-wait. The VM runs
`InstructionBudget` instructions a frame — for these programs that number *is*
the speed of the machine — and `TIMER` advances with the instructions executed.
A program that calibrates a delay loop against `TIMER`, as NIBBLES does, gets
delays of the length it asked for. A loop that only polls `TIMER`, like
GORILLA's `Rest`, is throttled to about 1000 polls a second
(`VM.TimerPollsPerFrame`), and an `INKEY$` loop that finds no key gives the
rest of the frame back (`VM.YieldOnIdleInkey`).

The full supported / unsupported list is in
[QBASIC.md](Packages/com.unitycoder.ubasic/QBASIC.md).

## Language

Case-insensitive. `'` and `REM` start a comment, `_` at end of line continues it,
`:` separates statements.

### Types

Five, decided at compile time by the sigil, exactly as in QBasic:

| Sigil | Type      | Example        |
|-------|-----------|----------------|
| `%`   | INTEGER   | `count%`       |
| `&`   | LONG      | `total&`       |
| `!`   | SINGLE    | `speed!`       |
| `#`   | DOUBLE    | `angle#`       |
| `$`   | STRING    | `name$`        |
| none  | SINGLE    | `x` = `x!`     |

`DIM x AS INTEGER` works too, and `DEFINT A-Z` (and `DEFLNG`, `DEFSNG`,
`DEFDBL`, `DEFSTR`) changes what "none" means. `a%` and `a&` are different
variables. Booleans are integers: true is `-1`, false is `0`, and `AND` / `OR` /
`NOT` / `XOR` / `EQV` / `IMP` are bitwise, so they double as both.

INTEGER and LONG are both 32-bit, and SINGLE and DOUBLE both compute in 64-bit;
the names decide variable identity, result types and how `PRINT` formats.
Assigning a float to an integer rounds to nearest, as QBasic does.

```basic
TYPE Player
  x AS INTEGER
  nm AS STRING * 8
END TYPE
DIM p AS Player, team(1 TO 4) AS Player
team(2).x = 7 : p = team(2)        ' whole-record copy
```

### Declarations

```basic
DIM i%, speed!, name$
DIM total AS SINGLE
DIM SHARED score%              ' visible inside SUBs and FUNCTIONs
DIM grid%(15, 9)               ' 0..15 by 0..9
DIM row(1 TO n * 2) AS LONG    ' any bounds, any expression
REDIM row(1 TO 500)            ' reallocate (and clear)
CONST MAXW% = 320, TITLE$ = "uBasic", HALF = MAXW% \ 2
```

Arrays are allocated when `DIM` runs, may be local to a procedure, and report
their bounds through `LBOUND` / `UBOUND`. `ERASE` clears one; `OPTION BASE`
sets the default lower bound.

Undeclared variables spring into existence with a zero value, as in QBasic.
Inside a procedure a bare name is **local** unless it was declared `DIM SHARED`
(or named in a `SHARED` statement there). `STATIC` keeps a local between calls.

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

SELECT CASE n%
  CASE 0          ...
  CASE 1, 2       ...
  CASE 3 TO 9     ...
  CASE IS >= 100  ...
  CASE ELSE       ...
END SELECT

EXIT FOR | EXIT DO | EXIT WHILE | EXIT SUB | EXIT FUNCTION
GOTO label        ' label: or a line number, within the same procedure
GOSUB label ... RETURN
ON n% GOTO a, b, c
ON n% GOSUB a, b, c
```

Data lives in the program text:

```basic
READ n%, name$
RESTORE levels
levels: DATA 3, "first", 5, second
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

Assigning to the function's own name sets the return value; the same name
*followed by `(`* is a recursive call. Runaway recursion faults at 512 frames
rather than taking the editor down.

Arguments are passed **by reference**, as in QBasic: a procedure that assigns
to a parameter changes the caller's variable, array element or record field.
Pass an expression, or wrap the variable in parentheses, to pass a value.
Whole arrays go as `name()`:

```basic
SUB Fill(a(), v%)
  FOR i% = LBOUND(a) TO UBOUND(a) : a(i%) = v% : NEXT
END SUB
Fill scores(), 0
```

`DEF FNname(x) = expression` defines a one-line function.

### Graphics

The screen is a palette-indexed framebuffer. Colours are `0`–`15` (the standard
16-colour palette); `PALETTE` redefines any entry.

```basic
SCREEN 320, 200                    ' resize the framebuffer
SCREEN 13                          ' or a QBasic mode: 320x200, 256 colours
CLS                                ' clear to current background
CLS 1                              ' clear to colour 1
PSET (x, y), c
LINE (x1, y1)-(x2, y2), c          ' line
LINE (x1, y1)-(x2, y2), c, B       ' box outline
LINE (x1, y1)-(x2, y2), c, BF      ' filled box
LINE -(x2, y2), c                  ' from the last point
CIRCLE (x, y), r, c                ' outline
CIRCLE (x, y), r, c, F             ' filled (uBasic extension)
CIRCLE (x, y), r, c, start, end    ' arc, radians
PAINT (x, y), c                    ' flood fill
PALETTE i, r, g, b                 ' 0..255 components
GET (x1, y1)-(x2, y2), sprite%     ' capture into an array
PUT (x, y), sprite%, PSET          ' draw it: PSET PRESET AND OR XOR
```

Coordinates accept `STEP` for relative positions.

Everything clips silently — plotting off-screen is a no-op, never an error.

### Text

Text is blitted from a built-in 6×8 bitmap font, so a character cell is 6×8
pixels and a 320×200 screen is 53×25 characters. QBasic `SCREEN` modes use an
8×8 font covering all of CP437 instead, stretched to the mode's 8×14 or 8×16
cell, so `CHR$(219)` is the solid block. There is no font asset and no
TextMeshPro dependency.

```basic
COLOR 14          ' foreground only
COLOR 14, 1       ' foreground, background
LOCATE row, col   ' 0-based
PRINT "score"; s%          ' ";" = no gap
PRINT a$, b$               ' "," = next 14-column zone
PRINT "no newline";        ' trailing ";" suppresses the newline
PRINT USING "Score: #,###  [&]"; s%; name$
PRINT TAB(20); "column 20"; SPC(3); "three on"
```

Numbers print QBasic-style, with a leading space standing in for the sign.
Printing past the bottom row scrolls.

### Builtins

**Math** `ABS SGN INT FIX CINT CLNG CSNG CDBL SQR SIN COS TAN ATN ATN2 EXP LOG
MIN MAX RND` (`RND` alone gives 0–1, `RND(n)` gives an integer 0…n-1)

**Strings** `LEN LEFT$ RIGHT$ MID$ INSTR UCASE$ LCASE$ LTRIM$ RTRIM$ TRIM$
SPACE$ STRING$ CHR$ ASC STR$ VAL HEX$ OCT$ DATE$ TIME$` (`MID$` is 1-based,
like QBasic, and also works as a statement: `MID$(a$, 2, 3) = "xyz"`)

**Arrays** `LBOUND UBOUND`

**Screen** `POINT(x, y)` `SCRW` `SCRH` `CSRLIN` `POS(0)` `TIMER`

**Input** `KEY(code)` held, `KEYHIT(code)` pressed this frame, `MOUSEX` `MOUSEY`
`MOUSEB(n)`. A key code is the ASCII code — `KEY(ASC("A"))` — plus `KEY_LEFT%`
and friends for the arrows. `INKEY$`, `INPUT` and `LINE INPUT` read typed
characters instead, and `INPUT` suspends the program until Enter.

**Other** `RANDOMIZE seed`, `SWAP a, b`, `SLEEP seconds`, `WAIT`, `END`

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
immediately so the program keeps drawing. As in QBasic the background buffer
holds 32 notes; a `PLAY` that overfills it waits for room:

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

A uBasic program that loops without ever reaching `WAIT` burns its instruction
budget and gets a console warning instead of hanging the editor. A classic
QBasic program has no `WAIT` at all: it is simply paused at the end of the
budget and resumed next frame, which is the same mechanism.

## Design notes

**Pipeline.** Source → tokens → bytecode, with no AST. A few cheap pre-passes
over the token list collect what has to be known up front — `DEFINT` defaults,
`TYPE` layouts, `DATA` items, `SUB`/`FUNCTION` headers — and then a single
recursive descent emits instructions directly.

**Untyped slots.** Because every variable's type is fixed by its sigil, the
compiler knows the type of every stack cell. Runtime values are an 8-byte
untyped union, never a tagged variant:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct Slot {
    [FieldOffset(0)] public int    I;   // INTEGER, LONG, array handle
    [FieldOffset(0)] public double F;   // SINGLE, DOUBLE
    [FieldOffset(0)] public int    S;   // handle into StringHeap
}
```

No boxing, no type checks in the dispatch loop, no per-operation allocation.
`AddI` and `AddF` are separate opcodes the compiler picks. Stacks and locals
are preallocated `Slot[]`; an array is one `Slot[]` behind a handle, and a
`TYPE` record is a contiguous run of slots, so a field access is a constant
offset.

**By reference without pointers.** Parameters are copy-in / copy-out: after a
call returns, its frame's slots are still intact above the caller's, and the
caller copies changed parameters back. The copy is only kept for parameters a
procedure actually assigns, directly or by passing them on; the rest are
patched out after compilation.

**Strings.** The one thing that does allocate. `StringHeap` is a free-listed
`string[]` with a precise mark-sweep. Since uBasic has no closures, every live
string is reachable from a small fixed set of places — the pinned constant
pool, globals typed `Str`, active frames' locals typed `Str`, and the string
slots of live arrays. Collection only runs at a statement boundary with an
empty operand stack, so the root set is exact: no conservative scanning, no
write barriers.

**A clock that follows the program.** `TIMER` advances with instructions
executed inside a frame, not just between frames, so busy-wait loops written
for a 1991 PC measure and consume time consistently however fast the host is.

**Audio without a filter callback.** Each cached `AudioClip` holds a whole number
of cycles of a square wave and is played **looping**, so a clip encodes a
*frequency* and nothing else. Duration is decided by when playback stops. One
clip therefore serves every note length, and the whole chromatic range QBasic
can address is about 84 clips of roughly 8 KB — generated up front by
`PrewarmNotes`. Using 4096-sample chunks rather than one cycle keeps every note
inside a quarter of a cent; single-cycle rounding would be about 4 cents sharp
at A4, audible in a melody. Notes are placed on `AudioSettings.dspTime` across a
small pool of `AudioSource`s, so timing is independent of frame rate.

**Host binding.** `Host.cs` is a flat table of fixed-arity signatures with
declared parameter types. The compiler resolves the overload by argument count
and converts each argument as it compiles it, so a call is an array index and a
delegate invoke.

## Limitations

Deliberate, and worth keeping that way:

- **No closures, no dynamic containers.** This is what buys the flat state and
  the exact GC. Event wiring and anything you'd write with a lambda is worse.
  Use a named `SUB` instead.
- **No file I/O.** `OPEN`, `PRINT #`, `INPUT #` and friends are compile errors.
- **Not yet:** `DRAW`, `WINDOW`, graphics `VIEW`, `ON KEY` / `ON TIMER` events,
  the `SCREEN()` function, `INPUT$`. `ON ERROR` is accepted but handlers never
  run.
- **INTEGER does not overflow at 32767** and fixed-length strings are not
  padded; a program that relies on either will behave differently.
- **By reference is copy-in / copy-out.** Indistinguishable from the real thing
  unless a procedure reaches the same variable through two names at once.
- **Arrays must be `DIM`med.** No implicit 0…10 arrays.
- **One program per runner.** No modules or `#include`.
- **Sound is monophonic**, one square-wave voice, as on a PC speaker.
- **`KEY`/`MOUSE`/`INKEY$` need the legacy Input Manager.** Set *Project Settings →
  Player → Active Input Handling* to **Both**, or swap in your own `IInput`.
- **WebGL needs a user gesture** before any audio plays — a browser rule. Have
  the player click something before the first `PLAY`.

## Development

The whole language is engine-free, so it builds and runs under Mono with no
Unity installation:

```bash
# the package's own Unity tests, headlessly
mcs -out:tests.exe Runtime/*.cs 'Tools~/NUnitShim.cs' 'Tools~/TestRunner.cs' Tests/Editor/*.cs
mono tests.exe

# the uBasic harness: runs a program, writes a PNG (and a WAV if it makes sound)
mcs -out:ubasic.exe Runtime/*.cs 'Tools~/TestHost.cs'
mono ubasic.exe --selftest
mono ubasic.exe 'Samples~/Demos/smoke.ubas' out.png
mono ubasic.exe 'Samples~/Demos/sound.ubas' out.png 1200   # also writes out.wav
```

To see the tests inside Unity's Test Runner when uBasic is installed as a
package, add it to `testables` in your project's `Packages/manifest.json`:

```json
{ "testables": [ "com.unitycoder.ubasic" ] }
```

`Tools~/NUnitShim.cs` implements just the slice of NUnit the tests use, so the
same `Tests/Editor/*.cs` files run both in the Unity Test Runner and in CI.
Compiling `Runtime/*.cs` without `Runtime/Unity/` is itself the engine-free
check, and CI does exactly that.

`Tools~/font_gen.py` regenerates `Runtime/Font6x8.cs` from readable ASCII art
and emits a preview sheet — edit the glyph art there, not the hex. CI verifies
the checked-in file matches what the generator produces.

`Tools~/font_gen_cp437.py` builds `Runtime/FontCP437.cs` from Daniel Hepper's
public-domain [font8x8](https://github.com/dhepper/font8x8), re-indexed to
CP437, with the symbols font8x8 lacks drawn by hand in the script.

## Samples

| File | Shows |
|------|-------|
| `smoke.ubas` | Every primitive, text, arrays, `SELECT CASE`, a `FUNCTION`, `GOTO` |
| `balls.ubas` | `WAIT`-driven animation, arrays of floats, string building under GC |
| `batball.ubas` | `KEY` input, block `IF`, `EXIT DO`, a full little game loop |
| `sound.ubas` | `BEEP`, `SOUND` sweep, the silent-delay idiom, MML scale, `MB` music under animation |

### Made With uBasic

- ludum dare entry: https://ldjam.com/events/ludum-dare/60/snake-temple (.ubas source code available in the link)

## Licence

MIT — see [LICENSE.md](LICENSE.md).
