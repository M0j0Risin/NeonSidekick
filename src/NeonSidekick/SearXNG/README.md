# SearXNG.Net

The built-in metasearch behind *Web search method*'s `metasearch`. It's a .NET 10, Native AOT compatible port of the search core of [SearXNG](https://github.com/searxng/searxng): it asks several search engines at once, merges duplicates and ranks the results, all in-process. It knows nothing about the app around it; only `Web/BuiltInMetaSearch.cs` uses it, and `SearXNGBoundaryTests` keeps it that way.

## Source and changes

This is a modified translation of SearXNG's code, not SearXNG itself. It was translated from SearXNG commit `3e95f6c` (2026-11-05), ported on 2026-11-06 and merged into this app on 2026-11-06. It has changed since the merge, and this repository's history has the dates. *Differences from SearXNG* lists what it changes. Its history before the merge is in the separate SearXNG.Net repository.

Generated tables keep their data's own terms. `Locales/LocaleData.cs` comes from Babel 2.18.0's locale data (BSD-3-Clause), and `Html/HtmlEntityTable.cs` from Python's HTML5 entity list.

## Status

| Area | State |
|---|---|
| Parallel search, timeouts, resting failed engines | Ported |
| Result merging and ranking (`searx/results.py`) | Ported for main results |
| Engines | Bing, Brave, Brave Search API (`braveapi`, with a key), DuckDuckGo, Startpage, Wikipedia |
| Answers, infoboxes, plugins, web UI, bot limiter | Not ported, and not planned |

## Tests

Tests are in `tests/NeonSidekick.Tests/SearXNG` and never touch the network. The fixtures in `tests/NeonSidekick.Tests/Fixtures/searxng` are real pages from 2026-11-06, reduced to what the engines parse, with scripts, tracking values and session tokens removed.

## Comparing with SearXNG

`GoldenTests` compares the port with `Fixtures/searxng/golden/searxng.json`, which records what SearXNG's own code did on the same inputs. Differences must be named in the test or listed below, so any other difference fails.

To regenerate it from the saved pages, or the tables that come from SearXNG's, Babel's and Python's data, use Python 3.13.14 with SearXNG's `requirements.txt` installed, as `html.parser` changes between patch releases. Nothing in `tools/searxng` fetches a page: `make_fixtures.py` trims hand-saved Brave, Startpage and Wikipedia pages. Check out SearXNG first; on Windows, a sparse checkout of `searx/` avoids a filename Windows can't create.

```powershell
python -I tools/searxng/generate_tables.py <searxng> src/NeonSidekick/SearXNG
python -I tools/searxng/generate_goldens.py <searxng> tests/NeonSidekick.Tests/Fixtures/searxng tests/NeonSidekick.Tests/Fixtures/searxng/golden/searxng.json
```

## Engines not ported

Each was tried once or a few times from one home IP on 2026-11-06, with plain `HttpClient`, unless the row says otherwise.

| Engine | Why not |
|---|---|
| Google | `wml/search`, SearXNG's request, returned 403 to every client tried, including `curl_cffi`. |
| Google CSE | Not tried. SearXNG's engine calls an undocumented endpoint (a token from `cse.js`, then `cse.google.com/cse/element/v1`) with another site's search ID, blackle.com's, which isn't one to borrow. A new ID can't do general search: [Google's help page](https://support.google.com/programmable-search/answer/12397162) limits new search engines to 50 domains, apart from nonprofits, accredited schools and government agencies. Existing whole-web ones keep that option until January 1, 2027; that this also ends blackle.com's ID is only an inference. Google's [JSON API](https://developers.google.com/custom-search/v1/overview) is closed to new customers and ends on the same date. |
| Qwant | 403 with a DataDome challenge. |
| Yep | 403. |
| Mojeek | Answers with a proof-of-work CAPTCHA, which SearXNG solves. |
| Yahoo | Works, but on two searches its first six results nearly matched Bing's. Merging would count Bing's picks twice. |

## Differences from SearXNG

Each of these is deliberate.

| Difference | Why |
|---|---|
| No browser TLS impersonation (`curl_cffi`); plain `HttpClient` | It adds a native dependency and bypasses a host's connection rules. In a small test from one home IP, `HttpClient` did as well or better on five engines. Datacenter IPs or other platforms may differ. |
| Engines are compiled in, not loaded at runtime | Native AOT can't load code at runtime. |
| Results merge in configured engine order, not finishing order | The same answers always give the same ranking. |
| Non-web URLs (`javascript:`, `data:`, `file:`) and URLs with no host are dropped | SearXNG leaves this to its templates. A library has none. |
| A refused or changed DuckDuckGo, Bing or Startpage page is a failure, not "no results" | SearXNG logs it or returns nothing, which hides blocks and site changes. |
| DuckDuckGo links must stay on `https://*.duckduckgo.com` | They come from scraped pages, and shouldn't be able to send the host's client elsewhere. |
| `braveapi` sends `text_decorations=false`, and reads a key at each search | SearXNG sends Python's `False`; the API documents a boolean. A host's key can change without a new `MetaSearch`, which would forget the rests. |
| Text after a Bing snippet icon is kept | lxml's `remove` also drops the text after the element, which looks unintended. |
| Bing markets are sent as `mkt`, not `setlang` and `cc`, except for the US, China and Russia | In a small test from one home IP on 2026-11-06, `cc` gave off-topic results for `en-GB` and `de-DE` with both `HttpClient` and `curl_cffi`, where `mkt` gave relevant ones. Those three markets keep SearXNG's `setlang` alone, because `mkt` was off-topic for Russia and generic for China. It isn't a full fix: in one search each across 11 markets, `en-AU` was still off-topic and `en-CA` generic. |
| Locale tags read the same in any case | SearXNG only finds an engine's locale in the exact case, so on Bing `en-in` goes to `hi-in` while `en-IN` goes to `en-in`. |
| Brave's video results and related queries are left out | They aren't main results, which are all this port handles. |
| Brave's page is read by a small data-only parser | SearXNG rewrites the JavaScript into JSON, which can fail on a double quote in a backtick string. Neither runs the page's code. |
| Startpage's proof-of-work challenge is reported as a CAPTCHA, not solved | The engine rests instead. |
| DuckDuckGo's JavaScript challenge is reported as a CAPTCHA, not answered | The engine rests for an hour instead. |
| HTML nested more than 256 levels deep is an engine failure | HtmlAgilityPack has no limit by default, and a stack overflow would end the whole app with no chance to catch it. Saved real pages nest at most 21 deep. |
| Wikipedia gives an ordinary result, not an infobox | Infoboxes aren't ported. This is the same engine's `list` form. |
| A `/` in a Wikipedia title is encoded | Otherwise a query could change the path on the API host. It also lets titles like `AC/DC` work as titles. |
| Thumbnails don't form their own result group | SearXNG groups results by `category:template:img_src`. Results here carry no thumbnail. |
| Startpage's news and image regions are left out | Only `web-google` is read, as this port only handles main web results. |
| Redirects are up to the host's client | SearXNG's default is not to follow them. This app's adapter follows up to 3, judging each. |
| A locale Babel doesn't know goes into `Accept-Language` as written | SearXNG sends `en-US` for it, and Babel also remaps some tags, such as `en-ZZ` to `en-US`. Its parser isn't ported, and this app always sends `en-US`. |
| A stray end tag's name is lowercased by .NET's rules, not Python's | They differ on a few characters such as `İ` and `Σ`, which only shows in a stray non-ASCII end tag echoed into a snippet. |
| Startpage's results end by matching brackets, not at the first `}})` | Otherwise a snippet holding those characters would lose the whole answer. |
| A Startpage code or DuckDuckGo first-page link isn't reused after a failure | SearXNG keeps it until it expires, so searches until then could fail the same way. |
| A `.` or `..` Wikipedia title asks nothing | The path would be resolved away, asking for another address. |
| Overlapping searches can't shorten a rest | A shorter rest doesn't replace a longer one, and a success from a search that started first doesn't clear it. |
| A JSON string holding half a surrogate pair counts as missing | Python keeps it, and .NET can't read it. One cut snippet shouldn't lose an engine's answer. |
| A host over 253 characters isn't decoded | No DNS name is longer, and decoding one takes quadratic time before it's refused. |
| A search returns at its deadline with parsing left running, as SearXNG's does, but the engine isn't asked again until that parse ends | Parsing can't be canceled, so repeated searches would otherwise pile up stuck parses. |

## License

AGPL-3.0-or-later, as SearXNG is; see [LICENSE-AGPL](../../../LICENSE-AGPL). This is a translation of SearXNG's code, and its copyright remains with the SearXNG authors.
