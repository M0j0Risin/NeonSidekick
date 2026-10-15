# pdf-lib

A PDF editor written in JavaScript, embedded so the headless browser can change PDFs: `pdf_edit` and `/pdf merge|extract|rotate|delete`
(2026-10-14). `HeadlessBrowser.EditPdfAsync` evaluates the build in a blank page, opens the PDFs there, makes the change, and reads
the new file back. Nothing is written to disk but the result, which the app writes.

- Package: `@cantoo/pdf-lib` 2.11.1 (2026-09-15) from the npm registry, `dist/pdf-lib.min.js` as published, unchanged. It is the
  maintained fork of Andrew Dillon's `pdf-lib` (whose last release, 1.17.1, is from 2021).
- Tarball integrity (checked when copied): `sha512-lYuRJFfXgoP8WVE8HYSeoLfDcnSXmjMLmdZS7YvmYHngbcZfiqDOruHrZBqjYizkrNa4ad1yW4WCrPYyb26T3Q==`
- `pdf-lib.min.js`'s SHA-256: `2294A0BB8742D8948AE2D49A60CB6A3600C0B5DEAE6DE198E57FB368D1B79BA9`
- License: MIT (`LICENSE.md` here), compatible with the app's GPLv3. The build bundles, by its source map:
  - culori, fflate, html-entities, node-html-better-parser: MIT
  - tslib: 0BSD
  - pdf-lib's own standard-fonts (Adobe's core 14 font metrics) and UPNG.js packages, base64-arraybuffer, and code from pdfkit: MIT
  - a stream and flate decoder ported from Mozilla's pdf.js (and XPDF before it): Apache-2.0

It is a classic UMD script: evaluated as one, it sets `globalThis.PDFLib`. `BrowserPdfEditor.LoadExpression` adds an object after it,
since a DevTools evaluate answers objects only.

To update: take `dist/pdf-lib.min.js` and `LICENSE.md` from a release at least two weeks old, record its version and integrity here,
check the bundled licenses again (the source map's `sources`), run the real-browser tests (`BrowserPdfEditorTests`, which merge, cut,
rotate and fill a form in the machine's own browser), and run the `pdf:edit` smoke probe on the published exe.
