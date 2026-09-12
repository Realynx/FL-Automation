# Live FL verification

Status: not performed. No production FL session was launched or altered during implementation. Automated tests do not establish support for a particular FL executable or native bridge layout.

For each exact supported FL Studio 2025 or 2026 executable, record the full version, FruityLink version, Windows version, and tested plugins. Run in a disposable Windows session with licensed FL and instruments, and a clean template.

1. Install the matching FruityLink 0.2.0 host/plugin and SDK Python wheel, enable FL MCP once, then close FL. Connect an MCP client and confirm all 24 tools appear.
2. Launch `sessions/smoke.flp`; confirm bridge PID and reported project Path match the new process and file. Confirm a second start attempt leaves the first session alone.
3. Read PPQ, load a known licensed generator, create a pattern, add four notes, read them back, and place a four-bar clip in the playlist. Set tempo and verify read-back.
4. Save `versions/smoke-v1.flp`. Confirm it reopens correctly in FL later and contains the authored notes and arrangement. Save uses a low-level SDK writer; do not infer semantic validity from its byte envelope alone.
5. Render `audio/smoke.wav`. Confirm command-line rendering finishes, process exits, output plays correctly, its duration matches the arrangement and export-tail choice, and the returned snapshot reopens.
6. Restart from the returned snapshot using `sourceProjectPath`, change tempo, and render to a new path. Confirm the first output is untouched.
7. Exercise a missing instrument or asset and a short render timeout. Confirm no false success, only owned processes stop, the snapshot remains, and restarting from that snapshot recovers work.
8. Cancel an active edit/render and inspect state before retrying. Confirm malformed pipe input and absent token do not invoke FL operations. Confirm another normal FL instance is refused before launch.
9. Close the MCP client with a disposable session open and verify it releases its pipe and owned process. Reopen the previously saved snapshot and confirm recovery.
10. Start a new project; inspect `fl_python_api` and `fl.capabilities()`. Use `fl_execute_python` to read typed project/channels, then run the authoring example with an installed licensed generator. Read back notes and clips, save, render, and listen to the output.
11. Cancel Python while a batch is running. Confirm completed edits remain, later batch items stop, and the next edit/render waits until the active native call finishes. Check that Python worker descendants exit and the relay disappears.
12. In a disposable test project, manually switch FL's project during script execution. Confirm the next callback fails identity validation, and new/open/save-as calls through Python cannot bypass managed lifecycle policy.

Until these pass, describe the host workflow as implemented but unverified, not production headless rendering. If FL does not exit after CLI render, investigate its documented behavior and adjust completion detection with evidence rather than accepting file-size stability as proof.
