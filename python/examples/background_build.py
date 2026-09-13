"""Create and render a disposable FL project without MCP or visible FL windows."""

import argparse
from pathlib import Path

from fruitylink import launch


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fl", type=Path, required=True, help="Installed FL64.exe with FruityLink.")
    parser.add_argument("--project", type=Path, required=True, help="New working .flp path.")
    parser.add_argument("--output", type=Path, required=True, help="New output .wav path.")
    args = parser.parse_args()
    with launch(args.fl, args.project, background=True) as session:
        fl = session.studio
        fl.transport.tempo = 120
        fl.transport.song_mode = True
        channel = fl.channels.add("3x Osc", name="Background build synth")
        channel.volume = 3000
        pattern = fl.patterns.create("E minor")
        for beat in range(8):
            for key in (64, 67, 71):
                pattern.notes.add_beats(channel=channel.index, key=key, start=beat, length=0.5)
        fl.playlist.add_pattern_ticks(pattern.index, track=1, start=0, length=8 * fl.timebase.ppq)
        session.save()
        print(session.render(args.output))


if __name__ == "__main__":
    main()
