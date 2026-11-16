# Luma Toolkit downloads

## v1.0.1 — queue binding fix

[Download the v1.0.1 self-contained Windows x64 portable ZIP](https://media.githubusercontent.com/media/iAhmad7/Video-app/v1.0.1/downloads/LumaToolkit-v1.0.1-win-x64-portable.zip)

Includes the .NET 10 Desktop Runtime. Fixes the queue progress binding and prevents repeated identical UI error popups while retaining diagnostic logs. See the [v1.0.1 release](https://github.com/iAhmad7/Video-app/releases/tag/v1.0.1), [checksums](SHA256SUMS-v1.0.1.txt) and [test results](../TEST-RESULTS.md).

Close Luma before upgrading. Keep your existing `Data` folder to retain settings; the new ZIP does not contain or replace it.

## Original v1.0.0 downloads (preserved)

- [Portable Windows x64 application — includes .NET Desktop Runtime](https://media.githubusercontent.com/media/iAhmad7/Video-app/main/downloads/LumaToolkit-win-x64-portable.zip)
- [Smaller Windows x64 application — requires .NET 10 Desktop Runtime](https://media.githubusercontent.com/media/iAhmad7/Video-app/main/downloads/LumaToolkit-win-x64-runtime-required.zip)
- [Complete original source archive](https://media.githubusercontent.com/media/iAhmad7/Video-app/main/downloads/LumaToolkit-source.zip)

All three files are the original, unchanged archives. Each was downloaded back from GitHub and verified against the original [SHA-256 checksums](../SHA256SUMS.txt).

Extract the entire application ZIP before launching `LumaToolkit.exe`. Both application builds use your existing FFmpeg and FFprobe installation. Existing build/test results and Windows verification limits are in [TEST-RESULTS.md](../TEST-RESULTS.md).

The archives use Git LFS. The links above download the complete ZIP contents. Cloning the repository with these assets requires Git LFS.
