"""Check built-site links, anchors and search data without network access."""

import json
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.feed(path.read_text(encoding="utf-8"))

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if "id" in attributes:
            self.ids.add(attributes["id"])
        for key in ("href", "src"):
            if key in attributes:
                self.links.append(attributes[key])


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "site").resolve()
    pages = {path.resolve(): Page(path) for path in root.rglob("*.html")}
    failures = []
    if root / "index.html" not in pages:
        failures.append("Site has no index.html; run mkdocs build first.")
    for path, page in pages.items():
        if path.name == "404.html":
            continue  # The 404 page intentionally uses site-root URLs.
        for link in page.links:
            url = urlsplit(link)
            if url.scheme or url.netloc:
                continue
            if url.path.startswith("/"):
                failures.append(f"{path.relative_to(root)}: unexpected root-relative link {link}")
                continue
            target = (path.parent / unquote(url.path)).resolve() if url.path else path
            if target.is_dir():
                target /= "index.html"
            if not target.is_relative_to(root) or not target.exists():
                failures.append(f"{path.relative_to(root)}: missing {link}")
            elif url.fragment and target in pages and unquote(url.fragment) not in pages[target].ids:
                failures.append(f"{path.relative_to(root)}: missing anchor {link}")
    search = root / "search/search_index.json"
    if not search.exists() or not json.loads(search.read_text(encoding="utf-8")).get("docs"):
        failures.append("Search index is missing or empty.")
    if failures:
        print("\n".join(sorted(set(failures))))
        return 1
    print(f"Validated {len(pages)} HTML pages, local links, anchors, assets and search index.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
