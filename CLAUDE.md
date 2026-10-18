# SleepPicker

Load the `IoT_minimal` skill before changing anything here. Its rules are the project's rules:

- **Target:** Windows 10 IoT Enterprise LTSC 2021 (build 19044), as shipped. Nothing gets installed.
- **Build:** `build.cmd`, using the in-box MSBuild 4.0 and the C# 5 compiler. That means no `$""`, `?.`, `nameof`, `=>` members, tuples or pattern matching. No NuGet, no SDK-style project. MSB3644 is expected.
- **Ship:** one self-contained `bin\SleepPicker.exe`, committed. Run it `asInvoker`, start it from the HKCU `Run` key, and write only to HKCU. Store a setting only while it differs from its default.
- **Style:** match the surrounding code: explicit types, braces always, a `NativeMethods` class nested in each file, and comments that say *why*. Add no new dependency unless it's already in the GAC (as `Accessibility` is).

- **Bitness:** the exe runs 64-bit (`Prefer32Bit=false`), because DWM refuses the taskbar peek from a 32-bit caller. Test harnesses that load the exe must run at the same bitness, or they test different code paths (a missing 32-bit export once hid behind a passing 64-bit test).

Testing on this machine: other sessions wait at prompts on the same desktop. Never inject keystrokes, and only move the pointer with the user's go-ahead.
