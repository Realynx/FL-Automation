# Edit and publish documentation

The Markdown in `docs/` is the source for this wiki-style documentation site.
You can read and edit it on GitHub, or preview it locally with Material for MkDocs.
The static site adds search, navigation, light/dark themes, copy buttons and page
anchors. It does not execute C# or Python examples or connect to FL Studio.

## Preview locally

Run these commands from the repository root with Python 3.12 or later:

```powershell
python -m venv .docs-venv
.docs-venv\Scripts\python -m pip install -r requirements-docs.txt
.docs-venv\Scripts\python -m mkdocs serve
```

Open the local address printed by MkDocs. Pages reload when Markdown changes.
On macOS/Linux, replace `.docs-venv\Scripts\python` with
`.docs-venv/bin/python`. FL Studio, .NET and the native bridge are not needed
to build the documentation.

## Check a change

```powershell
.docs-venv\Scripts\python -m mkdocs build --strict
.docs-venv\Scripts\python scripts/check_docs.py
```

The strict build checks Markdown links and configuration. The second command
checks the generated HTML's local links, anchors, assets and search index,
including compatibility with a GitHub Pages repository subpath. It does not
test external websites or run the FL examples. The `site/` directory is generated
and ignored by Git; use an HTTP server to preview it rather than opening HTML
files directly.

## Add or update a page

1. Put Markdown under `docs/`, with one descriptive H1 and short task-focused sections.
2. Add the page to `nav` in `mkdocs.yml` and link it from a related guide.
3. Use relative `.md` links for other documentation. Link source files relative to
   the current Markdown file; `scripts/docs_hooks.py` converts links outside `docs/`
   into GitHub source URLs when building the site. Missing source paths fail the build.
4. Keep examples grounded in the current source. Explain prerequisites, expected
   output, units and indexes, and whether a snippet changes the project.
5. Keep current capabilities separate from historical test evidence or planned work.
6. Run both checks above and inspect navigation, search and mobile layout.

Keep the README short: project purpose, prerequisites, a first step, documentation
entry points and license. Detailed installation, troubleshooting and API examples
belong in these pages. Python IDE documentation should continue to describe its
known limits while the IDE is under development.

## Publish on GitHub Pages

The `Documentation` workflow builds and checks every pull request and push to
`master`. Its `documentation-preview` artifact contains the static site.
Deployment is enabled separately so the build works before Pages is configured.

For the first publication:

1. Merge the documentation changes into `master`.
2. Open the repository's **Settings → Pages** and choose **GitHub Actions** as the
   source. The `github-pages` environment must permit deployment from `master`.
3. Under **Settings → Secrets and variables → Actions → Variables**, set repository
   variable `DOCS_PUBLISH` to `true`.
4. Run **Actions → Documentation → Run workflow** on `master`.
5. Open the deployment URL shown by the workflow and test a guide and search.
   Then point the README's primary documentation link and the Wiki Home link at
   that published URL; keep relative Markdown links as a source-browsing fallback.

Later pushes to `master` publish automatically. Pull requests only build previews
and never deploy. The URL comes from `site_url` in `mkdocs.yml`; update it if the
repository is renamed or a custom domain is added. A URL in the configuration
does not mean a site has already been published.

See GitHub's [publishing-source instructions](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site)
and [custom Pages workflows](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages).

## Connect the GitHub Wiki

GitHub Wiki uses its own Git repository. The `wiki/Home.md` file in this source
repository is a ready-to-copy landing page for that Wiki. After enabling the Wiki,
create its Home page and paste that Markdown, or copy it into a checkout of the
repository's `.wiki.git` repository and commit it there.

The landing page links to the documentation source, which works before Pages is
published. Once the site is live, add its published URL as the primary link.
Keep detailed guides here in `docs/` so code reviews and version history stay
together; the Wiki serves as another way to find them. The docs workflow does not
write to the Wiki or require a personal access token.

## Upgrade the documentation tools

`requirements-docs.in` lists direct dependencies; `requirements-docs.txt` pins the
resolved build environment. After changing a direct version, regenerate it with
`uv pip compile requirements-docs.in -o requirements-docs.txt`, reinstall, then
build and check both repositories before adopting the update.
