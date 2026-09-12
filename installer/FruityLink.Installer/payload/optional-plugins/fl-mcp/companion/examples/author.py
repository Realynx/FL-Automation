"""Submit this script to fl_execute_python after fl_project_start.

Discover installed generator names with fl_plugins_list and change GENERATOR.
After execution, call fl_project_save and fl_project_render with fresh paths.
This example has protocol coverage; authoring/rendering requires live FL validation.
"""

from fruitylink import NoteSpec

GENERATOR = "3x Osc"  # Replace with an exact installed generator name.

fl.transport.tempo = 120
channel = fl.channels.add(GENERATOR, name="MCP melody")
pattern = fl.patterns.create("Four beats")
timebase = fl.timebase
pattern.notes.add([
    NoteSpec(channel.index, key, timebase.ticks(beat), timebase.ticks(0.75), 90)
    for beat, key in enumerate((60, 64, 67, 72))
])
fl.playlist.add_pattern(pattern.index, track=1, start_beats=0, length_beats=4)
fl.transport.song_mode = True
result = {"channel": channel.index, "pattern": pattern.index, "ppq": timebase.ppq,
          "notes": pattern.notes.list(channel=channel.index)}
