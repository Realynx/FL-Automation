"""Executed by the .NET scripting integration test against its real pipe server."""

import json
import sys
from dataclasses import replace
from pathlib import Path

from fruitylink import ConnectionError, Endpoint, NoteSpec, Operations, RemoteError, connect
from fruitylink.values import to_json


def main() -> None:
    endpoint = Endpoint.from_json(to_json(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))))
    fl = connect(endpoint=endpoint)
    catalog = fl.catalog()
    assert isinstance(catalog, dict)
    operations = catalog.get("operations")
    expected_operations = [name for name in dir(Operations) if not name.startswith("_") and name != "invoke"]
    assert isinstance(operations, list) and len(operations) == len(expected_operations)
    assert fl.transport.tempo == 120.0
    fl.transport.tempo = 123.0
    assert fl.transport.tempo == 123.0
    fl.patterns[1].notes.add([NoteSpec(0, 60, 0, 96, 100)])
    batch = fl.batch().add("set_tempo", bpm=127.0).add("get_tempo")
    result = batch.run()
    assert result.succeeded and result.results[1].result == 127.0
    assert fl.project.info.title == "Interop project"
    try:
        connect(endpoint=replace(endpoint, token="incorrect-token"))
    except RemoteError:
        pass
    else:
        raise AssertionError("Incorrect token was accepted.")
    try:
        connect(endpoint=replace(endpoint, instance_id="stale-instance"))
    except ConnectionError:
        pass
    else:
        raise AssertionError("Stale instance identity was accepted.")
    print("Python/C# scripting interoperability passed.")


if __name__ == "__main__":
    main()
