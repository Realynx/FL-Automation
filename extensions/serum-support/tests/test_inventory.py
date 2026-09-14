import sqlite3
from pathlib import Path

import pytest

from fruitylink_serum.inventory import discover_serum_roots, iter_presets, query_index


def test_discovery_and_inventory_are_bounded_to_valid_roots(tmp_path: Path) -> None:
    serum2 = tmp_path / "Documents" / "Xfer" / "Serum 2 Presets"
    presets = serum2 / "Presets" / "Factory" / "Chord"
    presets.mkdir(parents=True)
    (presets / "Bright.SerumPreset").write_bytes(b"opaque")
    (presets / "notes.txt").write_text("ignore me")

    assert discover_serum_roots(home=tmp_path) == (serum2.resolve(),)
    records = list(iter_presets(serum2))
    assert [item.relative_path for item in records] == [
        Path("Presets/Factory/Chord/Bright.SerumPreset")
    ]
    assert records[0].format == "SerumPreset"
    assert records[0].size_bytes == 6


def test_query_index_returns_metadata_and_intersects_tags(tmp_path: Path) -> None:
    root = tmp_path / "Serum 2 Presets"
    system = root / "System"
    system.mkdir(parents=True)
    db = sqlite3.connect(system / "presets.db")
    db.executescript(
        """
        CREATE TABLE Locations(location_id INTEGER PRIMARY KEY, location TEXT NOT NULL);
        CREATE TABLE Presets(preset_id INTEGER PRIMARY KEY, comment TEXT, date_added INTEGER,
          date_modified INTEGER, author TEXT, category TEXT, description TEXT, file_ext TEXT,
          hash TEXT, location_id INTEGER, name TEXT);
        CREATE TABLE Tags(tag_id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE Presets_Tags(preset_id INTEGER, tag_id INTEGER);
        CREATE TABLE UserData(hash TEXT UNIQUE, rating INTEGER);
        INSERT INTO Locations VALUES(1, 'Factory/Keyboard');
        INSERT INTO Presets VALUES(1, '', 0, 0, 'Level 8', 'Keyboard',
          'Clean Future Bass Chords', 'SerumPreset', 'abc', 1, 'KY - Smart Future');
        INSERT INTO Tags VALUES(1, 'S2'), (2, 'Poly');
        INSERT INTO Presets_Tags VALUES(1, 1), (1, 2);
        INSERT INTO UserData VALUES('abc', 5);
        """
    )
    db.close()

    rows = query_index(root, text="future", tags=("S2", "Poly"))
    assert len(rows) == 1
    assert rows[0].name == "KY - Smart Future"
    assert set(rows[0].tags) == {"S2", "Poly"}
    assert rows[0].rating == 5
    assert query_index(root, tags=("Mono",)) == ()


def test_query_index_rejects_unbounded_limits(tmp_path: Path) -> None:
    for invalid in (0, True, 1.5):
        with pytest.raises(ValueError):
            query_index(tmp_path, limit=invalid)  # type: ignore[arg-type]


def test_missing_index_does_not_create_a_database(tmp_path: Path) -> None:
    db_path = tmp_path / "System" / "presets.db"
    with pytest.raises(FileNotFoundError):
        query_index(tmp_path)
    assert not db_path.exists()


def test_search_treats_sql_and_like_metacharacters_as_text(tmp_path: Path) -> None:
    root = tmp_path / "Serum 2 Presets"
    system = root / "System"
    system.mkdir(parents=True)
    db = sqlite3.connect(system / "presets.db")
    db.executescript(
        """
        CREATE TABLE Locations(location_id INTEGER PRIMARY KEY, location TEXT NOT NULL);
        CREATE TABLE Presets(preset_id INTEGER PRIMARY KEY, comment TEXT, date_added INTEGER,
          date_modified INTEGER, author TEXT, category TEXT, description TEXT, file_ext TEXT,
          hash TEXT, location_id INTEGER, name TEXT);
        CREATE TABLE Tags(tag_id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE Presets_Tags(preset_id INTEGER, tag_id INTEGER);
        CREATE TABLE UserData(hash TEXT UNIQUE, rating INTEGER);
        INSERT INTO Locations VALUES(1, 'User');
        INSERT INTO Presets VALUES(1, '', 0, 0, 'author', 'Synth', '', 'SerumPreset', 'a', 1, '100%_Lead');
        """
    )
    db.close()

    assert [row.name for row in query_index(root, text="%_")] == ["100%_Lead"]
    assert query_index(root, text="' OR 1=1 --") == ()
