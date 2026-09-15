"""query_index text matching: single words, word AND across fields, case and location (Parking Lot Moon fix)."""

import sqlite3
from pathlib import Path

from fruitylink_serum.inventory import query_index


def make_index(tmp_path: Path) -> Path:
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
        INSERT INTO Locations VALUES(1, 'Factory/Pad'), (2, 'Splice/Melodic House - Soft Focus'), (3, 'Factory/Lead');
        INSERT INTO Presets VALUES
          (1, '', 0, 0, 'Xfer', 'Pad', 'Everyone needs analog pads', 'SerumPreset', 'a', 1, 'PD - Analog Soft Cotton'),
          (2, '', 0, 0, 'Xfer', 'Pad', 'You want warm pads, wa got warm pads', 'SerumPreset', 'b', 1, 'PD - Analog Butter'),
          (3, '', 0, 0, '91V', '', 'Melodic House - Soft Focus', 'SerumPreset', 'c', 2, '91V_SF_keys_escape_analog'),
          (4, 'tape hiss', 0, 0, 'Xfer', 'Lead', 'Bright saw', 'SerumPreset', 'd', 3, 'LD - Glass');
        INSERT INTO Tags VALUES(1, 'Warm'), (2, 'Poly');
        INSERT INTO Presets_Tags VALUES(4, 1), (1, 2);
        """
    )
    db.commit()
    db.close()
    return root


def test_single_words_match_preset_names_case_insensitively(tmp_path: Path) -> None:
    root = make_index(tmp_path)
    for word in ("soft", "Soft", "SOFT", "cotton"):
        names = [row.name for row in query_index(root, text=word)]
        assert "PD - Analog Soft Cotton" in names, word
    # location text counts too: the Splice pack folder says "Soft Focus"
    assert "91V_SF_keys_escape_analog" in [row.name for row in query_index(root, text="soft")]


def test_every_word_must_match_across_name_description_comment_and_tags(tmp_path: Path) -> None:
    root = make_index(tmp_path)
    assert [row.name for row in query_index(root, text="analog pad")] == ["PD - Analog Butter", "PD - Analog Soft Cotton"]
    assert [row.name for row in query_index(root, text="warm")] == ["LD - Glass", "PD - Analog Butter"]   # tag + description
    assert [row.name for row in query_index(root, text="tape")] == ["LD - Glass"]                         # comment
    assert query_index(root, text="analog glass") == ()
    assert query_index(root, text="   ") == query_index(root)                                            # blank = no text filter


def test_limit_applies_after_word_filtering_and_category_still_filters_in_sql(tmp_path: Path) -> None:
    root = make_index(tmp_path)
    assert len(query_index(root, text="analog", limit=1)) == 1
    assert [row.name for row in query_index(root, text="analog", category="pad")] == ["PD - Analog Butter", "PD - Analog Soft Cotton"]
    assert [row.name for row in query_index(root, text="%_", limit=5)] == []                              # LIKE metacharacters are text
