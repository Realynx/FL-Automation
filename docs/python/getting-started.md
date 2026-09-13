# Your first Python script

Start with a project read, then make one small change and read it back. Choose the setup that matches where you want to run code.

## Option A: run through FLMCP

Install and configure [FLMCP](https://github.com/Realynx/Fl-MCP), establish a project session using its getting started guide, and send this code to its `fl_execute_python` tool:

```python
print("Tempo:", fl.transport.tempo)
result = fl.project.info
```

The tool supplies `fl`. Its response contains captured output and a structured project result. The framework bundles Python for this workflow; do not call `connect()` inside the script. FLMCP owns project/session policy around the SDK.

The [FL Python IDE](../../src/FruityLink.Plugins.PythonIde/README.md) is an optional editor using the same script shape. It remains under active development; it is not required for either quickstart path.

## Option B: run your own Python program

### 1. Prepare FL Studio

Use Windows x64 with the [FruityLink host installed](../installation.md) and a supported FL Studio build. Install the matching **FL Python** plugin into the host's configured `plugins` directory and enable **Tools → FL Plugins → FL Python**. Keep the plugin and host contract versions matched.

The [endpoint plugin instructions](../../src/FruityLink.Plugins.Python/README.md) explain packaging and installation. The endpoint connects to an already running FL Studio instance; it does not launch FL.

### 2. Install the Python package

Install Python 3.11 or later. From a checkout of the **FL-Automation** repository, run:

```powershell
python -m pip install ./python
```

The distribution is named `fruitylink-python`; import it as `fruitylink`. It has no runtime dependencies outside the Python standard library. These instructions install from source and do not assume a public package registry release.

### 3. Read the active project

Save this as `hello_fl.py` outside the SDK's source package:

```python
from fruitylink import connect

with connect() as fl:
    print("Tempo:", fl.transport.tempo)
    print("Project:", fl.project.info)
    for channel in fl.channels.list():
        print(channel.index, channel.name)
```

Run it from the directory containing your script:

```powershell
python hello_fl.py
```

You should see the tempo, a typed project record, and Channel Rack entries. Reads reflect the current project in the connected FL process. Exiting the `with` block does not close FL Studio.

If several FL instances have discovery records, explicitly select the process ID from Windows Task Manager:

```python
from fruitylink import connect

with connect(pid=1234) as fl:  # Replace 1234 with your FL Studio PID.
    print(fl.project.info)
```

`connect()` requires exactly one discovery record and verifies the live instance. It does not silently choose a different instance when a record is stale.

## Make your first change

Use a scratch project or save your current work first. In an embedded script, run the code below as written. In an external program, place it inside your `with connect() as fl:` block.

```python
fl.transport.tempo = 120
print("Updated tempo:", fl.transport.tempo)
result = {"tempo": fl.transport.tempo}
```

The setter runs immediately. `result` is how embedded scripts return structured data; in a normal `.py` program it is an ordinary variable. There is no separate commit call.

Next, follow [the composition recipe](examples.md#compose-a-pattern) to create an instrument, write notes and place a pattern in the Playlist.

## If the first read fails

| Symptom | What to check |
| --- | --- |
| `ModuleNotFoundError: fruitylink` | Install with the same Python executable that runs your script. |
| No scripting instance found | FL Studio must be running with the matching host and FL Python plugin enabled. |
| Multiple scripting instances found | Pass an explicit `pid`; do not rely on discovery order. |
| Instance changed, stale record, or transport error | Confirm the intended FL process and plugin are still running, then reconnect. |
| Unsupported operation or query | Inspect `fl.capabilities()`; support follows the host's verified native profile. |
| Hosted plugin parameter interface unavailable | Try a hosted generator or effect. Sampler envelopes and sample settings are not exposed through generic plugin parameters. |

See [errors and recovery](api.md#errors-and-recovery) for the distinction between a failed read and an edit whose completion is uncertain.
