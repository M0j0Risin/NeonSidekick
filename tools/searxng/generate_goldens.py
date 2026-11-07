# SPDX-License-Identifier: AGPL-3.0-or-later
"""Records what SearXNG's own code does, for the C# tests to compare against.

Runs the real searx modules over fixed inputs and the test fixtures. Nothing here touches the network: every
HTTP call an engine would make is replaced with a stub.

usage: generate_goldens.py <searxng_checkout> <fixtures_dir> <output.json>
needs: SearXNG's requirements.txt installed in the running Python
"""
import json
import os
import subprocess
import sys
import types
import warnings

# html.parser changes between patch releases, and HtmlText follows this one, so another Python would record different decisions.
if sys.version_info[:3] != (3, 13, 14):
    sys.exit("needs Python 3.13.14, not " + sys.version.split()[0])

SEARXNG, FIXTURES, OUTPUT = sys.argv[1:4]
sys.path.insert(0, SEARXNG)
os.environ.setdefault("SEARXNG_SECRET", "golden-generation-only")
warnings.simplefilter("ignore")

import babel  # noqa: E402
import babel.core  # noqa: E402
import babel.localedata  # noqa: E402
from lxml import html as lxml_html  # noqa: E402
from urllib.parse import urlparse, quote_plus, parse_qs  # noqa: E402

import searx  # noqa: E402
import searx.engines  # noqa: E402
import searx.locales  # noqa: E402
import searx.metrics  # noqa: E402
from searx import settings, get_setting  # noqa: E402
from searx.results import ResultContainer  # noqa: E402
from searx.result_types import MainResult  # noqa: E402
from searx.result_types._base import _normalize_text_field  # noqa: E402
from searx.utils import html_to_text  # noqa: E402
from searx.network.raise_for_httperror import raise_for_httperror  # noqa: E402

ENGINE_NAMES = ("bing", "duckduckgo web", "brave", "startpage", "wikipedia")
# Startpage is inactive in SearXNG's default settings, which would stop it loading here.
searx.engines.load_engines([{**e, "inactive": False} for e in settings["engines"] if e["name"] in ENGINE_NAMES])
searx.metrics.initialize(list(ENGINE_NAMES))
ENGINES = searx.engines.engines
bing = ENGINES["bing"]
ddg = ENGINES["duckduckgo web"]
brave = ENGINES["brave"]
startpage = ENGINES["startpage"]
wikipedia = ENGINES["wikipedia"]
wikipedia.display_type = ["list"]  # The port has no infobox, so it uses this engine's list form.


def read_fixture(name):
    with open(os.path.join(FIXTURES, name), encoding="utf8") as f:
        return f.read()


class FakeResponse:
    """The parts of SXNG_Response the engines use."""

    def __init__(self, text, params=None, status_code=200, url="https://example.com/"):
        self.text = text
        self.search_params = params or {}
        self.status_code = status_code
        self.url = url
        self.headers = {}
        self.ok = status_code < 400
        self.cookies = {}

    def html(self):
        return lxml_html.fromstring(self.text)

    def json(self):
        return json.loads(self.text)


class FakeCache(dict):
    def set(self, key, value, expire=None):
        self[key] = value


def plain(result):
    get = result.get if isinstance(result, dict) else lambda k, d="": getattr(result, k, d)
    return {"url": get("url", ""), "title": get("title", ""), "content": get("content", "")}


def merged(result):
    return {
        "url": result.url,
        "title": result.title,
        "content": result.content,
        "score": result.score,
        "positions": list(result.positions),
        "engines": sorted(result.engines),
    }


# --- text ------------------------------------------------------------------------------------------------------------
HTML_TO_TEXT = [
    'Example <span id="42">#2</span>',
    "<style>.span { color: red; }</style><span>Example</span>",
    "regexp: (?&lt;![a-zA-Z]",
    "<p><b>Lorem ipsum </i>dolor sit amet</p>",
    "&#x3e &#x3c &#97",
    "",
    "<b>Native</b> <b>AOT</b> samples",
    "Let&#x27;s go",
    "a<br/>b",
    "a<br>b",
    "<p>a<br>b</p>",
    "a <script>var x = '<b>no</b>';</script> b",
    "a <SCRIPT>no</SCRIPT> b",
    "a <!-- hidden --> b",
    "a <!-- unterminated",
    "1 < 2",
    "1 < 2 > 1",
    "a <b",
    'x <a href="a>b">y</a>',
    "</b>stray",
    "  many   spaces\n\nhere ",
    " nbsp inside　wide ",
    "<B>upper</B>",
    "<b>mixed</B>",
    "</ b >x",
    "<b>a</b >c",
    "<p/>x",
    "<a/b>x</a>",
    "<!doctype html>a",
    "<?pi data?>a",
    "<![CDATA[x]]>a",
    "&amp;",
    "&amp",
    "&amp;amp;",
    "&copy; 2026",
    "&copy 2026",
    "&notin;",
    "&notanentity;",
    "&notanentity",
    "AT&T",
    "AT & T",
    "x &",
    "&#150;",
    "&#0;",
    "&#xD800;",
    "&#99999999999;",
    "&#128512;",
    "&apos;",
    "&hellip;",
    "&mdash;",
    "&nbsp;x&nbsp;",
    "&#x27",
    "&#39;s",
    "&#;",
    "&#x;",
    "<i>it&#x27;s</i> &quot;quoted&quot;",
    "<b>unclosed bold",
    "<",
    "<>",
    "a<>b",
    "</>a",
    "<1>a",
    "<b class='x>y'>z</b>",
    "<b class=\"unterminated>z</b>",
    "x</b",
    "x</",
    "x<!",
    "x<!doc",
    "x<?pi",
    "<b>a</b",
    "a <b c",
    "a <b>c <i",
    "&#1;",
    "&#xb;",
    "&#13;",
    "&#x7F;",
    "&#x81;",
    "&#x8D;",
    "&#xFDD0;",
    "&#xFFFE;",
    "&#x1FFFF;",
    "&#x10FFFF;",
    "&#x110000;",
    "&#xDFFF;",
    "&#65",
    "&#x41",
    "&#X41;",
    "&AMP;",
    "&AMP",
    "&Amp;",
    "&COPY",
    "&eacute",
    "&eacutex",
    "&frac12",
    "&frac123",
    "&not",
    "&notin",
    "&notin;x",
    "&sup1",
    "&yen;",
    "&NotEqualTilde;",
    "&fjlig;",
    "&ThickSpace;",
    "&" + "a" * 32 + ";",
    "&" + "a" * 33 + ";",
    "&amp&lt;",
    "&&amp;",
    "&amp ;",
    "&;",
    "&ä;",
    "&#65;&#66;",
    # What Python 3.13.14's html.parser decides, and a simpler reader wouldn't.
    "a<!-->b-->c",
    "a<!--->b-->c",
    "a<!-- b --!>c",
    "<![CDATA[a>b]]>c",
    "<![cdata[a>b]]>c",
    "<!doctype a>b",
    "<?a>b",
    "<!a>b",
    "x <b it's> y",
    "<title>a&amp;b<i>c</title>d",
    "<textarea>a&amp;b</textarea>c",
    "<xmp>a&amp;b</xmp>c",
    "<xmp>a</xmpx>b",
    "<iframe>a</iframe>b",
    "<noembed>a</noembed>b",
    "<noframes>a</noframes>b",
    "<noscript>a<b>c</b></noscript>d",
    "<script>a</scriptx>b</script>c",
    "<plaintext>a</plaintext>b",
    "<a b=c/>x</i>y",
    '<a b="c"/>x</i>y',
    "<script/>x</script>y",
    "<xmp/>x</xmp>y",
    "<title>a",
    "<!--a",
    "<![CDATA[a",
    "<?a",
    "<!a",
    "</1a>b",
    '<b>x</a y="z>w">v',
    "<a b='c>d",
    "<title>a</title",
    "\x1ca\x1db\x1e",
]

NORMALIZE_TEXT = [
    ["  a \t b\n\n c ", 200],
    ["hello world", 9],
    ["helloworldagain", 9],
    ["\U0001F600\U0001F600", 2],
    ["\U0001F600\U0001F600\U0001F600", 2],
    ["a b", 200],
    ["a\rb", 200],
    ["　wide　", 200],
    ["word " * 60, 200],
    ["x" * 201, 200],
    ["x" * 200, 200],
    ["", 200],
    ["\x1cab\x1f", 200],
]

# --- urls ------------------------------------------------------------------------------------------------------------
URLS = [
    "HTTPS://Example.com:8080/a%20b/c;p=1?q=%C3%A4&r=2#top",
    "https://example.com/a;x/b",
    "https://example.com/a;b;c",
    "https://example.com/a;x/b;y",
    "  https://exa\tmple\n.com/",
    "https://example.com/a;p?q=1#f",
    "http://example.com",
    "https://example.com/",
    "https://example.com/a?#",
    "https://example.com?x=1",
    "https://example.com#frag",
    "https://example.com/?",
    "//example.com/x",
    "ftp://example.com/y;type=i",
    "ftps://example.com/y",
    "https://xn--strung-xxa.de/a",
    "https://xn--strung-xxa.de:8443/a",
    "https://user:pw@example.com/a",
    "http://[::1]:8080/a",
    "https://example.com/a b",
    "https://example.com/ä",
    "https://EXAMPLE.com/A",
    "javascript:alert(1)",
    "data:text/html,hello",
    "file:///etc/passwd",
    "example.com/no-host",
    "mailto:a@example.com",
    "http:///nohost",
    "1http://example.com/x",
]


def parse_url_case(url):
    out = {"input": url}
    try:
        p = urlparse(url)
        out["parts"] = [p.scheme, p.netloc, p.path, p.params, p.query, p.fragment]
    except ValueError as e:
        out["parse_error"] = str(e)
    try:
        r = MainResult(url=url, title="t", content="c")
        r.normalize_result_fields()
        out["normalized"] = r.url
    except (ValueError, UnicodeError) as e:
        out["normalize_error"] = type(e).__name__
    return out


# --- bing ------------------------------------------------------------------------------------------------------------
def bing_url(query, locale, safesearch=0):
    params = {"searxng_locale": locale, "safesearch": safesearch}
    bing.request(query, params)
    return params["url"]


BING_REQUESTS = [
    ["dotnet aot", "all", 0],
    ["dotnet aot", "de-DE", 0],
    ["dotnet aot", "en-US", 0],
    ["dotnet aot", "ru-RU", 0],
    ["dotnet aot", "zh-CN", 0],
    ["dotnet aot", "zh-HK", 0],
    ["dotnet aot", "fr-CH", 1],
    ["c# & d", "all", 2],
    ["äöü", "all", 0],
]

region_tags = set(bing.traits.regions)
languages = sorted({t.split("-")[0] for t in region_tags})
region_tags |= set(languages)
region_tags |= {"all", "xx", "xx-YY", "en-ZZ", "de-ZZ", "de-CH", "de-AT", "fr-XX", "en-DE", "sr-Latn", "sr-Cyrl", "zh-Hans", "zh-Hant", "pt-XX", "es-XX"}
territories = [t for t in babel.core.get_global("territory_languages") if len(t) == 2]
region_tags |= {f"{language}-{territory}" for language in ("en", "de", "fr", "es", "zh", "ar") for territory in territories}
region_tags |= {f"{language}-ZZ" for language in languages}
region_tags |= {i.replace("_", "-") for i in babel.localedata.locale_identifiers()}
region_tags |= set(babel.core.get_global("language_aliases"))
region_tags |= {f"{language}-{alias}" for language in languages for alias in babel.core.get_global("territory_aliases") if len(alias) == 2}
region_tags |= {"EN-gb", "zh-hant", "SR-latn-rs", "zh-MY", "sr-XK", "uz-AF"}
BING_REGIONS = {t: bing.traits.get_region(t, bing.traits.all_locale) for t in sorted(region_tags)}

BING_REDIRECTS = [
    "https://example.com/plain",
    "https://www.bing.com/ck/a?!&&ntb=1",
    "https://www.bing.com/ck/a?u=b1aGk&ntb=1",
    "https://www.bing.com/ck/a?!&&u=a1aHR0cHM6Ly9hLmV4YW1wbGUv&ntb=1",
    "https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9hLmV4YW1wbGU_",
]


def bing_redirect(href):
    page = f'<ol id="b_results"><li class="b_algo"><h2><a href="{href.replace("&", "&amp;")}">t</a></h2></li></ol>'
    return bing.response(FakeResponse(page))[0]["url"]


BING_SNIPPETS = {
    "icon_with_following_text": '<ol id="b_results"><li class="b_algo"><h2><a href="https://a.example/">t</a></h2><p><span class="algoSlug_icon">WEB</span>The snippet</p></li></ol>',
    "two_paragraphs": '<ol id="b_results"><li class="b_algo"><h2><a href="https://a.example/">t</a></h2><p> one </p><p> two </p></li></ol>',
    "entities": '<ol id="b_results"><li class="b_algo"><h2><a href="https://a.example/?x=1&amp;y=2">a &#183; b &amp; c</a></h2><p>Jan&nbsp;&#0183;&#32;text</p></li></ol>',
    "comment_in_title": '<ol id="b_results"><li class="b_algo"><h2><a href="https://a.example/">a<!-- no -->b</a></h2></li></ol>',
    "nested_list_item": '<ol id="b_results"><li class="b_algo"><div><h2><a href="https://a.example/">deep</a></h2></div></li><li class="b_ad"><h2><a href="https://ad.example/">ad</a></h2></li></ol>',
    "no_results": "<html><body>nothing</body></html>",
    "unclosed_markup": '<ol id="b_results"><li class="b_algo"><h2><a href="https://a.example/">t</a></h2><p>one<li class="b_algo"><h2><a href="https://b.example/">u</a></h2><p>two</ol>',
}


# --- duckduckgo ------------------------------------------------------------------------------------------------------
FIRST_PAGE = '<html><head><link id="deep_preload_link" rel="preload" as="script" href="https://links.duckduckgo.com/d.js?q=dotnet&amp;vqd=TOKEN&amp;dp=TOKEN"></head></html>'

calls = []


def fake_get(url, headers=None, **kwargs):
    calls.append({"url": url, "headers": dict(headers or {}), "kwargs": {k: v for k, v in kwargs.items()}})
    return FakeResponse(FIRST_PAGE, params={})


ddg.get = fake_get
ddg.CACHE = FakeCache()

params = {"pageno": 1, "headers": {"Accept-Language": "en-US,en;q=0.9"}, "query": "dotnet native aot"}
ddg.request("dotnet native aot", params)
ddg_flow = {"first_page_url": calls[0]["url"], "api_url": params["url"], "api_headers": params["headers"], "impersonate": params.get("impersonate")}

response_params = {"pageno": 1, "headers": {}, "query": "dotnet native aot"}
ddg.CACHE = FakeCache()
ddg_results = list(ddg.response(FakeResponse(read_fixture("ddg-results.json"), response_params)))
ddg_next = next(iter(ddg.CACHE.values()), None)
ddg_flow["next_page_link"] = ddg_next

over_limit = {"pageno": 1, "headers": {}}
ddg.request("a" * 500, over_limit)
ddg_flow["url_for_500_chars"] = over_limit.get("url")


# --- errors ----------------------------------------------------------------------------------------------------------
ERROR_CASES = [
    [200, "", ""],
    [302, "", ""],
    [402, "", ""],
    [403, "", ""],
    [429, "", ""],
    [404, "", ""],
    [500, "", ""],
    [429, "cloudflare", "x __cf_chl_jschl_tk__= y"],
    [503, "cloudflare", "/cdn-cgi/challenge-platform/ orchestrate/jsch/v1 window._cf_chl_enter("],
    [403, "cloudflare", "__cf_chl_captcha_tk__="],
    [429, "nginx", "__cf_chl_jschl_tk__="],
    [403, "cloudflare", '<span class="cf-error-code">1020</span>'],
    [503, "", 'src="https://www.google.com/recaptcha/api.js"'],
]


class HttpStatusError(Exception):
    pass


def error_case(status, server, text):
    def raise_for_status():
        raise HttpStatusError()

    resp = types.SimpleNamespace(status_code=status, text=text, headers={"Server": server}, raise_for_status=raise_for_status)
    try:
        raise_for_httperror(resp)
        return {"status": status, "server": server, "text": text, "raised": None}
    except HttpStatusError:
        return {"status": status, "server": server, "text": text, "raised": "HTTPError", "suspended_time": None}
    except searx.exceptions.SearxEngineAccessDeniedException as e:
        return {"status": status, "server": server, "text": text, "raised": type(e).__name__, "suspended_time": e.suspended_time}


# --- merging ---------------------------------------------------------------------------------------------------------
def hit(path, title="title", content="content", scheme="https"):
    return {"url": f"{scheme}://example.com/{path}", "title": title, "content": content}


MERGE_SCENARIOS = {
    "one_engine": {"engines": {"a": [1, "general"]}, "extends": [["a", [hit("1"), hit("2"), hit("3"), hit("4")]]]},
    "two_engines_share_a_page": {"engines": {"a": [1, "general"], "b": [1, "general"]}, "extends": [["a", [hit("first"), hit("second"), hit("shared")]], ["b", [hit("shared")]]]},
    "weights_multiply": {"engines": {"a": [2, "general"], "b": [0.5, "general"], "c": [3, "general"]}, "extends": [["a", [hit("light")]], ["b", [hit("light")]], ["c", [hit("heavy")]]]},
    "longer_text_wins": {"engines": {"a": [1, "general"], "b": [1, "general"]}, "extends": [["a", [hit("x", "short", "a long enough snippet")]], ["b", [hit("x", "a longer title", "brief")]]]},
    "http_then_https": {"engines": {"a": [1, "general"], "b": [1, "general"]}, "extends": [["a", [hit("x", scheme="http")]], ["b", [hit("x")]]]},
    "https_then_http": {"engines": {"a": [1, "general"], "b": [1, "general"]}, "extends": [["a", [hit("x")]], ["b", [hit("x", scheme="http")]]]},
    "repeat_within_an_engine": {"engines": {"a": [1, "general"]}, "extends": [["a", [hit("x"), hit("y"), hit("x")]]]},
    "content_equal_to_title": {"engines": {"a": [1, "general"]}, "extends": [["a", [hit("x", "same", "same")]]]},
    "ties_keep_first_seen": {"engines": {"a": [1, "general"], "b": [1, "general"]}, "extends": [["a", [hit("from-a")]], ["b", [hit("from-b")]]]},
    "category_group_pulls_up": {"engines": {"g": [1, "general"], "i": [0.9, "images"]}, "extends": [["g", [hit("g1"), hit("g2")]], ["i", [hit("i1")]]]},
    "group_fills_after_eight": {"engines": {"g": [1, "general"], "i": [0.9, "images"]}, "extends": [["g", [hit(f"g{n}") for n in range(1, 11)]], ["i", [hit("i1")]]]},
    "group_distance_limit": {"engines": {"g": [1, "general"], "h": [0.99, "other"], "i": [0.98, "images"]}, "extends": [["g", [hit(f"g{n}") for n in range(1, 31)]], ["h", [hit(f"h{n}") for n in range(1, 31)]], ["i", [hit(f"i{n}") for n in range(1, 31)]]]},
    "query_and_fragment_differ": {"engines": {"a": [1, "general"]}, "extends": [["a", [{"url": "https://example.com/x", "title": "t", "content": "c"}, {"url": "https://example.com/x?a=1", "title": "t", "content": "c"}, {"url": "https://example.com/x#part", "title": "t", "content": "c"}, {"url": "https://www.example.com/x", "title": "t", "content": "c"}, {"url": "https://example.com/x/", "title": "t", "content": "c"}]]]},
    "text_normalized": {"engines": {"a": [1, "general"]}, "extends": [["a", [{"url": "https://example.com/x", "title": "  a \t b\n c ", "content": "x" * 1300}]]]},
}


def run_scenario(scenario):
    saved = dict(ENGINES)
    try:
        for name, (weight, category) in scenario["engines"].items():
            ENGINES[name] = types.SimpleNamespace(name=name, weight=weight, categories=[category], paging=False, display_error_messages=True, timeout=3.0)
        searx.metrics.initialize(list(ENGINES))
        container = ResultContainer()
        for engine, results in scenario["extends"]:
            container.extend(engine, [dict(r) for r in results])
        container.close()
        return [merged(r) for r in container.get_ordered_results()]
    finally:
        ENGINES.clear()
        ENGINES.update(saved)


for scenario in MERGE_SCENARIOS.values():
    scenario["expected"] = run_scenario(scenario)

# --- brave ----------------------------------------------------------------------------------------
def brave_request(query, locale, page, safesearch):
    params = {"searxng_locale": locale, "pageno": page, "safesearch": safesearch, "time_range": None, "cookies": {}, "headers": {}}
    brave.request(query, params)
    return {"query": query, "locale": locale, "page": page, "safesearch": safesearch, "url": params["url"], "cookies": params["cookies"]}


def brave_page(data):
    return f"<script>kit.start(app, element, {{ node_ids: [0, 19], data: [{data}], form: null }});</script>"


def brave_hit(title, url, description):
    return f'{{title:{title},url:{url},description:{description}}}'


def brave_results(results):
    return f'{{type:"data",data:{{body:{{response:{{web:{{results:[{results}]}}}}}}}}}}'


BRAVE_FIRST = '{type:"data",data:{}}'
BRAVE_SNIPPETS = {
    "plain_objects": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit('"Plain"', '"https://a.example/"', '"The <strong>snippet</strong>"'))),
    "function_form": brave_page(BRAVE_FIRST + ",(function(a,b){return " + brave_results(brave_hit("a", '"https://a.example/"', "b")) + '}("Named","Bound"))'),
    "function_called_outside": brave_page(BRAVE_FIRST + ",(function(a){return " + brave_results(brave_hit("a", '"https://a.example/"', '""')) + '})("Outside")'),
    "both_elements_functions": brave_page('(function(x){return {type:"data",data:{v:x}}}(1)),(function(a){return ' + brave_results(brave_hit("a", '"https://a.example/"', '""')) + '}("Second"))'),
    "escapes": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit(r'"a\"b<c\x41\n"', '"https://a.example/"', r"'it\'s <b>bold</b> \\ done'"))),
    "backticks": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit("`tick`", '"https://a.example/"', '`a "quoted" word`'))),
    "brackets_in_strings": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit('"a]b}c)"', '"https://a.example/"', '"x{y[z"'))),
    "void_description": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit('"t"', '"https://a.example/"', "void 0"))),
    "numbers_and_flags": brave_page(BRAVE_FIRST + ',{type:"data",data:{body:{response:{web:{count:.5,neg:-1,exp:1e3,yes:true,no:false,nil:null,undef:undefined,list:[1,2,],results:[' + brave_hit('"t"', '"https://a.example/"', '"c"') + ",],},},},},}"),
    "several": brave_page(BRAVE_FIRST + "," + brave_results(brave_hit('"one"', '"https://a.example/1"', '"1"') + "," + brave_hit('"two"', '"https://a.example/2"', '"2"'))),
    "no_results_flag": brave_page(BRAVE_FIRST + ',{type:"data",data:{noResults:true}}'),
    "web_empty": brave_page(BRAVE_FIRST + ',{type:"data",data:{body:{response:{web:{}}}}}'),
    "web_null": brave_page(BRAVE_FIRST + ',{type:"data",data:{body:{response:{web:null}}}}'),
    "empty_results": brave_page(BRAVE_FIRST + "," + brave_results("")),
}
BRAVE_BAD_PAGES = {
    "no_data": "<html>nothing here</html>",
    "one_element": brave_page(BRAVE_FIRST),
    "no_body": brave_page(BRAVE_FIRST + ',{type:"data",data:{}}'),
    "no_web": brave_page(BRAVE_FIRST + ',{type:"data",data:{body:{response:{}}}}'),
    "unbalanced": "<script>data: [{type:</script>",
}


def main_only(results):
    # Brave's video results and suggestions come back with its main results. The port leaves them out.
    return [plain(r) for r in results if type(r) is MainResult]


def brave_snippet(page):
    try:
        return {"input": page, "output": main_only(brave.response(FakeResponse(page)))}
    except ValueError as e:
        return {"input": page, "error": type(e).__name__ + ": " + str(e)}


def fails(call):
    try:
        call()
    except Exception as e:  # pylint: disable=broad-except
        return type(e).__name__
    return None


# --- startpage ------------------------------------------------------------------------------------
STARTPAGE_HOME = '<html><form id="search" action="/sp/search"><input type="hidden" name="sc" value="CODE&amp;123"></form></html>'
startpage.get = lambda url, headers=None, **kwargs: FakeResponse(STARTPAGE_HOME, url=url)
startpage.CACHE = FakeCache()


def startpage_request(query, locale, page, safesearch):
    params = {"searxng_locale": locale, "pageno": page, "safesearch": safesearch, "time_range": None, "cookies": {}, "headers": {}}
    startpage.CACHE.clear()
    startpage.request(query, params)
    return {
        "query": query, "locale": locale, "page": page, "safesearch": safesearch, "url": params["url"], "method": params["method"],
        "form": [[k, v] for k, v in params["data"].items()], "preferences": params["cookies"]["preferences"], "origin": params["headers"]["Origin"], "referer": params["headers"]["Referer"],
    }


def startpage_page(mainline):
    return "<script>React.createElement(UIStartpage.AppSerpWeb, " + json.dumps({"render": {"presenter": {"regions": {"mainline": mainline}}}}) + ")</script>"


def startpage_hit(description, title="A <b>title</b>", url="https://a.example/"):
    return {"clickUrl": url, "title": title, "description": description}


STARTPAGE_SNIPPETS = {
    "date_prefix": startpage_page([{"display_type": "web-google", "results": [startpage_hit("2 Sep 2014 ... The snippet")]}]),
    "date_prefix_31": startpage_page([{"display_type": "web-google", "results": [startpage_hit("31 Dec 2024 ... More ... text")]}]),
    "days_ago": startpage_page([{"display_type": "web-google", "results": [startpage_hit("5 days ago ... The snippet")]}]),
    "one_day_ago": startpage_page([{"display_type": "web-google", "results": [startpage_hit("1 day ago ... The snippet")]}]),
    "not_a_date": startpage_page([{"display_type": "web-google", "results": [startpage_hit("32 Sep 2014 ... Stays")]}]),
    "not_at_start": startpage_page([{"display_type": "web-google", "results": [startpage_hit("On 2 Sep 2014 ... Stays")]}]),
    "no_description": startpage_page([{"display_type": "web-google", "results": [{"clickUrl": "https://a.example/", "title": "t"}]}]),
    "bold_and_entities": startpage_page([{"display_type": "web-google", "results": [startpage_hit("<b>Python&#39;s</b> &amp; more")]}]),
    "adverts_left_out": startpage_page([{"display_type": "ads-google-top", "results": [startpage_hit("advert")]}, {"display_type": "web-google", "results": [startpage_hit("real")]}]),
    "no_mainline": "<script>React.createElement(UIStartpage.AppSerpWeb, " + json.dumps({"render": {"presenter": {"regions": {}}}}) + ")</script>",
    "no_results": startpage_page([{"display_type": "web-google", "results": []}]),
}


# --- wikipedia ------------------------------------------------------------------------------------
WIKIPEDIA_REQUESTS = [
    ("rust", "all"), ("rust", "en-US"), ("rust", "de-DE"), ("Rust", "de"), ("it's", "en"), ("native aot", "en"), ("Native aot", "en"), ("c# language", "en"), ("a1b c2d", "en"),
    ("straße", "de"), ("ñandú", "es"), ("日本語", "ja"), ("123", "en"), ("a-b_c", "en"), ("AC/DC", "en"), ("rust", "zh-TW"), ("rust", "zh-CN"), ("rust", "sr-Latn"),
    ("rust", "xx"), ("rust", "pt-BR"), ("rust", "en-GB"),
]


def wikipedia_url(query, locale):
    params = {"searxng_locale": locale}
    wikipedia.request(query, params)
    return {"query": query, "locale": locale, "url": params["url"]}


def wiki_summary(**fields):
    return json.dumps({"content_urls": {"desktop": {"page": "https://en.wikipedia.org/wiki/X"}}, **fields})


WIKIPEDIA_CASES = {
    "standard": (200, wiki_summary(type="standard", title="X", titles={"display": "<span>X</span> <i>y</i>"}, description="The description", extract="Extract")),
    "disambiguation": (200, wiki_summary(type="disambiguation", title="X", titles={"display": "X"}, description="Topics")),
    "no_display_title": (200, wiki_summary(type="standard", title="Plain &amp; title", description="d")),
    "empty_display_title": (200, wiki_summary(type="standard", title="Fallback", titles={"display": ""}, description="d")),
    "no_description": (200, wiki_summary(type="standard", title="X", titles={"display": "X"})),
    "not_found": (404, '{"status":404,"type":"Internal error"}'),
    "invalid_title": (400, '{"type":"https://mediawiki.org/wiki/HyperSwitch/errors/bad_request","detail":"title-invalid-characters"}'),
}
WIKIPEDIA_FAILS = {
    "other_bad_request": (400, '{"type":"https://mediawiki.org/wiki/HyperSwitch/errors/bad_request","detail":"other"}'),
    "too_many": (429, "{}"),
    "server_error": (500, "{}"),
    "no_link": (200, '{"type":"standard","title":"X"}'),
}


# --- locales for the newer engines ----------------------------------------------------------------
# Keys with an underscore are engine-side names, such as pt_BR. They are not locales a search can ask for, and Babel rejects them.
LOCALE_SAMPLE = sorted(t for t in (set(sorted(BING_REGIONS)[::7]) | set(brave.traits.regions) | set(brave.traits.custom["ui_lang"]) | set(startpage.traits.regions) | set(startpage.traits.languages) | set(wikipedia.traits.regions) | set(wikipedia.traits.languages) | {"all", "xx", "zh-Hans", "zh-Hant", "sr-Latn", "de-CH", "en-DE", "ar-AD", "fr-XX", "pt-BR"}) if "_" not in t)
LOCALE_TABLES = {
    "brave_regions": {t: brave.traits.get_region(t, "all") for t in LOCALE_SAMPLE},
    "brave_interface": {t: searx.locales.get_engine_locale(t, brave.traits.custom["ui_lang"], "en-us") for t in LOCALE_SAMPLE},
    "startpage_regions": {t: startpage.traits.get_region(t, "en_US") for t in LOCALE_SAMPLE},
    "startpage_languages": {t: startpage.traits.get_language(t, "english") for t in LOCALE_SAMPLE},
    "wikipedia_hosts": {t: wikipedia.get_wiki_params(t, wikipedia.traits)[1] for t in LOCALE_SAMPLE},
}

searx.metrics.initialize(list(ENGINES))
bing_results =bing.response(FakeResponse(read_fixture("bing-results.html")))
container = ResultContainer()
container.extend("bing", bing.response(FakeResponse(read_fixture("bing-results.html"))))
container.extend("duckduckgo web", list(ddg.response(FakeResponse(read_fixture("ddg-results.json"), dict(response_params)))))
container.close()
merged_fixtures = [merged(r) for r in container.get_ordered_results()]

# ---------------------------------------------------------------------------------------------------------------------
golden = {
    "python_version": sys.version.split()[0],
    "babel_version": babel.__version__,
    "searxng_commit": subprocess.run(["git", "-C", SEARXNG, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip(),
    "settings": {
        "ban_time_on_fail": get_setting("search.ban_time_on_fail"),
        "max_ban_time_on_fail": get_setting("search.max_ban_time_on_fail"),
        "request_timeout": get_setting("outgoing.request_timeout"),
        "bing": {"categories": bing.categories, "paging": bing.paging, "timeout": bing.timeout, "weight": getattr(bing, "weight", 1)},
        "duckduckgo": {"categories": ddg.categories, "paging": ddg.paging, "timeout": ddg.timeout, "weight": getattr(ddg, "weight", 1)},
        **{n: {"categories": e.categories, "paging": e.paging, "timeout": e.timeout, "weight": getattr(e, "weight", 1.0)} for n, e in (("brave", brave), ("startpage", startpage), ("wikipedia", wikipedia))},
    },
    "html_to_text": [{"input": s, "output": html_to_text(s)} for s in HTML_TO_TEXT],
    "normalize_text": [{"input": s, "max": m, "output": _normalize_text_field(s, "title", m)} for s, m in NORMALIZE_TEXT],
    "urls": [parse_url_case(u) for u in URLS],
    "quote_plus": [{"input": s, "output": quote_plus(s)} for s in ["rust async", "a_b.c-d~e", "c# & d=1", "ä", "a/b?c", "!*'()", "\U0001F600"]],
    "parse_qs_u": [{"input": q, "output": parse_qs(q).get("u", [None])[0]} for q in ["a=1&u=two&u=three", "u=&u=second", "u=a%20b+c", "!&&u=x&ntb=1", "a=1", ""]],
    "bing_requests": [{"query": q, "locale": l, "safesearch": s, "url": bing_url(q, l, s)} for q, l, s in BING_REQUESTS],
    "bing_regions": BING_REGIONS,
    "bing_redirects": [{"input": h, "output": bing_redirect(h)} for h in BING_REDIRECTS],
    "bing_snippets": {k: {"input": v, "output": [plain(r) for r in bing.response(FakeResponse(v))]} for k, v in BING_SNIPPETS.items()},
    "bing_fixture": [plain(r) for r in bing_results],
    "ddg_fixture": [plain(r) for r in ddg_results],
    "ddg_flow": ddg_flow,
    "errors": [error_case(*c) for c in ERROR_CASES],
    "brave_requests": [brave_request(q, l, p, s) for q, l, p, s in [("dotnet aot", "all", 1, 0), ("c# & d", "de-DE", 2, 1), ("x", "en-US", 10, 2), ("x", "en-GB", 1, 0), ("x", "pt-BR", 1, 0), ("x", "xx", 1, 0)]],
    "brave_fixture": main_only(brave.response(FakeResponse(read_fixture("brave-results.html")))),
    "brave_snippets": {k: brave_snippet(v) for k, v in BRAVE_SNIPPETS.items()},
    "brave_bad_pages": {k: {"input": v, "error": fails(lambda v=v: brave.response(FakeResponse(v)))} for k, v in BRAVE_BAD_PAGES.items()},
    "startpage_requests": [startpage_request(q, l, p, s) for q, l, p, s in [("dotnet aot", "all", 1, 0), ("c# & d", "de-DE", 2, 1), ("x", "en-US", 18, 2), ("x", "fr-CA", 1, 0), ("x", "xx", 1, 0)]],
    "startpage_fixture": [plain(r) for r in startpage.response(FakeResponse(read_fixture("startpage-results.html")))],
    "startpage_snippets": {k: {"input": v, "output": [plain(r) for r in startpage.response(FakeResponse(v))]} for k, v in STARTPAGE_SNIPPETS.items()},
    "startpage_no_marker_error": fails(lambda: startpage.response(FakeResponse("<html>nothing</html>"))),
    "wikipedia_requests": [wikipedia_url(q, l) for q, l in WIKIPEDIA_REQUESTS],
    "wikipedia_fixture": [plain(r) for r in wikipedia.response(FakeResponse(read_fixture("wikipedia-summary.json")))],
    "wikipedia_cases": {k: {"status": s, "input": v, "output": [plain(r) for r in wikipedia.response(FakeResponse(v, status_code=s))]} for k, (s, v) in WIKIPEDIA_CASES.items()},
    "wikipedia_fails": {k: {"status": s, "input": v, "error": fails(lambda s=s, v=v: wikipedia.response(FakeResponse(v, status_code=s)))} for k, (s, v) in WIKIPEDIA_FAILS.items()},
    "locale_tables": LOCALE_TABLES,
    "merge_scenarios": MERGE_SCENARIOS,
    "merged_fixtures": merged_fixtures,
}

os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
with open(OUTPUT, "w", encoding="utf8", newline="\n") as f:
    json.dump(golden, f, indent=1, ensure_ascii=True)
    f.write("\n")
print("sections:", {k: len(v) if hasattr(v, "__len__") else v for k, v in golden.items()})
