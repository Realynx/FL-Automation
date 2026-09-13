"""Route a named channel, load an effect, and discover its structured parameters."""

from fruitylink import connect


def main() -> None:
    with connect() as fl:
        piano = fl.channels.find("Piano")
        piano.mixer_track = 1
        track = fl.mixer[1]
        track.name = "Piano bus"
        track.volume = 9000
        effect = track.effects[0]
        effect.load("Fruity Parametric EQ 2")
        for parameter in effect.parameters:
            print(parameter.index, parameter.name, parameter.raw_value, parameter.display_value)


if __name__ == "__main__":
    main()
