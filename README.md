# Yet Another Research Tree (YART)

**English** | [한국어](README.ko.md)

[![Build YART](https://github.com/csh1668/YetAnotherResearchTree/actions/workflows/build.yml/badge.svg)](https://github.com/csh1668/YetAnotherResearchTree/actions/workflows/build.yml)
[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](https://rimworldgame.com/)
[![Steam Subscribers](https://img.shields.io/steam/subscriptions/3745434121?logo=steam&label=Workshop%20subscribers&color=1b2838)](https://steamcommunity.com/sharedfiles/filedetails/?id=3745434121)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

YART is a RimWorld mod that replaces the research tab with a custom, graph-based tech tree.
It lays research out with a Sugiyama hierarchical layout and renders it lag-free through a custom GL
pipeline. It also provides fast, convenient search powered by a suffix array.

![Preview](About/Preview.png)

## Features

- **Dynamic graph layout** — automatically generates a readable, compact research tree.
- **Per-channel research queue** — research that can run in parallel (e.g. the Anomaly DLC or Vanilla
  Gravship Expanded) progresses on its own independent track.
- **Fast search** — search by research name, unlocked content, description, or `@mod`, with
  multi-language normalization.

## Installation

**Steam Workshop** — [subscribe to the mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3745434121) (recommended).

**Non-Steam / manual** — download `YetAnotherResearchTree.zip` from the
[latest release](https://github.com/csh1668/YetAnotherResearchTree/releases/latest) and extract the
`YetAnotherResearchTree` folder into your RimWorld `Mods/` directory.

## Building

Requires MSBuild (Visual Studio 2022 Build Tools) and the .NET Framework 4.7.2 target.

```powershell
msbuild Source/YART/YART.csproj -t:Restore -p:Configuration=Release-1.6
msbuild Source/YART/YART.csproj -t:Build   -p:Configuration=Release-1.6 -nologo
```

## License

[MIT License](LICENSE)
