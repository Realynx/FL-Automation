"""Keep repository-relative source links useful in the published documentation."""

import re
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit

from mkdocs.exceptions import PluginError


def on_page_markdown(markdown, page, config, files):
    """Map links outside docs/ to GitHub without changing checked-in Markdown."""
    docs = Path(config.docs_dir).resolve()
    root = docs.parent
    source = Path(page.file.abs_src_path).parent
    repository = config.repo_url.rstrip("/")

    def replace_link(match):
        destination = match.group(2)
        url = urlsplit(destination)
        if url.scheme or url.netloc or not url.path or url.path.startswith("/"):
            return match.group(0)
        target = (source / unquote(url.path)).resolve()
        if target.is_relative_to(docs):
            return match.group(0)
        if not target.is_relative_to(root) or not target.exists():
            raise PluginError(f"{page.file.src_uri}: missing repository link {destination}")
        kind = "tree" if target.is_dir() else "blob"
        path = quote(target.relative_to(root).as_posix(), safe="/")
        suffix = f"#{url.fragment}" if url.fragment else ""
        return f"{match.group(1)}{repository}/{kind}/master/{path}{suffix}{match.group(3)}"

    # Fenced examples may contain literal Markdown; leave their contents intact.
    chunks = re.split(r"(?ms)(^\s{0,3}(?:`{3,}|~{3,}).*?^\s{0,3}(?:`{3,}|~{3,})\s*$)", markdown)
    for index in range(0, len(chunks), 2):
        chunks[index] = re.sub(r"(\]\()([^\s)]+)(\))", replace_link, chunks[index])
    return "".join(chunks)
