"""YART 비주얼 v2 텍스처 에셋 생성기.

모든 에셋은 흰색 RGB + 알파로 생성하며, 게임 코드에서 vertex color로 틴트한다.
사용법: python generate_assets.py  (출력: <mod>/Textures/YART/)
"""
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[3] / "Textures" / "YART"
OUT.mkdir(parents=True, exist_ok=True)

# 고정 시드 (재생성해도 동일한 노이즈)
random.seed(20260610)

SS = 4  # 슈퍼샘플링 배율 (안티앨리어싱)


def save(img: Image.Image, name: str):
    path = OUT / name
    img.save(path)
    print(f"  {path.name}  {img.size[0]}x{img.size[1]}")


def glow_radial(size=256):
    """방사형 소프트 글로우. alpha = (1 - r)^2.4, 중심부 약간 강조."""
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    px = img.load()
    c = (size - 1) / 2
    for y in range(size):
        for x in range(size):
            r = math.hypot(x - c, y - c) / c
            if r >= 1.0:
                a = 0.0
            else:
                a = (1.0 - r) ** 2.4
                a += 0.25 * max(0.0, 1.0 - r * 4) ** 2  # 중심 코어
            px[x, y] = (255, 255, 255, min(255, int(a * 255)))
    save(img, "GlowRadial.png")


def node_panel(size=64, radius=12):
    """라운드 사각형 채움 (9-slice용)."""
    big = size * SS
    img = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius * SS, fill=255)
    alpha = img.resize((size, size), Image.LANCZOS)
    out = Image.merge("RGBA", [Image.new("L", (size, size), 255)] * 3 + [alpha])
    save(out, "NodePanel.png")


def node_panel_border(size=64, radius=12, width=3):
    """라운드 사각형 테두리 (9-slice용). width는 텍스처 픽셀 기준."""
    big = size * SS
    img = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(
        [SS // 2, SS // 2, big - 1 - SS // 2, big - 1 - SS // 2],
        radius=radius * SS, outline=255, width=width * SS)
    alpha = img.resize((size, size), Image.LANCZOS)
    out = Image.merge("RGBA", [Image.new("L", (size, size), 255)] * 3 + [alpha])
    save(out, "NodePanelBorder.png")


def noise_tile(size=512):
    """타일러블 그레인. 픽셀 단위 랜덤이라 자연스럽게 타일링됨."""
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            a = int(random.random() ** 2 * 90)  # 대부분 어둡고 드문드문 밝은 입자
            px[x, y] = (255, 255, 255, a)
    save(img, "NoiseTile.png")


def vignette(size=512):
    """비네팅: 중심 투명 -> 가장자리 어두움. RGB는 검정, 알파에 농도."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = img.load()
    c = (size - 1) / 2
    for y in range(size):
        for x in range(size):
            # 사각형에 맞춘 타원 거리
            r = math.hypot((x - c) / c, (y - c) / c) / math.sqrt(2)
            a = max(0.0, (r - 0.55) / 0.45) ** 1.8
            px[x, y] = (0, 0, 0, min(255, int(a * 200)))
    save(img, "Vignette.png")


def edge_line(width=4, height=16):
    """엣지 리본 단면 프로파일. V(세로): 중앙 불투명 코어 + 양끝 소프트 폴오프.
    리본 쿼드의 폭 방향에 V를 매핑하면 바이리니어 필터링이 안티앨리어싱이 된다."""
    img = Image.new("RGBA", (width, height), (255, 255, 255, 0))
    px = img.load()
    c = (height - 1) / 2
    for y in range(height):
        d = abs(y - c) / c  # 0(중앙) ~ 1(가장자리)
        if d <= 0.35:
            a = 1.0
        else:
            a = max(0.0, (1.0 - d) / 0.65) ** 1.8
        for x in range(width):
            px[x, y] = (255, 255, 255, int(a * 255))
    save(img, "EdgeLine.png")


def icon(name, draw_fn, size=32):
    big = size * SS
    img = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(img)
    draw_fn(d, big)
    alpha = img.resize((size, size), Image.LANCZOS)
    out = Image.merge("RGBA", [Image.new("L", (size, size), 255)] * 3 + [alpha])
    save(out, f"{name}.png")


def draw_lock(d, s):
    u = s / 32
    d.rounded_rectangle([7 * u, 14 * u, 25 * u, 28 * u], radius=2 * u, fill=255)
    d.arc([10 * u, 4 * u, 22 * u, 18 * u], 180, 360, fill=255, width=int(3 * u))


def draw_check(d, s):
    u = s / 32
    d.line([6 * u, 17 * u, 13 * u, 24 * u], fill=255, width=int(4 * u))
    d.line([13 * u, 24 * u, 26 * u, 8 * u], fill=255, width=int(4 * u))


def draw_play(d, s):
    u = s / 32
    d.polygon([9 * u, 6 * u, 9 * u, 26 * u, 26 * u, 16 * u], fill=255)


def draw_queue(d, s):
    u = s / 32
    for i, y in enumerate((7, 14, 21)):
        d.rounded_rectangle([7 * u, y * u, 25 * u, (y + 4) * u], radius=u, fill=255)


def _star_pts(s, outer=0.47, inner=0.20):
    """5각 별 꼭짓점 10개 (외곽/내부 반지름 교대). 위쪽 꼭짓점부터 시계방향."""
    cx = cy = s / 2
    pts = []
    for i in range(10):
        ang = -math.pi / 2 + i * math.pi / 5
        r = (outer if i % 2 == 0 else inner) * s
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    return pts


def draw_star(d, s):
    """채운 별 (즐겨찾기 ON — 코드에서 노란색 틴트)."""
    d.polygon(_star_pts(s), fill=255)


def draw_star_hollow(d, s):
    """외곽선 별 (즐겨찾기 OFF — 비어있는 별)."""
    pts = _star_pts(s)
    d.line(pts + [pts[0]], fill=255, width=max(1, int(2.4 * s / 32)), joint="curve")


def draw_swap(d, s):
    """전환 아이콘: 상단=오른쪽 화살표, 하단=왼쪽 화살표 (두 화살표로 swap 표현)."""
    u = s / 32
    w = int(3 * u)
    # 상단 → 오른쪽
    d.line([8 * u, 12 * u, 22 * u, 12 * u], fill=255, width=w)
    d.polygon([22 * u, 7 * u, 22 * u, 17 * u, 28 * u, 12 * u], fill=255)
    # 하단 ← 왼쪽
    d.line([10 * u, 20 * u, 24 * u, 20 * u], fill=255, width=w)
    d.polygon([10 * u, 15 * u, 10 * u, 25 * u, 4 * u, 20 * u], fill=255)


if __name__ == "__main__":
    print(f"Output: {OUT}")
    glow_radial()
    node_panel()
    node_panel_border()
    noise_tile()
    vignette()
    edge_line()
    icon("IconLock", draw_lock)
    icon("IconCheck", draw_check)
    icon("IconPlay", draw_play)
    icon("IconQueue", draw_queue)
    icon("IconSwap", draw_swap)
    icon("IconStar", draw_star)
    icon("IconStarHollow", draw_star_hollow)
    print("done")
