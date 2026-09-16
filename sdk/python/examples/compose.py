"""Create a named chord pattern and place it with explicit beat/tick conversions."""

from fruitylink import NoteSpec, connect


def main() -> None:
    with connect() as fl:
        channel = fl.channels.add("FL Keys", name="Piano")
        pattern = fl.patterns.create("Verse chords")
        timebase = fl.timebase
        pattern.notes.add([NoteSpec(channel.index, key, timebase.ticks(0), timebase.ticks(4), 90)
                           for key in (60, 64, 67)])
        fl.playlist[1].name = "Piano"
        fl.playlist.add_pattern(pattern.index, track=1, start_beats=0, length_beats=4)
        print(fl.patterns.list())


if __name__ == "__main__":
    main()
