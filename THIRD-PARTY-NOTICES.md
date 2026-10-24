# Third-party notices

VidGood's own source code is licensed under the MIT License (see `LICENSE`).
The release executable also contains the following components, which keep their own licenses.

## libVLC 3.0.24 — VideoLAN

- License: GNU Lesser General Public License v2.1 or later (`licenses/LGPL-2.1.txt`)
- Distributed through the NuGet package `VideoLAN.LibVLC.Windows` 3.0.24
- Source code: https://code.videolan.org/videolan/vlc and https://www.videolan.org/vlc/download-sources.html
- The release exe carries libVLC as an embedded archive and unpacks it unmodified to
  `%LOCALAPPDATA%\VideoPlayer\vlc\<version>\`. You may replace those files with your own
  build of libVLC 3.x; the app loads `libvlc.dll` from that folder at startup.

## LibVLCSharp / LibVLCSharp.WPF 3.10.2 — VideoLAN

- License: GNU Lesser General Public License v2.1 or later (`licenses/LGPL-2.1.txt`)
- Source code: https://github.com/videolan/libvlcsharp

## .NET 8 runtime and WPF — Microsoft

- License: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- Included because the release exe is self-contained.

VLC and VideoLAN are trademarks of VideoLAN. VidGood is not affiliated with or endorsed by VideoLAN.
