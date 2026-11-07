# SPDX-License-Identifier: AGPL-3.0-or-later
"""Reduces saved pages from Brave, Startpage and Wikipedia to what the engines parse, for use as test fixtures.

Keeps only the results, so a page's session tokens, location and interface text don't end up in the repository.

usage: make_fixtures.py <searxng_checkout> <bodies_dir> <fixtures_dir>
bodies_dir holds brave.body, sp-dotnet.body and wiki-Rust.body
"""
import json
import os
import re
import sys
import warnings

SEARXNG, BODIES, FIXTURES = sys.argv[1:4]
sys.path.insert(0, SEARXNG)
os.environ.setdefault("SEARXNG_SECRET", "fixture-generation-only")
warnings.simplefilter("ignore")

from searx.engines.brave import IIFE_RE  # noqa: E402
from searx.utils import js_find_matching_bracket, js_find_variable_positions, js_split_args  # noqa: E402

RESULTS_KEPT = 10


def read(name):
    with open(os.path.join(BODIES, name), encoding="utf8") as f:
        return f.read()


def write(name, text):
    with open(os.path.join(FIXTURES, name), "w", encoding="utf8", newline="\n") as f:
        f.write(text)


def brave():
    """Keeps the page's real form: a function called with the values its results refer to by name."""
    page = read("brave.body")
    array_start = page.index("data: [") + len("data: [") - 1
    array_end = js_find_matching_bracket(page, array_start, "[", "]")
    source = page[array_start + 1 : array_end]

    # The second element of the array holds the results.
    first = IIFE_RE.match(source)
    position = source.index("(", js_find_matching_bracket(source, first.start("body"), "{", "}"))
    position = js_find_matching_bracket(source, position, "(", ")") + 1
    while source[position] in " \t\n\r,)":
        position += 1

    match = IIFE_RE.match(source, position)
    body_end = js_find_matching_bracket(source, match.start("body"), "{", "}")
    call_start = source.index("(", body_end)
    call_end = js_find_matching_bracket(source, call_start, "(", ")")
    params = [p.strip() for p in match["params"].split(",") if p.strip()]
    args = js_split_args(source[call_start + 1 : call_end])
    body = source[match.start("body") : body_end + 1]

    start = body.index("results:[", body.index("web:{")) + len("results:")
    end = js_find_matching_bracket(body, start, "[", "]")
    results = body[start : end + 1]

    # Cut the list down, keeping each element as written.
    kept, position = [], 1
    while results[position] == "{" and len(kept) < RESULTS_KEPT:
        stop = js_find_matching_bracket(results, position, "{", "}")
        kept.append(results[position : stop + 1])
        position = stop + 1
        if results[position] == ",":
            position += 1
    results = "[" + ",".join(kept) + "]"

    used = {name for name, _ in js_find_variable_positions(results)}
    pairs = [(p, a) for p, a in zip(params, args) if p in used]
    reduced = (
        f"(function({','.join(p for p, _ in pairs)}){{return {{type:\"data\",data:{{body:{{response:{{web:{{results:{results}}}}}}}}},uses:{{}}}}}}"
        f"({','.join(a for _, a in pairs)}))"
    )
    return f'<!doctype html>\n<script>\nkit.start(app, element, {{\n  node_ids: [0, 19],\n  data: [{{type:"data",data:{{}},uses:{{}}}},{reduced}],\n  form: null\n}});\n</script>\n'


def startpage():
    page = read("sp-dotnet.body")
    marker = "React.createElement(UIStartpage.AppSerpWeb, "
    start = page.index(marker) + len(marker)
    end = page.index("}})", start)
    regions = json.loads(page[start : end + 2])["render"]["presenter"]["regions"]

    kept = []
    for region in regions["mainline"]:
        if region["display_type"] == "web-google":
            items = [{k: r.get(k) for k in ("clickUrl", "title", "description")} for r in region["results"]]
            kept.append({"display_type": "web-google", "results": items})
        elif region["display_type"] == "ads-google-top":
            # An advert, reduced, to show adverts are left out.
            kept.append({"display_type": "ads-google-top", "results": [{"clickUrl": "https://ads.example/", "title": "An advert", "description": "Sponsored"}]})

    data = {"render": {"presenter": {"regions": {"mainline": kept}}}}
    return f"<!doctype html>\n<script>\nReact.createElement(UIStartpage.AppSerpWeb, {json.dumps(data, ensure_ascii=False)})\n</script>\n"


def wikipedia():
    data = json.loads(read("wiki-Rust.body"))
    keep = ("type", "title", "displaytitle", "titles", "content_urls", "description", "extract")
    return json.dumps({k: data[k] for k in keep if k in data}, ensure_ascii=False, indent=1) + "\n"


write("brave-results.html", brave())
write("startpage-results.html", startpage())
write("wikipedia-summary.json", wikipedia())
