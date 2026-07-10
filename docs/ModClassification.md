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

## Sources

- [DIVA Mod Loader configuration and `mod_pv_db.txt`](https://github.com/blueskythlikesclouds/DivaModLoader)
- [New Classics database loader](https://github.com/mrcloverthecoder/nc/blob/732b3390ac4a820bdc817852f433b05071336566/src/db.cpp)
- [New Classics DSC parser](https://github.com/mrcloverthecoder/nc/blob/732b3390ac4a820bdc817852f433b05071336566/src/game/dsc.cpp)
- [MM+ Song Mod Creation Guide](https://gamebanana.com/tuts/15681)
- [Comfy Studio chart tooling](https://docs.divamodarchive.com/Comfy_Studio_F2X)
- [Diva Mod Archive: New Classics](https://divamodarchive.com/post/169)
- [Diva Mod Archive: Console Charts](https://divamodarchive.com/post/149)
