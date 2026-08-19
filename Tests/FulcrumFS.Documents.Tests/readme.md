These tests require a LibreOffice installation. Set the `LIBREOFFICE_PROGRAM_PATH` environment variable to the directory containing the `soffice` executable, e.g.:
- Windows: `C:\Program Files\LibreOffice\program`
- Linux: `/usr/bin` or `/usr/lib/libreoffice/program`
- macOS: `/Applications/LibreOffice.app/Contents/MacOS`

Note: you can create a `libreoffice_path.txt` file containing the path for testing locally without having to set the environment variable manually (which also allows testing in VS Code for example - note leading / trailing whitespace is ignored) - place it in this directory:
```
C:\Program Files\LibreOffice\program
```

The sample documents are shared with the `FulcrumFS.Tests` project - see the provenance notes in its `SampleFiles/README.md`.
