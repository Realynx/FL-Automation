"""Read-only discovery and inventory for locally installed Serum preset libraries."""

from __future__ import annotations

import os
import sqlite3
from contextlib import closing
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable, Iterator

PRESET_EXTENSIONS = frozenset({".serumpreset", ".fxp"})


@dataclass(frozen=True, slots=True)
class PresetFile:
    """A preset found on disk. No preset payload is opened or copied."""

    root: Path
    path: Path
    relative_path: Path
    format: str
    size_bytes: int

    @property
    def name(self) -> str:
        """The preset name as shown in Serum's browser (file stem)."""
        return self.path.stem

    @property
    def folder(self) -> str:
        """Folder relative to ``<root>/Presets`` with POSIX separators."""
        parts = self.relative_path.parts
        inner = parts[1:-1] if parts and parts[0] == "Presets" else parts[:-1]
        return "/".join(inner)

    def to_dict(self) -> dict[str, Any]:
        """JSON-friendly view (slotted dataclasses do not support ``vars``)."""
        data = {key: (str(value) if isinstance(value, Path) else value) for key, value in asdict(self).items()}
        data.update(name=self.name, folder=self.folder)
        return data


@dataclass(frozen=True, slots=True)
class IndexedPreset:
    """Descriptive metadata read from Serum 2's local SQLite search index."""

    name: str
    location: str
    file_ext: str | None
    category: str | None
    author: str
    description: str | None
    comment: str | None
    tags: tuple[str, ...]
    rating: int | None

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["tags"] = list(self.tags)
        return data


def discover_serum_roots(
    *,
    home: Path | None = None,
    extra_roots: Iterable[Path | str] = (),
) -> tuple[Path, ...]:
    """Return existing Serum 2/Serum library roots in deterministic priority order.

    Explicit roots are checked first, followed by the standard Xfer directories in
    the user's Documents folder. A candidate must contain a ``Presets`` directory.
    """

    base = Path(home) if home is not None else Path.home()
    candidates = [Path(value).expanduser() for value in extra_roots]
    env_root = os.environ.get("SERUM_PRESETS_PATH")
    if env_root:
        candidates.append(Path(env_root).expanduser())
    candidates.extend(
        (
            base / "Documents" / "Xfer" / "Serum 2 Presets",
            base / "Documents" / "Xfer" / "Serum Presets",
        )
    )

    found: list[Path] = []
    seen: set[str] = set()
    for candidate in candidates:
        candidate = candidate.resolve()
        key = os.path.normcase(str(candidate))
        if key not in seen and (candidate / "Presets").is_dir():
            seen.add(key)
            found.append(candidate)
    return tuple(found)


def iter_presets(root: Path | str) -> Iterator[PresetFile]:
    """Yield preset file facts without interpreting proprietary preset payloads."""

    root_path = Path(root).expanduser().resolve()
    preset_dir = root_path / "Presets"
    if not preset_dir.is_dir():
        raise FileNotFoundError(f"Serum preset directory not found: {preset_dir}")
    for path in sorted(preset_dir.rglob("*"), key=lambda item: str(item).casefold()):
        if path.is_file() and path.suffix.casefold() in PRESET_EXTENSIONS:
            stat = path.stat()
            yield PresetFile(
                root=root_path,
                path=path,
                relative_path=path.relative_to(root_path),
                format=path.suffix.removeprefix("."),
                size_bytes=stat.st_size,
            )


def query_index(
    root: Path | str,
    *,
    text: str | None = None,
    category: str | None = None,
    tags: Iterable[str] = (),
    limit: int = 100,
) -> tuple[IndexedPreset, ...]:
    """Query Serum 2's descriptive preset index through a read-only connection.

    This treats the database as optional, undocumented local metadata. Schema or
    access failures are reported to the caller and never trigger a file mutation.
    """

    if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1 or limit > 10_000:
        raise ValueError("limit must be between 1 and 10000")
    db_path = Path(root).expanduser().resolve() / "System" / "presets.db"
    if not db_path.is_file():
        raise FileNotFoundError(f"Serum 2 preset index not found: {db_path}")

    clauses: list[str] = []
    parameters: list[object] = []
    if text:
        clauses.append(
            "(p.name LIKE ? ESCAPE '\\' OR p.description LIKE ? ESCAPE '\\' "
            "OR p.comment LIKE ? ESCAPE '\\' OR p.author LIKE ? ESCAPE '\\')"
        )
        needle = f"%{_escape_like(text)}%"
        parameters.extend((needle, needle, needle, needle))
    if category:
        clauses.append("p.category = ? COLLATE NOCASE")
        parameters.append(category)
    requested_tags = tuple(dict.fromkeys(tag.strip() for tag in tags if tag.strip()))
    for tag in requested_tags:
        clauses.append(
            "EXISTS (SELECT 1 FROM Presets_Tags pt2 JOIN Tags t2 ON t2.tag_id=pt2.tag_id "
            "WHERE pt2.preset_id=p.preset_id AND t2.name=? COLLATE NOCASE)"
        )
        parameters.append(tag)
    where = " WHERE " + " AND ".join(clauses) if clauses else ""
    sql = (
        "SELECT p.name,l.location,p.file_ext,p.category,p.author,p.description,p.comment,"
        "(SELECT GROUP_CONCAT(ordered.name, char(31)) FROM "
        "(SELECT t.name FROM Presets_Tags pt JOIN Tags t ON t.tag_id=pt.tag_id "
        "WHERE pt.preset_id=p.preset_id ORDER BY t.name COLLATE NOCASE) ordered),u.rating "
        "FROM Presets p JOIN Locations l ON l.location_id=p.location_id "
        "LEFT JOIN UserData u ON u.hash=p.hash"
        + where
        + " ORDER BY p.category COLLATE NOCASE,p.name COLLATE NOCASE LIMIT ?"
    )
    parameters.append(limit)

    uri = db_path.as_uri() + "?mode=ro"
    with closing(sqlite3.connect(uri, uri=True)) as connection:
        connection.execute("PRAGMA query_only=ON")
        rows = connection.execute(sql, parameters).fetchall()
    return tuple(
        IndexedPreset(
            name=row[0],
            location=row[1],
            file_ext=row[2],
            category=row[3],
            author=row[4],
            description=row[5],
            comment=row[6],
            tags=tuple(row[7].split("\x1f")) if row[7] else (),
            rating=row[8],
        )
        for row in rows
    )


def _escape_like(value: str) -> str:
    """Treat SQLite LIKE metacharacters as literal search text."""

    return value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
