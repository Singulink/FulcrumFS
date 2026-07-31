Note: you can create a `ffmpeg_path.txt` file like so for testing locally without having to set the environment variable manually (which also allows testing in VS Code for example - note leading / trailing whitespace is ignored) - place it in this directory:
```
/opt/homebrew/bin
```

See the `readme.md` in `Videos` folder for the licensing information for some of the files in there that are under a different license.

When testing locally, by default tests are ordinarily run with a higher level of parallelism than what CI uses, to get the results faster. You can adjust this in testconfig.json (see `.github/config/testconfig.windows.json` for example). However, it may be too high for the amount of RAM you have, so you may get spurious test failures due to out of RAM or similar, so simply re-run any failing tests afterwards to double check this.

Some tests are excluded from CI runs - to exclude these locally too, set the `CI=true` environment variable when building. Additionally, there are some special runs that test specific hardware acceleration methods in ffmpeg to ensure we do not regress their behaviour - these are set via `HWACCEL_MODE` envorinment variable - currently supported options are `auto` (tests automatic hardware acceleration selection using the normal detection logic), `decodeonly` (forces `-hwaccel auto` with no hardware filters, for comparing opportunistic hardware decoding against the software baseline), `videotoolbox`, `cuda`, `qsv`, `amf`, and `d3d12va`. Builds without `HWACCEL_MODE` set act as the software baseline, since the library's `HardwareAccelerationKind` option defaults to `None`; builds with `HWACCEL_MODE` set default to `Auto` instead, so that the forced hardware acceleration mode is exercised by the tests without requiring per-test configuration.
