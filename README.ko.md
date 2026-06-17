# Yet Another Research Tree (YART)

[English](README.md) | **한국어**

[![Build YART](https://github.com/csh1668/YetAnotherResearchTree/actions/workflows/build.yml/badge.svg)](https://github.com/csh1668/YetAnotherResearchTree/actions/workflows/build.yml)
[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](https://rimworldgame.com/)
[![Steam Subscribers](https://img.shields.io/steam/subscriptions/3745434121?logo=steam&label=Workshop%20subscribers&color=1b2838)](https://steamcommunity.com/sharedfiles/filedetails/?id=3745434121)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

YART는 림월드 모드로, 연구 탭을 그래프 기반의 커스텀 테크 트리로 대체합니다.
Sugiyama 계층 레이아웃으로 배치하고, 커스텀 GL 파이프라인으로 렉 없이 렌더링합니다. 접미사 배열 (Suffix Array)를 사용한 매우 빠르고 편리한 검색 기능을 제공합니다.

![Preview](About/Preview.png)

## 특징

- **동적 그래프 레이아웃** — 가독성 좋고 컴팩트한 연구 트리를 자동으로 생성합니다.
- **채널별 연구 큐** — 아노말리 DLC나 Vanilla Gravship Expanded와 같이 병렬 실행이 가능한 연구들이 독립된 트랙으로 진행됩니다.
- **빠른 검색** — 연구 이름, 해금 콘텐츠, 설명, `@모드`로 검색하며 다국어 정규화를 지원합니다.

## 설치

**스팀 창작마당** — [모드 구독하기](https://steamcommunity.com/sharedfiles/filedetails/?id=3745434121) (권장).

**비스팀 / 수동 설치** — [최신 릴리스](https://github.com/csh1668/YetAnotherResearchTree/releases/latest)에서
`YetAnotherResearchTree.zip`을 받아 `YetAnotherResearchTree` 폴더를 림월드 `Mods/` 디렉터리에
압축 해제합니다.

## 빌드하기

MSBuild (Visual Studio 2022 Build Tools)와 .NET Framework 4.7.2 타깃이 필요합니다.

```powershell
msbuild Source/YART/YART.csproj -t:Restore -p:Configuration=Release-1.6
msbuild Source/YART/YART.csproj -t:Build   -p:Configuration=Release-1.6 -nologo
```

## 라이선스

[MIT License](LICENSE)
