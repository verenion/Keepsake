# Photo Library

> **Work in progress.** It works on the author's library every day, but expect rough edges, schema changes
> and missing features. Issues and pull requests are welcome.

Desktop app (C# / .NET 10 / Avalonia, runs on Windows and Linux) that keeps a photo library in
`YYYY\YYYY_MM\YYYY_MM_DD\<original file name>` folders and imports new folders into it.

## Run

```
dotnet run --project src\PhotoLibrary.App
```
Open `PhotoLibrary.slnx` in VS / Rider / VS Code for development (`.vscode` has
launch and build tasks). Requires the .NET 10 SDK.

## What it does

- **Library view**: Year › Month › Day tree on the left, thumbnails grouped by day on the right, click a photo for
  a large preview and its metadata (capture date and where it came from, camera, size, GPS, Takeout description).
  A filter bar narrows the grid by file type, camera, GPS presence, file size, megapixels, shape (landscape /
  portrait / square), album membership and file name; the header shows "n of m items" while a filter is on.
  Tiles carry a coloured dot for each album the photo is in.
- **Import folder…**: checks every photo/video in any folder (Google Takeout exports included) against the library:
  *present* (identical file), *similar* (a re-saved/edited/resized version of a shot you have; shown side by side so
  you decide), or *missing*. Missing files are **copied** into their dated folder; the source folder is never changed.
- **Organise…**: previews and then moves everything in the library into the dated layout. Exact duplicates go to
  `_Duplicates`, undated files to `_Undated`, non-media files stay put. Every run is logged to `_Logs` and can be undone.

- **AI Tools tab**: on-device search and grouping using CLIP (ViT-B/32, int8, via ONNX Runtime). The model
  (~150 MB) is downloaded once from Hugging Face into `%LOCALAPPDATA%\PhotoLibraryi_models`; after that nothing
  leaves the machine. *Index new photos* embeds every library item from its thumbnail (re-runnable, only does new
  work), then you can search in plain English (`kids on the beach`, `christmas 2019`, `red car`), find photos that
  look like the selected one, browse the zero-shot tag cloud, or group the library into events by time, place and
  appearance. Vectors and tags live in the same SQLite file as the index, keyed by file id, so Organise moving a
  photo never loses its embedding. Videos (with ffmpeg) are indexed from a frame every 10 seconds by default
  (Settings, capped at 60 per video): a search matches any moment of a clip, the result tile shows "▶ at 1:20",
  and the preview opens on that frame. The mean of a video's frames is its vector for similarity, duplicates and
  events.
- **Albums tab**: right-click any photo (in the library, a search result, anywhere) and choose *Add to album*.
  Every album has a name, a colour, a cover photo and a type, all editable from *Album settings*. A **virtual**
  album is a list in the database: files stay where they are and a photo can be in any number of them. A
  **folder** album is a real folder, `_Albums\<name>`, inside the library: adding a photo moves its file there
  (and out of any other folder album, so a photo is in at most one), removing it moves the file back into its
  dated folder, renaming the album renames the folder, and Organise leaves `_Albums` alone. Folders you create
  under `_Albums` yourself show up as albums after a rescan.
- **Secure albums (vaults)**: choose *Secure* as an album's type to get an encrypted vault with its own
  password. Adding a photo encrypts it into `_Vault\<random id>` inside the library, checks the encrypted copy
  decrypts to the same bytes, then deletes the original permanently and removes every trace of it from the
  library: its database row, AI vectors, tags and video frames, quality scores, events, other album memberships,
  cached thumbnail and the import/organise log rows that named it. Vault photos appear only in their album, as
  grey lock tiles until it is unlocked; thumbnails and previews are then decrypted into memory only and are
  dropped when the vault locks (by hand, after 10 idle minutes, or when the app closes). From an unlocked vault
  you can export a decrypted copy, move a photo back into the library, or delete it. Deleting a secure album
  decrypts everything back into the library first. There is no recovery key: a forgotten password means the
  photos are gone.
- **Duplicates tab**: groups copies at four levels of risk, from byte-identical files (SHA-256) through re-saved
  copies (64-bit difference hash) to near-identical and merely similar shots (CLIP cosine similarity, when the
  library is indexed). Each group keeps its best copy, by pixels then bytes, and ticks the rest; you can remove the
  ticked files from the library or send them to the Recycle Bin. RAW files are only compared with other RAW files
  unless you opt in, so a RAW + JPEG pair is never split.
- **Quality tab**: measures every photo once (mean brightness, contrast, variance of the Laplacian for sharpness)
  from its thumbnail, then a strictness slider surfaces black, blown-out, flat and blurry photos, worst first, with
  the reason under each tile.
- **Right-click on any tile** (Ctrl/Shift-click to multi-select): open, show in folder, find similar, remove from
  the library, or delete from disk. Removing from the library is a soft delete: the row is kept and hidden, the
  file stays on disk, and the same bytes are never re-imported. Restore from Settings. Deleting from disk asks
  first and uses the Recycle Bin.
- **Settings tab**: library folder, thumbnail size, the AI model (CLIP ViT-B/32, B/16 or L/14, int8 or full
  precision; each downloaded into its own folder under `ai_models` and indexed separately, so switching back is
  instant) and search tuning (match strictness, result cap, "photos like this" cutoff). Settings are saved to the
  library database as soon as they change. A *Storage and indexes* card shows the database, thumbnail cache and
  model sizes, and how many photos each model's index holds. A *Hardware* card can move the model onto any DirectX 12 GPU through
  DirectML (the ONNX Runtime DirectML build is used throughout, so no CUDA install is needed; projects target x64
  because the DirectML redistributable is only copied for an explicit platform). Full-precision models index about
  ten times faster on a GPU; int8 ones gain roughly 2x.

## Where the date comes from (in order)

1. Camera EXIF `DateTimeOriginal` / Apple video creation date / video header
2. A full date **and time** in the file name (`20190106_125200000_iOS.jpg`, `PXL_20240519_143847545.jpg`)
3. EXIF digitized / modified dates
4. Google Takeout `photoTakenTime` (often just the upload date, so only used when nothing better exists)
5. A date-only file name

Photos are never modified. Everything extra (Takeout dates, descriptions, GPS, hashes, fingerprints) lives in
`%LOCALAPPDATA%\PhotoLibrary\library.sqlite`; thumbnails are cached next to it.

## Optional extras

- **HEIC** photos are decoded by Windows itself (the "HEIF Image Extensions" and "HEVC Video Extensions" Store
  packages, present on most machines), and by ffmpeg when those are missing.
- **ffmpeg** for video thumbnails and previews. Settings has a one-click download of the static build into
  `%LOCALAPPDATA%\PhotoLibrary\tools`; a copy already on `PATH` or installed by winget is picked up automatically.
- TIFF files (scans, exports) are decoded by Windows' built-in decoder, turned upright by their orientation tag,
  from memory too, so TIFFs and HEICs inside a vault get previews.
- Photoshop PSD/PSB files need nothing extra: the flattened composite Photoshop stores after the layers is
  decoded (RGB, greyscale, CMYK, indexed; 8 or 16 bit), falling back to the embedded thumbnail for files saved
  without "Maximize compatibility".
- RAW files (CR2, DNG, NEF, ...) need nothing extra: thumbnails come from the embedded JPEG preview, rotated by
  the file's own orientation tag, and the recorded size is the sensor's, not the preview's. A DNG with no preview
  (astrophotography apps write these) gets a quick render straight from the uncompressed sensor data.
- Pixel motion photos from Google Takeout come as `x.MP.COVER.jpg` (the still) plus `x.MP.COVER` (an MP4 clip);
  the clip is treated as a video.

## Project layout

- `src/PhotoLibrary.Core` — index, metadata, hashing, organise/import logic (no UI).
- `src/PhotoLibrary.AI` — CLIP tokenizer/model wrapper, vector index, zero-shot tagger, event grouper, `AiEngine` facade.
- `src/PhotoLibrary.App` — Avalonia desktop app.
- `tests/PhotoLibrary.Tests` — xunit. AI tests skip themselves when the model isn't downloaded.

## Vault format

Each vault folder holds `vault.json` and one `.plv` file per photo. The password goes through Argon2id
(64 MiB, 3 passes, 4 lanes; stored in `vault.json` so they can be raised) to a key that unwraps a random
256-bit vault key with AES-256-GCM. Changing the password re-wraps that key without touching the photos. Each
`.plv` file has a random salt; HKDF-SHA256 turns the vault key and salt into a per-file key, and the photo's
metadata (original name, dates, camera, GPS) and contents are sealed with AES-256-GCM in 1 MiB records whose
nonces carry the record index and a final-record flag, so altered, reordered or truncated files are rejected.
The folder reveals only how many items a vault holds and roughly their sizes.

Known limits: videos show no thumbnail inside a vault (decoding them would need a temporary decrypted file), identical copies of a photo elsewhere in the library stay unencrypted
(the confirmation says how many), a later import of the same photo from another folder would bring it back
unencrypted, old Organise/Import CSV logs in `_Logs` may still name the file, and an SSD can't guarantee the
deleted original is unrecoverable from the raw disk.

## Privacy

Everything runs on your machine. The only network access is downloading, when you ask for it, the CLIP model
files from Hugging Face and the ffmpeg build from GitHub. Nothing about your photos is sent anywhere.

## Tests

```
dotnet test
```
`IntegrationTests` uses real camera JPEGs from the folder in the `PHOTOLIBRARY_SAMPLES` environment variable
when it is set, and generated images otherwise.

## Licence

MIT, see [LICENSE](LICENSE). Third-party components and the things the app downloads at runtime are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
