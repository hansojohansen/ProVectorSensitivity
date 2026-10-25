# Third-party notices

OpenVector is licensed under the MIT License (see [`LICENSE`](LICENSE)). This file lists third-party
software that OpenVector uses or interoperates with, and the projects we only consulted.

## Raw Accel (MIT)

- Project: <https://github.com/RawAccelOfficial/rawaccel>
- License: MIT. Upstream text: <https://github.com/RawAccelOfficial/rawaccel/blob/master/LICENSE>

**How we use it.**
- The product does **not** bundle Raw Accel's driver or binaries. Users install official Raw Accel themselves.
- OpenVector writes a Raw Accel `settings.json` and runs Raw Accel's own `writer.exe`.
- The `settings.json` key map and the projection math are based on reading Raw Accel's MIT-licensed
  source; see `docs/research/A-input-layer.md`.

**Research fork.** `research/rawaccel-angular/` is a research fork of Raw Accel v1.7.1. It keeps
upstream's LICENSE and attribution in that directory. It is not part of the OpenVector product
([D11](docs/DECISIONS.md)).

**TODO (Docs/Release, before first release).** If any Raw Accel source is ever copied or ported into
`src/`, add its full MIT notice here verbatim, with the upstream copyright lines:

```
MIT License

Copyright (c) <year> <Raw Accel copyright holders, copy verbatim from upstream LICENSE>

Permission is hereby granted, free of charge, to any person obtaining a copy ...
[full text to be copied verbatim from the upstream LICENSE]
```

## NuGet dependencies

These are resolved at build time and are not vendored in this repository. Current and planned ones:

| Package | License | Used by |
|---|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 | tests only |
| Microsoft.NET.Test.Sdk | MIT | tests only |
| coverlet.collector | MIT | tests only |
| MathNet.Numerics (planned) | MIT | Core |
| Avalonia (planned) | MIT | App |
| ScottPlot 5 (planned) | MIT | App |
| Silk.NET or Vortice.Windows (planned) | MIT | Trainer |
| Velopack (planned) | MIT | installer |

Release builds must regenerate this table from the actual dependency graph.

## Projects consulted as design references only

- **OpenAimTrainer** (Apache-2.0). Evaluated as a fork base and rejected ([D6](docs/DECISIONS.md)). It may
  serve as a design reference. No code is copied. If any is copied, add its Apache-2.0 NOTICE here.

### GPL projects: consulted as reference only, no code copied

The following projects are GPL-licensed. We read their public documentation or behaviour as background.
**No code from them is copied into OpenVector**, and contributors must not copy any ([D0](docs/DECISIONS.md)):

- **RefleK's** (GPL-3.0)
- **LibreAim** (GPL)
- **ValTrainer** (GPL)

### Not used

- **NVIDIA FPSci** (CC BY-NC-SA). Its non-commercial share-alike license is incompatible with our goals.
  It is not used or consulted for code.
