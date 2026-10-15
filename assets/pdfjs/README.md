# pdf.js

Mozilla's PDF renderer, embedded so the headless browser can read PDFs: `read_pdf` (2026-10-14) and a store's reference PDFs
for the shopping list (2026-10-11). `HeadlessBrowser.ReadPdfAsync` imports both modules into a blank page, draws each page to a
canvas and reads its text, its outline and its page labels.

- Package: `pdfjs-dist` 4.10.38 from the npm registry, `build/pdf.min.mjs` and `build/pdf.worker.min.mjs` as published, unchanged.
- `cmaps/*.bcmap` (2026-10-14): the same package's packed CMaps, unchanged, so a PDF whose fonts use a predefined CMap (most Chinese,
  Japanese and Korean PDFs) reads its text. They are handed to pdf.js from memory (`BrowserPdfReader.CMapsExpression`), never fetched.
- Tarball integrity (checked when copied): `sha512-/Y3fcFrXEAsMjJXeL9J8+ZG9U01LbuWaYypvDW2ycW1jL269L3js3DVBjDJ0Up9Np1uqDXsDrRihHANhZOlwdQ==`
- Licenses: pdf.js is Apache-2.0 (`LICENSE` here), compatible with the app's GPLv3. The CMaps are Adobe's cmap-resources under
  BSD-3-Clause (`cmaps/LICENSE`).

To update: take both builds and the whole `cmaps` folder from the same `pdfjs-dist` release, record its version and integrity here,
run the real-browser tests (`BrowserPdfReaderTests`, which read an outline, a locked file and Japanese text in the machine's own
browser), and run the `shopping:pdf-read` and `pdf:cmap` smoke probes on the published exe.

Keep it current. Since 2026-10-14 `web_fetch` hands pdf.js PDFs from the web, not only the user's own files, so a pdf.js security
advisory is a reason to update. The reading runs with `isEvalSupported: false` (no font functions compiled: CVE-2024-4367's door) on a
blank page that reaches no network (`HeadlessBrowser.PdfReadArguments`), in a browser profile of its own.
