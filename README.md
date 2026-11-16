# Luma Video Toolkit

A portable C# / WPF video toolkit for Windows, using your existing FFmpeg and
FFprobe. Native LibVLC provides streaming playback without loading videos into RAM.

## Run

1. Use **64-bit Windows 10 22H2** (or Windows 11). Extract the **entire** ZIP into a
   writable folder such as `Documents\LumaToolkit`; do not run inside the ZIP.
2. Launch `LumaToolkit.exe`. No installer, Python or administrator rights are needed.
3. Keep your existing `ffmpeg.exe` and `ffprobe.exe`. Luma checks PATH and looks
   beside FFmpeg for FFprobe. If not found, open **Settings**, browse to both
   executables and click **Save settings**. No FFmpeg binaries are bundled/downloaded.
4. Add or drop videos. Select an operation, review its processing note, then choose
   **Add selected to queue**. Ctrl/Shift selects a batch. Progress and errors appear
   in Processing queue. Successful outputs receive unique names beside each source.

The **self-contained** ZIP includes the .NET 10 Windows Desktop Runtime and the
x64 playback libraries. It does not require a separate .NET install. The smaller
**runtime-required** ZIP needs the **Microsoft .NET 10 Desktop Runtime, Windows x64**
(https://dotnet.microsoft.com/download/dotnet/10.0); the base .NET runtime alone is
insufficient for WPF. Both ZIPs still need your existing FFmpeg/FFprobe.

The executable was cross-compiled on Linux. Required runtime and playback files
were checked in the package; **Windows startup, GUI interaction and playback have
not been run here**. See TEST-RESULTS.md for the actual verification boundary.

## Included tools

- **Converter:** MP4, MKV, MOV, AVI, WebM, FLV and TS outputs. H.264, H.265, VP9 and
  MPEG-4 encoding, subject to the encoders in your FFmpeg build. Presets, custom
  CRF/speed, resolution, FPS and bitrate. Compatible container-only changes can
  copy streams; the processing note explains the chosen method.
- **Compressor:** quality presets; a 10–90% **file-size reduction** slider; or a target
  in MiB/GiB (binary MB/GB). Reduction/target modes use two-pass CPU H.264, deduct
  selected audio bitrate and reserve 3% for container overhead. Targets update
  immediately. Unrealistically low bitrates are rejected; low-quality targets
  warn. Actual output size and savings appear on completion. Exact sizes vary.
- **Remuxer:** copies all selected compatible video/audio/subtitle tracks. Rejects
  incompatible combinations with recovery advice. Choose MKV or remove tracks.
  Encoded video quality is preserved. Data streams, attachments and cover art are
  excluded explicitly; stream-level metadata and chapters are retained where supported.
- **Cutter:** embedded preview, playhead marks, timestamp inputs, fast stream-copy
  cuts near keyframes, or precise re-encoded cuts. Cut outputs omit source chapters
  because chapter timestamps would otherwise refer to the uncut video.
- **Repair:** rebuild container, fix seeking or timestamp repair. The latter two
  use `-fflags +genpts` and `-avoid_negative_ts make_zero`; these are deliberately
  the same conservative timestamp strategy. MKV is the repair default. Remuxing
  rebuilds writable indexes; it cannot recreate lost media. Optional full decode
  validation runs within the repair job before output publication and logs warnings.
- **Validation:** quick FFprobe stream/container check or cancellable full A/V
  decoding, with warnings, technical details and repair suggestions. Quick checking
  does not prove every frame or seek point is valid. Deep checking reads the whole file.
- **Information:** readable file summary plus complete formatted FFprobe stream,
  chapter and metadata JSON, including available aspect ratio, pixel format, bit
  depth, languages, stream IDs and encoder tags. Missing fields stay missing.
  Copy, TXT/JSON export and comparison with a completed output are included.
- **Utilities:** MP3, AAC/M4A, WAV and FLAC audio extraction; global audio/subtitle
  stream-ID selection/removal; PNG frame export; rotation, flips, crop and resize.
  Audio extraction re-encodes one selected track. Image operations re-encode video.
- **Settings:** JSON persistence, optional light theme, output folder/suffix,
  auto-open output folder, remembered preferences/window size, executable verification,
  available-encoder report, GPU test, cache limits and cleanup, logs, import/export/reset.

## Preview and performance

Press Play to start embedded LibVLC playback; use the seek bar or keyboard arrows.
Preview initialization is lazy and thumbnails are generated only for selected files,
then cached. Only a small set of thumbnail bitmaps remains resident. A failed preview
never blocks processing: **Open externally** uses your default player.

Defaults are **one queued job**, **two FFmpeg CPU threads**, **below-normal priority**
and a **64 MiB thumbnail cache**. Leave these defaults on a 4 GB, dual-core PC.
Two-pass encoding reads the source twice. MP4/MOV fast-start relocation also adds
an output-file pass. H.265 and VP9 can be slow on this CPU; H.264 is recommended.
Thread controls constrain codec threads but cannot cap every internal library worker.
Preview is paused when queueing a batch. Heavy validation is kept separate from
queued encoding. Two concurrent jobs are optional, intended for stronger PCs.

**Auto/GPU/CPU** applies to encoding. Luma really encodes a short sample using
`h264_qsv`, rather than trusting the encoder list. If that fails, CPU is used. If a
later QSV job fails, its own partial output is removed and that job is tried once
with the CPU, with details in the log. No H.265 or AV1 GPU support is assumed.
Size-targeted jobs always use CPU H.264. Successful HD 4400 GPU operation is untested.

## File safety and portable data

Sources are never overwritten/deleted. Jobs use unique `.luma-…partial.ext` files
in the destination folder. Only nonempty, FFprobe-readable outputs with expected
streams are renamed to descriptive, collision-free names. This lightweight check
is not a full integrity guarantee; use deep validation for important recordings.
Cancellation kills only that job's owned process tree and removes its partial/pass
files. Errors are isolated per job. Existing output names are given numbered suffixes.
Disk-space checks are estimates; quality-based encodes may grow larger than the source.
A crash/power loss can leave clearly named `.luma-` partial files; these are not shown
as successes. The in-memory queue is not restored after exit/crash.

Settings, thumbnails and logs live beside the executable in `Data`. If that folder
is not writable, the app uses `%LOCALAPPDATA%\LumaToolkit` and shows that location
in Settings. Logs are bounded at 2 MiB/day and older-than-seven-day logs are cleaned
at startup. FFmpeg stderr tails are capped at 256 KiB. Full videos are never buffered
in app memory. Actual Windows RAM usage and HDD performance have not been measured.

## Build and test

Install the **.NET 10 SDK** (not just the runtime). NuGet access is needed for the
three playback packages; no Python/Node dependencies are used. From this folder:

```powershell
.\build.ps1                         # self-contained win-x64 ZIP
.\build.ps1 -FrameworkDependent     # smaller ZIP, .NET 10 Desktop Runtime required
.\test.ps1                          # real FFmpeg integration tests
```

If PowerShell's local script policy blocks execution, run the direct command:

```powershell
dotnet publish src/Luma.Desktop/Luma.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/LumaToolkit
```

Keep the generated folder intact, including `libvlc\win-x64`. Do not enable WPF
trimming or bundle everything into a single executable. Build scripts refuse to
replace an existing artifact folder. Tests create a fresh directory of real sample
videos and outputs; they need FFmpeg/FFprobe in PATH with libx264, libx265, libvpx-vp9,
AAC, libmp3lame and libopus. `test-results.txt` contains results. Tests use generated
fixtures, never your own recordings. The core/tests also run on Linux with .NET 10.

Structure: `src/Luma.Core` (planning, processes, media, settings and queue),
`src/Luma.Desktop` (WPF UI/player), `tests/Luma.Tests` (dependency-free test runner).

## Limits

Clip merging is deferred: dependable multi-clip compatibility/timestamp handling
needs additional work. There is no full timeline editor, arbitrary FFmpeg command
entry, advanced image effects, AI processing or resume-after-crash queue. Output
containers/codecs are limited to the choices above; input probing accepts formats
supported by the installed FFmpeg. No corrupted user recording, Windows GUI flow,
actual HD 4400 GPU success, or multi-gigabyte HDD workload was verified in this build.
LGPL playback libraries and their source links are described in THIRD-PARTY-NOTICES.md.
