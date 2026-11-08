# Third-party notices

Keepsake is MIT licensed (see [LICENSE](LICENSE)). It builds on the packages below, pulled from NuGet at
build time, and can download a few things at runtime when you ask it to. Nothing third-party is vendored into
this repository.

## NuGet packages

| Package | Licence | Used for |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | MIT | Desktop UI (the Inter font is bundled under the SIL Open Font License 1.1) |
| CommunityToolkit.Mvvm | MIT | View models |
| SkiaSharp (+ NativeAssets.Linux) | MIT | Image decoding, resizing, thumbnails |
| MetadataExtractor | Apache-2.0 | EXIF / QuickTime metadata |
| Konscious.Security.Cryptography.Argon2 (+ .Blake2) | MIT | Argon2id password hashing for secure albums |
| Velopack | MIT | Installer and on-demand updates |
| Microsoft.Data.Sqlite, SQLitePCLRaw | MIT / Apache-2.0 | Library database (SQLite itself is public domain) |
| Microsoft.ML.OnnxRuntime.DirectML, Microsoft.ML.OnnxRuntime.Managed | MIT | Running the CLIP model |
| Microsoft.AI.DirectML | Microsoft Software License Terms (redistributable, Windows only) | GPU inference. Pulled in by the ONNX Runtime DirectML package; its licence permits distribution inside applications but not modification. See `LICENSE.txt` inside the package. |
| xunit, xunit.runner.visualstudio | Apache-2.0 | Tests |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT | Tests |

## Downloaded at runtime (never redistributed by this repository)

- **CLIP model weights**: ONNX exports published on Hugging Face under `Xenova/clip-vit-base-patch32`,
  `Xenova/clip-vit-base-patch16` and `Xenova/clip-vit-large-patch14`. These are conversions of OpenAI's CLIP
  models, which are MIT licensed; the base-patch32 export is tagged MIT on the Hub, the other two carry no
  explicit tag and inherit OpenAI's terms. The tokenizer files (`vocab.json`, `merges.txt`) come from the same
  repositories.
- **ffmpeg**: the static Windows build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), which is
  GPL-2.0-or-later. Keepsake runs it as a separate process and does not link against it, so the GPL does not
  extend to this code. The binary is stored in the app's data folder, not in this repository.

## Windows components used when present

- Windows Imaging Component with the "HEIF Image Extensions" and "HEVC Video Extensions" Store packages, for HEIC
  decoding. These are part of Windows / the Microsoft Store and are not distributed here.
- DXGI for enumerating GPUs.
