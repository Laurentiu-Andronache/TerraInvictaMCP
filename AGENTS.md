# Working in this repository

`docs/CONTRIBUTING.md` controls contributions. Read it before you write anything
you want to submit. It explains four of the five constraints
below. It covers the fifth constraint, stdout hygiene, under "Before
opening a PR".

## Layout

- `src/` is the in-game C# 6 DLL. It runs the bridge, a small TCP server inside
  the game. It answers verbs. Each verb is one named request. `server/` is the MCP
  (Model Context Protocol, the standard AI assistants use to call outside tools)
  stdio server. It uses only the Python 3 standard library.
- `docs/PROTOCOL.md` is the wire contract between them. It defines the
  verb names and payloads.

## Constraints, and where each is written down

Each bullet shows where the rule lives. It also names the tool that
enforces it.

- Build `-nostdlib` against the game's own assemblies. The header comment of
  `build.sh` and `build.ps1` ("-nostdlib against the game's own assemblies is
  deliberate") explains why. The compiler argument lists (`ARGS=` in `build.sh`, `$CArgs`
  in `build.ps1`) show the flags. There is no automated check. A typeref against the wrong
  assembly compiles cleanly and throws an error at load.
- `ModInfo.json` is the only `.json` this mod may contain. Read the comment in
  `release.sh` beginning "The game's Mod Manager parses EVERY .json". The
  `STRAY=` assertion under that comment enforces this rule. The "no stray .json" step in
  `.github/workflows/ci.yml` also enforces it.
- Verbs finish inside one frame. Read "Execution model" in `docs/PROTOCOL.md`. This is worked
  through under "Verbs finish inside one frame" in `docs/CONTRIBUTING.md`. No
  automated check exists. Code review is the only check.
- Stdout carries JSON-RPC and nothing else. Read the docstring of
  `server/stdio_selfcheck.py`. That script enforces the rule.
  `.github/workflows/ci.yml` runs it on every PR.
- Never crash the game. Read "Design principles" in `docs/PROTOCOL.md`. The
  catch-alls enforce this rule. These are `Verbs.Execute` in `src/Verbs.cs` and `OnUpdate` in
  `src/Main.cs`.

## Commands

- `./build.sh` (or `build.ps1`) writes the DLL where the game loads it. This is
  the whole loop for a DLL change. Set `TI_ROOT` if the source lives outside your game
  folder. `install.sh` also repairs the game install and registers MCP clients. Run
  it when you want those tasks, not as part of a build.
- Run `python3 server/stdio_selfcheck.py` after any Python change. It needs no running game.
- Run `python3 -m unittest discover -s server/tests` after any change to `server/modcheck.py`.
  Modcheck is the tool that checks a mod's templates merged correctly into the game.
  It runs the checks against a synthetic install in a temporary directory. It needs no
  game and no bridge. `.github/workflows/ci.yml` runs it on every PR.
- `python3 server/modcheck.py [check] [mod]` is the debug entry for modcheck. It is the only
  way to run modcheck without an MCP client.

## Versions

A wire-contract change is a mod version bump. The rule, the three files that state
the version, and what catches drift are under "Before opening a PR" in
`docs/CONTRIBUTING.md`. Release version numbers belong to the maintainer.
