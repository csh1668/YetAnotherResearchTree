#!/usr/bin/env python3
"""
Unihan_Readings.txt → 컴팩트 CJK 읽기 테이블 베이크.

YART 검색 i18n(Tier 2)용. 한자(CJK)를 라틴 로마자로 정규화하기 위한 글자별 읽기 테이블을 만든다.
  - 병음(중국어): kMandarin 값의 첫 항목에서 성조(결합표시) 제거 → 무성조 라틴 병음. 예) 電 → dian
  - 음독(일본어): kJapaneseOn 값의 첫 항목(이미 헵번 로마자) → 소문자. 예) 電 → den

런타임은 활성 언어로 둘 중 하나만 로드(중국어=병음, 일본어=음독)하므로, 같은 한자가
언어에 따라 다르게 정규화되는 충돌이 없다.

출력: Resources/cjk_readings.txt(.gz)
  형식(탭 구분, 헤더 1줄): "<hexCodepoint>\t<pinyin or '-'>\t<on or '-'>"
  둘 다 비면 줄 자체를 생략.

사용:
  python generate_cjk_readings.py <Unihan_Readings.txt> <out_dir>
"""
import sys, os, unicodedata, gzip

def strip_tones(s: str) -> str:
    # 성조 결합표시 제거(NFD). 병음 ü의 다이어레시스도 함께 제거되어 u로 떨어짐(검색 근사).
    nfd = unicodedata.normalize("NFD", s)
    base = "".join(c for c in nfd if not unicodedata.combining(c))
    return base.lower()

def is_ascii_latin(s: str) -> bool:
    return all(("a" <= c <= "z") for c in s)

def main():
    if len(sys.argv) != 3:
        print("usage: generate_cjk_readings.py <Unihan_Readings.txt> <out_dir>")
        sys.exit(1)
    src, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    pinyin = {}  # cp(int) -> str
    on = {}      # cp(int) -> str

    with open(src, encoding="utf-8") as f:
        for line in f:
            if not line.startswith("U+"):
                continue
            parts = line.rstrip("\n").split("\t")
            if len(parts) != 3:
                continue
            cp_hex, field, val = parts
            cp = int(cp_hex[2:], 16)
            first = val.split()[0] if val.split() else ""
            if not first:
                continue
            if field == "kMandarin":
                p = strip_tones(first)
                if p and is_ascii_latin(p):
                    pinyin[cp] = p
            elif field == "kJapaneseOn":
                o = first.lower()
                if o and is_ascii_latin(o):
                    on[cp] = o

    cps = sorted(set(pinyin) | set(on))
    out_txt = os.path.join(out_dir, "cjk_readings.txt")
    lines = ["# YART CJK readings (Unihan kMandarin/kJapaneseOn). cp<TAB>pinyin<TAB>on  ('-'=none)"]
    for cp in cps:
        lines.append(f"{cp:X}\t{pinyin.get(cp, '-')}\t{on.get(cp, '-')}")
    data = ("\n".join(lines) + "\n").encode("utf-8")

    with open(out_txt, "wb") as f:
        f.write(data)
    with gzip.open(out_txt + ".gz", "wb", compresslevel=9) as f:
        f.write(data)

    print(f"pinyin entries: {len(pinyin)}")
    print(f"on entries:     {len(on)}")
    print(f"rows:           {len(cps)}")
    print(f"txt bytes:      {len(data)}")
    print(f"gz bytes:       {os.path.getsize(out_txt + '.gz')}")
    # 샘플 검증 (콘솔 인코딩이 CJK 미지원이어도 죽지 않게)
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    for ch in "電気电气研究機械":
        cp = ord(ch)
        print(f"  U+{cp:04X}  pinyin={pinyin.get(cp,'-')}  on={on.get(cp,'-')}")

if __name__ == "__main__":
    main()
