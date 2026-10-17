# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-10-17

### Added
- Packaged as a UPM package installable directly from a git URL.
- Assembly definitions. `Unitycoder.UBasic` sets `noEngineReferences`, so it is a
  compile error for core language code to reference UnityEngine — the boundary
  is now enforced rather than merely intended.
- Unity Test Framework suite under `Tests/Editor` (20 tests), runnable both in
  the editor and headlessly under Mono via `Tools~/TestRunner.cs`.
- GitHub Actions workflow running the full suite plus every sample program on
  each push.
- Sound: `SOUND`, `BEEP` and `PLAY` with QBasic MML (notes, octaves, tempo,
  dotted lengths, `ML`/`MN`/`MS` articulation, `MB`/`MF` background/foreground).
- `SoundScheduler`, an engine-free note timeline driven by an injected clock.
- `UnityAudioAdapter`, which plays tones as looping pre-rendered `AudioClip`s.
  No `OnAudioFilterRead`, so audio works on WebGL.
- Stall watchdog: the runner warns if a program waits on sound for over 10s.

### Fixed
- Recursive `FUNCTION` calls. A mention of a function's own name inside its
  body was always treated as the result variable, so `Fib(n - 1)` failed to
  parse. A name followed by `(` is now a recursive call, as in QBasic.
- An idle audio scheduler reported "busy" forever, permanently blocking a
  program on its first `SOUND`.
- `MOD` and `\` converted the wrong stack slot when the left operand was a
  float, faulting with a spurious "MOD by zero".
- Single-line `IF c THEN a : b` ran `b` unconditionally. Everything after
  `THEN` on the line now belongs to the `THEN` clause, as in QBasic.
- A typo'd function name with a subscript list became a silent implicit
  variable instead of a compile error.

## [0.1.0] - 2026-09-12

### Added
- Initial language: lexer, two-pass bytecode compiler, stack VM.
- Palette-indexed framebuffer with `PSET`, `LINE`, `CIRCLE`, `PAINT`, `PALETTE`.
- Built-in 6x8 bitmap font with `PRINT`, `LOCATE`, `COLOR`.
- `WAIT` for per-frame suspend/resume, with a per-frame instruction budget.
- Precise mark-sweep for the string heap.
- `.ubas` scripted importer that compile-checks on every save.
