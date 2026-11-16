# Verification and limits

Release verification: 14 November 2026 (Asia/Riyadh).

- Debian 13 x64; .NET SDK 10.0.404 / runtime 10.0.15.
- FFmpeg/FFprobe 7.1.5 Debian build, with the encoders used by the test suite.
- **32/32 integration tests passed.** `test-results.txt` is the original runner output.
- WPF Release build and self-contained/framework-dependent Windows x64 publishing
  succeeded with warnings treated as errors. No build warnings or errors.
- Portable packages checked for Windows x64 EXE, app/config/dependency files,
  WPF/core runtimes in the self-contained build, and x64 LibVLC/player plugins.
  ARM64/x86 playback binaries and external ffmpeg.exe/ffprobe.exe are excluded.

## Implemented and exercised with real media

| Area | Evidence |
|---|---|
| Conversion | H.264, H.265, VP9, MPEG-4; MP4/MKV/MOV/AVI/WebM/FLV/TS; resized/FPS-converted outputs fully decoded |
| Container-only conversion/remux | Every decoded video frame hash matched the original; streams and languages inspected |
| Quality compression | Smaller output, expected resolution/duration, full decode |
| 50% file reduction | Original 1,677,885 bytes; calculated target 838,942 bytes; actual 829,262 bytes, **50.58% reduction** |
| Target-size compression | Target 1,090,625 bytes; actual 1,075,237 bytes, **1.41% below target** |
| Fast cut | Stream-copy codec preserved; decodable bounded output; keyframe inaccuracy allowed |
| Precise cut | Four seconds at 25 fps produced exactly 100 frames; CRF-0 first frame matched source frame 50 |
| Thumbnails | Actual JPEG output and identical cached image on repeat |
| Metadata | Actual video/audio/path/size/duration/language properties parsed; absent values left unknown |
| Repair | TS to MKV through all three repair options; decode and near-end seeking checked |
| Missing index/duration | Live-style MKV had no reported duration; rebuilding restored duration and seeking while preserving all decoded frame hashes |
| Negative timestamps | Real TS started at -2.021333s; timestamp repair produced nonnegative start at 0.059s and full decode |
| Validation | Quick stream/container report and full A/V decode; optional repaired-output deep validation |
| Track handling | Multiple audio/subtitle preservation, explicit stream IDs, removals, MP4 text-subtitle conversion, incompatible remux rejection |
| Small utilities | MP3/AAC/WAV/FLAC extraction, PNG export, rotations, flips, crop/resize, mute; outputs inspected/decoded |
| File safety | Input SHA-256 unchanged after processing/failures/cancellation; existing output hash unchanged on name conflict |
| Cancellation | Cancelled after real FFmpeg progress on an active encoding process; no successful output or partial/pass files left |
| Failures/queue | Corrupt input and failed encode produced errors; subsequent queued job still completed; waiting job cancellation marked correctly |
| GPU fallback | Actual QSV test failed on this machine; GPU request fell back to real CPU encoding. A stale-success capability injection caused actual QSV failure, then actual CPU retry successfully |
| Settings/cache | JSON persistence/import/export, bounds, corrupt settings diagnostics; cache clear; duration formatting beyond 24 hours |

Fixtures are generated with lavfi: a 12-second, 640×360, 25 fps test pattern and
48 kHz sine audio, plus subtitle/multitrack, live-MKV and negative-timestamp TS variants.
No personal recordings were used. The test runner source and invocation are included.

## Implemented, not verified on target Windows hardware

WPF layouts, navigation, drag/drop, dialogues, theming, clipboard, folder opening,
window restoration, native LibVLC playback/seek, package startup, Windows process
priority, and successful Intel HD 4400 Quick Sync encoding need a Windows smoke test.
Compiling WPF on Linux does not test these interactions. CPU fallback and actual
media processing were exercised through the same core the GUI invokes.

Actual 4 GB PC memory usage, large HDD recordings, unusual codecs/metadata, optional
two-job concurrency and repairs of the user's own damaged recordings remain untested.
The target-size fixture tolerance is not a promise that arbitrary recordings will hit
that tolerance. Remux repair cannot restore missing/corrupt video data, and valid
metadata does not prove that a file is entirely healthy.

## Deferred

Multi-clip merging, arbitrary container/encoder configuration, persistent/resumable
queues, and advanced timeline editing. These were omitted to keep the processing
core small and dependable. No placeholder buttons represent deferred features.
