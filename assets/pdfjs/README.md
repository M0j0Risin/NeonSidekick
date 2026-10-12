# pdf.js

Mozilla's PDF renderer, embedded so the headless browser can read a store's reference PDFs (the shopping list, 2026-10-11):
`HeadlessBrowser.ReadPdfAsync` imports both modules into a blank page, draws each page to a canvas and reads its text.

- Package: `pdfjs-dist` 4.10.38 from the npm registry, `build/pdf.min.mjs` and `build/pdf.worker.min.mjs` as published, unchanged.
- Tarball integrity (checked when copied): `sha512-/Y3fcFrXEAsMjJXeL9J8+ZG9U01LbuWaYypvDW2ycW1jL269L3js3DVBjDJ0Up9Np1uqDXsDrRihHANhZOlwdQ==`
- Licence: Apache-2.0 (`LICENSE` here), compatible with the app's GPLv3.

To update: take both files from the same `pdfjs-dist` release, record its version and integrity here, and run the
`shopping:pdf-read` smoke probe on the published exe.
