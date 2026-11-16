# Running classic QBasic programs

Drop a `.BAS` file into the project (it imports like `.ubas`), assign it to a
uBasic Runner, press play. NIBBLES.BAS and GORILLA.BAS run unmodified.

## Dialects

`UBasicRunner.Dialect` is `Auto` by default:

- a bare `WAIT` or `SCREEN w, h` anywhere -> **uBasic** (unchanged behaviour)
- otherwise, anything only classic programs use (`DECLARE`, `DEFINT`, `GOSUB`,
  `DATA`, `INKEY$`, `INPUT`, `TYPE`, line numbers, ...) -> **QBasic**
- otherwise -> uBasic

| | uBasic | QBasic |
|---|---|---|
| start-up screen | `ScreenWidth` x `ScreenHeight`, 6x8 font | `SCREEN 0`, 80x25 text |
| `LOCATE` | 0-based | 1-based |
| `CLS n` | clear to colour n | 0/1 whole screen, 2 text viewport |
| `RND(n)` | whole number 0..n-1 | QBasic's (n is a mode, not a range) |
| `PAINT (x,y), c` | flood the colour under the point | fill out to border colour |
| `Name(a, b)` without `CALL` | a call | `Name (a)` passes a by value |

Everything else is shared, so uBasic programs can use GOSUB, TYPE, INKEY$ etc.

## What is supported

- Types: `%` `&` `!` `#` `$`, `DEFINT`/`DEFLNG`/`DEFSNG`/`DEFDBL`/`DEFSTR`, `DIM x AS ...`,
  `TYPE ... END TYPE` (nested, arrays of, whole-record assignment and `SWAP`)
- Arrays: any bounds (`lo TO hi`, expressions), up to any dimensions, `REDIM`, `ERASE`,
  `LBOUND`/`UBOUND`, `OPTION BASE`, local and `STATIC` arrays, arrays as parameters
- Procedures: by-reference parameters, `STATIC` subs, `SHARED`, `DEF FN`, `DECLARE` (ignored)
- Flow: `GOSUB`/`RETURN`, `ON n GOTO/GOSUB`, line numbers and labels, `SELECT CASE` with
  `TO` and `IS`, `EXIT`, `DO`/`WHILE`/`FOR` (incl. `NEXT i, j`), single-line `IF ... THEN 100`
- Data: `DATA`/`READ`/`RESTORE`, `CONST` expressions, `SWAP`, `MID$` statement
- Screen: `SCREEN 0, 1, 2, 7, 8, 9, 11, 12, 13`, `WIDTH`, `VIEW PRINT`, `COLOR`, `PALETTE`,
  `PSET`/`PRESET`, `LINE` (`B`/`BF`, `STEP`), `CIRCLE` (arcs, aspect), `PAINT`, `POINT`,
  `GET`/`PUT` in QBasic's real image layouts (sprites in DATA work)
- Text: full CP437 character set, `PRINT USING`, `TAB`, `SPC`, `CSRLIN`, `POS`
- Keyboard: `INKEY$` (extended keys as `CHR$(0)` + scan code), `INPUT`, `LINE INPUT`
- Time: `TIMER`, `SLEEP`, `SOUND`/`PLAY`/`BEEP`, `RANDOMIZE`
- Accepted and ignored: `DEF SEG`, `PEEK` (returns 0), `POKE`, `OUT`, `ON ERROR`, `KEY OFF`

## Timing

Classic programs never yield; they busy-wait. The VM runs `InstructionBudget`
instructions per frame and `TIMER` advances with them, so a program that
calibrates a delay loop against `TIMER` (NIBBLES) gets delays of the length it
asked for. A loop that only polls `TIMER` (GORILLA's `Rest`) is throttled to
about 1000 polls a second (`VM.TimerPollsPerFrame`), and an `INKEY$` loop that
finds no key gives the rest of the frame back (`VM.YieldOnIdleInkey`).

## Not supported yet

Files (`OPEN`, `PRINT #`...), `DRAW`, `WINDOW`, graphics `VIEW`, `ON ERROR` trapping
(handlers never run), `ON KEY/TIMER` events, `SCREEN()` function, `INPUT$`,
16-bit INTEGER overflow (INTEGER is 32-bit), fixed-length string padding,
parameters declared `AS ANY` in the SUB line itself.
