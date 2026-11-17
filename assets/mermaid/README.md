# Mermaid

A diagram library written in JavaScript, embedded so the headless browser can draw diagrams for `render_picture` (2026-11-16).
`HeadlessBrowser.RenderPictureAsync` evaluates the build in a blank page, turns the model's diagram text into SVG there with
`securityLevel: 'strict'`, then draws the SVG in a fresh page with no script and no network. Nothing is written to disk but the picture,
which the app writes.

- Package: `mermaid` 12.2.0 (2026-10-13) from the npm registry, `dist/mermaid.min.js` as published, unchanged.
- Tarball integrity (checked when copied): `sha512-w7nr/SFrLO7BaVTs3dQTvYSV6nYSBj0Yy9DjBhsIJtAcHqRzXSq4Pg32ujHZ/AdWsAMNEvvhbm5dt9E2tOiHzw==`
- `mermaid.min.js`'s SHA-256: `A8A6A6F805B074A8261A2825C86F99C8054ACB3B35F50BB7E24A4F9F2C3C3B9F`
- License: MIT (`LICENSE` here), compatible with the app's GPLv3. The build bundles its dependencies, by its `package.json`:
  - d3, d3-sankey, dagre-d3-es, cytoscape and its layouts, khroma, stylis, roughjs, dayjs, marked, katex, chevrotain, es-toolkit,
    ts-dedent, `@braintree/sanitize-url`, `@iconify/utils`, `@mermaid-js/parser`, `@upsetjs/venn.js`: MIT, ISC or BSD
  - elkjs: EPL-2.0 (its secondary-license notice allows GPL)
  - dompurify: Apache-2.0 or MPL-2.0

It is the package's self-contained IIFE bundle: evaluated as a classic script, it sets `globalThis.mermaid`. The package's ESM entry loads
`dist/chunks/` and can't be evaluated as one text, so this is the file to ship. Icons named from a web font (`fa:`) don't draw: the page has
no network.

To update: take `dist/mermaid.min.js` and `LICENSE` from a release at least two weeks old, record its version and integrity here, check the
bundled licenses again, run the real-browser tests (`PictureRenderTests`, which draw a flowchart and catch a parse error in the machine's own
browser), and run the `render:png` smoke probe on the published exe.
