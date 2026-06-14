# LayoutHarness

게임을 키지 않고 SugiyamaLayout을 실행 및 시각화하는 하네스입니다.

## 준비물

- 그래프 layout graph 파일

그래프 파일은 RimWorld에서 개발자 모드를 켜고 모드 설정 -> Yet Another Research Tree -> 레이아웃 그래프 내보내기 (개발용)" 버튼을 누르면 바탕화면에 JSON이 써진다.

```
%USERPROFILE%\Desktop\YART_layout_graph.json
```

## 사용
```powershell
cd Source\LayoutHarness
$dump = "$env:USERPROFILE\Desktop\YART_layout_graph.json"

dotnet run -c Release -- --in "$dump" --list
dotnet run -c Release -- --in "$dump" --graph Unified --out unified.svg
dotnet run -c Release -- --in "$dump" --graph Unified --dump --out unified.svg
```