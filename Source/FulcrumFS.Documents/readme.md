To use this library, you will need a LibreOffice installation (or portable distribution) available to the host. Requirements:
- LibreOffice 7.x or later is recommended.
- The components required for the document types you convert must be installed: Writer (doc/docx/odt/rtf), Calc (xls/xlsx/xlsm/ods), and Impress (ppt/pptx/odp).
- Point `DocumentPdfConversionProcessor.ConfigureWithLibreOffice` at the directory containing the `soffice` executable:
  - Windows: the LibreOffice `program` directory (e.g. `C:\Program Files\LibreOffice\program`), containing `soffice.com` / `soffice.exe`.
  - Linux/macOS: the directory containing the `soffice` executable (e.g. `/usr/bin` or `/usr/lib/libreoffice/program`).

Each conversion runs in an isolated LibreOffice user profile so conversions can run concurrently. The maximum number of concurrent LibreOffice processes can be limited via the configuration options passed to `ConfigureWithLibreOffice` - note that each process can consume a significant amount of memory.
