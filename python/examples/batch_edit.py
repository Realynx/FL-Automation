"""Batch control operations and native bulk note edits without implied rollback."""

from fruitylink import NoteEdit, connect


def main() -> None:
    with connect() as fl:
        with fl.batch() as batch:
            batch.add("set_tempo", bpm=125)
            batch.add("set_song_mode", song=True)
            batch.add("set_loop_region", start_tick=0, end_tick=fl.timebase.ticks(16))
        notes = fl.patterns.current.notes
        changed = notes.edit([NoteEdit(note.channel, note.key, note.start_tick,
                                       new_velocity=min(note.velocity + 5, 127)) for note in notes.list()])
        print(f"Changed {changed} notes")


if __name__ == "__main__":
    main()
