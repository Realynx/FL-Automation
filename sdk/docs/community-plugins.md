# Community plugin catalog

`community-plugins.json` (repo root, default branch) is the list of supported
community plugins. Both FruityLink installers — the open-source
`fruitylink-installer` released here and the packaged FL Automate installer —
fetch this file at install time and offer each entry as an optional checkbox,
downloading the plugin into `FL Studio\FruityLink\plugins\<id>\` when checked.

## Entry format

```json
{
  "version": 1,
  "plugins": [
    {
      "id": "my-plugin",
      "name": "My Plugin",
      "description": "One-line description shown in the installer.",
      "version": "1.0.0",
      "downloadUrl": "https://github.com/you/my-plugin/releases/download/v1.0.0/my-plugin.zip"
    }
  ]
}
```

Rules the installer enforces (entries that break them are silently skipped):

- `id` — letters, digits, `.`, `_`, `-` only (it becomes the folder name under
  `FruityLink\plugins\`), max 64 chars, unique across the list.
- `downloadUrl` — must be HTTPS and point to a **zip** containing the plugin
  DLL (built against `FruityLink.Plugins.Abstractions`) plus its dependencies.
  A single wrapping top-level folder inside the zip is fine — the installer
  unwraps it.
- `name` — required; `description` and `version` are optional but recommended.

## Getting your plugin listed

Open a pull request that adds your entry to `community-plugins.json`. Keep the
download URL on a tagged GitHub release so the bits are immutable, and note
what the plugin does in the PR description so it can be reviewed and verified
in FL Studio before merging.
