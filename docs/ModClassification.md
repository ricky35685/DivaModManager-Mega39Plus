# Local mod classification

Diva Mod Manager keeps the category reported by a mod author or download site,
but it does not trust that value as the only source of truth. `ModClassifier`
scans installed content without modifying it and returns a separate primary
category, additional category labels, format variant, confidence, and evidence.

## Reliable signatures

| Structure | Interpretation |
| --- | --- |
| `rom/mod_pv_db.txt` plus charts or song media | Legacy custom song |
| `rom/nc_db.toml` | New Classics content format |
| `nc_db.toml` plus charts, but no song audio or movie | Additional difficulties |
| Song database or charts plus `rom/sound/song` or `rom/movie` | Custom song |
| Only `rom/sound/song/*.ogg` | Cover |
| `rom/mod_gm_module_tbl.farc` | Module |
| `rom/mod_gm_customize_item_tbl.farc` without the module table | Accessory |
| `spr_nswgam_*`, `aet_nswgam_*`, or common game-menu archives | User interface |
| `rom/sound/bgm/*.ogg` without song-pack signals | Sound replacement |
| A non-empty DLL listed by root `config.toml`, with the file present | Plugin |

Matches are case-insensitive. A `rom` directory may be below a configured
include root such as `AP/rom` or `Base Songs/rom`. Database filenames must be
direct children of that `rom`; `rom/backup/nc_db.toml` does not count. When a
valid `include` list exists, files outside those roots are ignored.

The classifier collects all flags before assigning scores. This is important
for mixed packs: a single mod may legitimately be both `Custom Song` and
`Module`, or `Custom Song`, `Plugin`, and `User Interface`. Folder names are
never used as evidence.

## New Classics details

New Classics loads a fixed `rom/nc_db.toml` file for each mod content root. The
database contains a `songs` table array and can select `ARCADE`, `CONSOLE`, or
`MIXED` chart styles. `script_file_name` is not restricted to a `script_nc`
folder, so that folder name is only corroborating evidence, not a decisive
signature.

An `nc_db.toml` song may intentionally reuse an MM+ stock PVID. This adds a New
Classics gameplay style or difficulty to the built-in song and reuses its
audio, video, and artwork; it is not a custom-song PVID conflict. Only chart
paths explicitly declared by `nc_db.toml` receive this exception. Legacy or
unregistered local charts targeting a stock PVID remain invalid overrides.

The first four bytes of a DSC can further identify the chart encoding:

| Signature | Chart encoding |
| --- | --- |
| `0x12020220` | F |
| `0x43535650` (`PVSC`) | F 2nd / X |
| `0x25061313` | New Classics |
| `0x14050921` | Future Tone |

DSC encoding alone cannot distinguish a custom song from an added difficulty;
the classifier combines it with databases and media. Header inspection is
bounded to 64 charts per mod so selection remains responsive on large packs.

## Eden Project details

Eden Project is a MEGA39+ dependency ecosystem, not a normal single song pack.
The official distribution contains Eden Core, multiple themed song packs, a
Module Pack, and resource patches. Song packs require an enabled, valid Core;
the Module Pack is independent and its stock-PVID records are module mappings,
not duplicate songs.

Legacy Eden databases use `difficulty.<name>.length` as the active array size.
Indexed template fields at or above that length are ignored. If a declared
active slot is absent, it remains a real missing-chart error. `another_song.*`
records are optional vocal alternatives, so missing alternate audio degrades a
song to a warning without making its base chart unplayable.

Media follows DivaModLoader's merged VFS: audio and video may come from another
enabled mod or from the MEGA39+ CPKs. Charts do not. A Legacy or New Classics
chart must exist in the content root that declares it; this prevents unrelated
mods from silently satisfying a missing chart.

Eden Core is accepted only when its configured name and author match the
official release and `settings.toml`, `rom/mod_pv_db.txt`, `DLCChecker.dll`,
`OldMan.dll`, and `SaveDataMigrator.dll` are present and declared. A signed Core
may add Extra Extreme charts to stock PVIDs 83, 93, 95, 276, 434, and 623 only
when slot `extreme.1` is marked extra, the declared Extreme length is at least
two, and the exact local file is `rom/script/pv_NNN_extreme_1.dsc`. Its stock
base chart and media may then be inherited. PVIDs 19, 27, and 207 are restored
PC songs and are handled as full songs rather than MM+ stock-song patches.

Chartless entries can be attached to a compatible full-song provider as
patches and do not create PVID conflicts. Patch paths remain visible, while
metadata editing and whole-song deletion are disabled to protect shared Core,
Module, and artwork databases. Independent full songs that reuse a PVID remain
real conflicts even when their names happen to match.

## Song health and manual overrides

Runtime checks are specific to MEGA39+ and distinguish required assets from
optional or inherited content. Missing main audio, every usable chart, or a
video that is explicitly declared by the database is blocking. If at least one
chart remains usable, unresolved chart slots are a warning instead. Empty
Legacy/New Classics placeholders already satisfied by a matching local chart
do not produce a warning. `another_song.*` audio is an optional vocal variant,
so its absence remains a warning even when the song supplies media for a patch
or an Additional Difficulty target.

A full song with no declared movie is treated as a 3D PV and does not require a
video file. This exception does not apply to Additional Difficulty entries,
which inherit their target song's media. Missing Thumbnail, Jacket, or
Background sprites are warnings and are reported separately.

Two enabled full-song definitions that reuse a PVID normally conflict. A
compatibility exception applies when every definition has the same configured
mod name, declared authors do not disagree, and the entries share a song name.
Only charts whose DSC files exist are compared. Difficulty identity uses the
normalized difficulty name, including the `ex_` distinction, but not the
database array index:

| Same-source overlap | Automatic status |
| --- | --- |
| No shared difficulty | Keep the existing status |
| Shared difficulty, disjoint known star ratings | Warning |
| Shared difficulty and star rating | Broken |
| Shared difficulty with an unknown/invalid star rating | Broken |

All contributing database paths remain visible in Song Manager for both
compatible definitions and true conflicts. Users can persistently override the
final status to Ready, Warning, or Broken, or return it to Automatic. Overrides
are stored in `%LocalAppData%\DivaModManager\song-run-status-overrides.json`;
they change filtering and display color without deleting the automatic
diagnostic. Invalid, incomplete, or unsupported override documents are ignored
rather than defaulting a song to Ready. User-facing song-management errors and
health reasons are displayed in Chinese, while original exception details are
retained only in the log.

## Sources

- [DIVA Mod Loader configuration and `mod_pv_db.txt`](https://github.com/blueskythlikesclouds/DivaModLoader)
- [New Classics database loader](https://github.com/mrcloverthecoder/nc/blob/732b3390ac4a820bdc817852f433b05071336566/src/db.cpp)
- [New Classics DSC parser](https://github.com/mrcloverthecoder/nc/blob/732b3390ac4a820bdc817852f433b05071336566/src/game/dsc.cpp)
- [MM+ Song Mod Creation Guide](https://gamebanana.com/tuts/15681)
- [Comfy Studio chart tooling](https://docs.divamodarchive.com/Comfy_Studio_F2X)
- [Diva Mod Archive: New Classics](https://divamodarchive.com/post/169)
- [Diva Mod Archive: Console Charts](https://divamodarchive.com/post/149)
- [Eden Project v5.6](https://gamebanana.com/mods/405848)
- [Eden Project Core](https://gamebanana.com/mods/478684)
- [Eden Project official pack collection](https://gamebanana.com/collections/75181)
- [Eden Project v5.6 archive](https://archive.org/details/v-5.6-full)
