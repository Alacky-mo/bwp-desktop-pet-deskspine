# DeskSpine

[中文](README.md) | [English](README.en.md)

> Fork note: the shipped program name is **BwpDesktopPet** and the tray / About window show **百闻牌桌宠**. Namespaces, folders, the config directory and the single-instance mutex keep the `DeskSpine` name on purpose -- renaming those would orphan existing settings and the auto-start entry.

> Assets note: the three demo skeletal sets that came with the upstream repository (`guanghui_2` and `yanzhan_g` from Azur Lane, `dyn_illust_char_1012_skadi2` from Arknights -- 9 files of somebody else's game content) **were deleted from this fork on 2026-11-04**, together with the nine matching copy items in `DeskSpine.csproj` and the default value in `Config.cs` that pointed at one of them. So after `dotnet build` on this fork, launching the exe gives you an empty roster and nothing is drawn -- put the absolute path of your own `.skel` into a `SkelPath` row in the settings window, or let `bwp-assets/setup_pet.py` from the parent project fill the roster. Upstream's own two icons (`appicon_dark.ico` and `res/image/hourlychime.ico`) are **kept on purpose**: the first is the build fallback for a clean clone that has no derived icon, the second is still selectable by hand as the hourly-chime bubble icon.

> Two tracks since 2026-11-04 (owner's ruling: "the release we publish *is* a 百闻牌 desktop pet"). **This repository still carries 0 bytes of game assets** -- pure code, and the sentence above about an empty roster stays true for a clone. The two **zip attachments on the GitHub Release**, on the other hand, ship four characters (不知火 `buzhihuo_1127`, 猫掌柜焕宴 `maozhanggui_sp_yihua_3754`, 千姬 `qianji_0552`, 蟹姬 `xieji_0481` -- `.skel` + `.atlas` + page textures, plus 42 expression voice clips and a 22-line dialogue pool for 不知火). On first launch the program looks at the `res\spine` folder **next to its own dll** (`AppContext.BaseDirectory`, not an install path and not relative to the working directory) and registers every skeleton whose same-stem `.atlas` is present, up to the ten roster slots -- see `DeskSpine/src/BundledRoster.cs`. **If a config file already exists, or that folder does not exist, not one byte is touched**, so people who already set theirs up never get overwritten and a self-built clone behaves exactly as before.
>
> That registration is measured, not asserted: `bwp-animator-test.exe seed <unzipped dir>\BwpDesktopPet.dll <unzipped dir>\res\spine <empty dir>` runs the shipped dll against the real bundled assets in a throwaway config directory and reports "4 registered", with the tray rows rendering as 不知火 / 猫掌柜焕宴 / 千姬 / 蟹姬, and a second first-run returning 0 with the file unchanged. The rule itself is pinned by layer 27 of the `desk` gate, which uses synthetic fake skeletons in a temp directory and reads no real asset. **What is still marked SKIP: nobody has eyeballed the pet standing on a desktop after unzipping the attachment** -- on the development machine a config already exists, so first-run seeding is necessarily skipped there, and seeing it means moving `%LOCALAPPDATA%\DeskSpine` aside first.

---

## Introduction

This is a desktop pet software for Windows based on [Spine](https://esotericsoftware.com/spine-in-depth) animations.

- Supports custom Spine models and placing them on the desktop for interaction, including dynamic illustrations.
- Supports loading multiple skeletal model files split from the same character.
- Supports multiple Spine runtime versions.
- Supports pre-multiplied alpha texture format.
- Supports custom character interaction voice lines.
- Supports dynamic wallpaper effects embedded on the desktop.
- Supports various pre-set animation interaction schemes for multiple gacha games.

In addition, some extra features are provided.

- Spine Animation Export
    - Supports exporting full animation frame sequences in PNG format with a maximum resolution of 8k and a frame rate of up to 120 FPS.
    - Allows free adjustment of model size and position.
- Performance Monitor Window
    - Left-click the tray icon to view system resource usage, such as the CPU.
    - Records real-time and historical resource usage changes.
- Hourly Chime Tool
    - Supports a custom bubble icon for hourly chimes.

## Installation

Please go to the [Release](https://github.com/ww-rm/DeskSpine/releases) page to download the latest `msi` installer.

After installation, you can find the program in the desktop or Start menu. Once launched, you should see a tray icon appear shortly, and, unless something goes wrong, a default animated character will appear on your desktop.

## Adjusting Window Size and Position

- Use the left mouse button to **drag the window**.
- Use the right mouse button to **drag the sprite itself**, without changing the window's position.
- Double-click the right mouse button to show the window resize frame, allowing quick adjustment of the display area size.
- The sprite's own scaling ratio needs to be adjusted in the settings panel.

## How to Use the Animation Export Tool

The SpineTool includes an animation export tool. You can launch it from the tray icon's right-click menu after the sprite has been started, or directly run `SpineTool.exe` (or use the shortcut) from the installation directory.

The model loading method is the same as for the desktop pet. The screen on the right is a preview, where you can use the left mouse button to drag the position, or the mouse wheel to zoom in/out and adjust the desired frame location.

After clicking the export button, the tool will export the animation frame by frame according to the set resolution, frame rate, and the position/zoom of the preview on the right.

## Background Issues with the Desktop Pet

Due to rendering limitations, the implementation of non-rectangular windows uses a transparent color key (`crKey`), and `SetLayeredWindowAttributes` is used to cut out all pixels of this color. Therefore, some semi-transparent areas cannot blend perfectly with the desktop background, and can only blend with the background color.

A common issue is that semi-transparent glowing objects in the image may appear with the background color, such as gray, which can affect the visual quality. As a compromise, you can remove the semi-transparent areas from the image, which can be done using the edge processing tool in the SpineTool.

Of course, this issue does not exist in the animation export tool, as it exports accurate PNG sequences with semi-transparent frames.

---

*If you think this project is cool, please give it a :star: and share it with more people! :)*
