# Third-party components

Luma's own source is MIT licensed. Third-party components retain their licenses.

- LibVLCSharp and LibVLCSharp.WPF 3.10.2 — VideoLAN, LGPL-2.1-or-later.
  Source: https://code.videolan.org/videolan/LibVLCSharp (3.10.2 release).
  NuGet: https://www.nuget.org/packages/LibVLCSharp.WPF/3.10.2
- VideoLAN.LibVLC.Windows 3.0.24 — VideoLAN, LGPL-2.1-or-later.
  Unmodified x64 native binaries and codec/demux plugins are distributed separately
  in libvlc/win-x64. Source and build recipes:
  https://code.videolan.org/videolan/vlc (3.0.24 release) and
  https://code.videolan.org/videolan/libvlc-nuget
  VideoLAN source archives: https://download.videolan.org/pub/videolan/vlc/3.0.24/
  NuGet: https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24
- Microsoft .NET 10 / Windows Desktop Runtime — Microsoft and contributors,
  MIT and third-party notices. Source: https://github.com/dotnet/runtime and
  https://github.com/dotnet/wpf. Runtime LICENSE.txt / ThirdPartyNotices.txt
  are included in the self-contained distribution where provided by the SDK.

The LGPL text is in licenses/LGPL-2.1.txt. The managed and native playback libraries
are dynamically linked, unmodified, and replaceable; do not remove notices when
redistributing. LGPL permits reverse engineering for debugging modifications to
these libraries. Preserve applicable third-party notices for replacement builds.

FFmpeg and FFprobe executables are NOT distributed or installed by Luma. Luma runs
the user's existing installation as a separate process. That installation's own
license and encoder availability apply. LibVLC contains its own native decoding
libraries for playback; these do not replace or alter system FFmpeg.
