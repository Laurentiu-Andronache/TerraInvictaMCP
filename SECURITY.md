# Security policy

## Reporting

- Do not open a public issue. Use GitHub's private vulnerability reporting on
  this repository (Security tab, "Report a vulnerability").
- Send a working example you have run against real project code. A
  report without one will not be fixed.
- Describe what you saw and the steps to make it happen. Leave out
  severity ratings, CVSS scores and CWE numbers. Do not paste scanner
  output as a finding.

## Known and accepted

We know and accept two details, so do not report them. Both are
covered under "Safety and privacy" in `README.md`.

- **The bridge socket has no authentication.** The bridge is the small TCP
  server the mod runs inside the game. It listens on `127.0.0.1` only,
  and any program running as you on that computer can connect to it and drive
  the game while the game is open. This is the same rule as any other
  program you run.
- **Console commands can unlock Steam achievements.** Terra Invicta
  normally stops achievement unlocks when the debug console is on.
  This mod reaches the console directly, so a game with no console mod can
  still unlock them through it.

## Supported versions

We only support the current release. Fixes go on top of `main`. There are
no backports.
