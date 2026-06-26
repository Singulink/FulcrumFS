Note: you can create a `ffmpeg_path.txt` file like so for testing locally without having to set the environment variable manually (which also allows testing in VS Code for example - note leading / trailing whitespace is ignored) - place it in this directory:
```
/opt/homebrew/bin
```

See the `readme.md` in `Videos` folder for the licensing information for some of the files in there that are under a different license.

When testing locally, you may want to run with a higher level of parallelism than 2, to get the results faster. You can adjust this in testconfig.json. However, be aware that if you increase it too high you may get spurious test failures due to out of RAM or similar, so simply re-run any failing tests afterwards to double check this.
