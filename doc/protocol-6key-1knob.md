# 6-key 1-knob 매크로패드 프로토콜 분석 (VID 0x1189 / PID 0x8840)

이 문서는 CH55x 계열 6키 1노브 매크로패드(VID `0x1189`(4489), PID `0x8840`(34880))에
키/노브 매핑을 기록하기 위해 실제 장치로 검증한 HID 프로토콜을 정리한다.

기존 `RSoft.MacroPad.BLL` 이 가정하는 프로토콜과 이 장치의 실제 동작 사이에 몇 가지
차이가 있어, `RSoft.MacroPad.Cli` 에서 이를 보정하여 동작을 확인했다.

## 요약

| 항목 | 값 |
| --- | --- |
| VendorId | `0x1189` (4489) |
| ProductId | `0x8840` (34880) |
| 대상 HID 인터페이스 | `mi_00` (vendor-defined collection, UsagePage `0xFF00`) |
| Report ID | `3` |
| Output Report 길이 | 65 바이트 (Report ID 1 + 데이터 64) |
| 매직 바이트 | `0xFD` |
| Layer 표기 | 1-based (`layer + 1`) |

## 물리 장치 식별

`config.txt` 에 등록된 경로 조각(path fragment)이 `mi_01` 로 되어 있었으나,
실제 설정 대상(vendor-defined collection)은 `mi_00` 인터페이스다.

```
USB\VID_1189&PID_8840&MI_00   <- 설정 대상 (HID 규격 공급업체 정의 장치, UsagePage 0xFF00)
USB\VID_1189&PID_8840&MI_01   <- 키보드/마우스/컨슈머 입력 (col01~col04)
```

`config.txt` 를 `4489:34880,mi_00,1` 로 수정해야 이 장치를 올바르게 연다.

## Windows 쓰기 방식

- Windows HID 스택을 감싸는 라이브러리(`HidLibrary` 의 `WriteReport`,
  `HidSharp` 의 stream `Write`)는 이 장치에 대해 `ERROR_INVALID_PARAMETER`(87) 로 실패한다.
- 저수준 `CreateFile` + overlapped `WriteFile` 로 직접 쓰면 정상 동작한다.
  - `CreateFile`: `GENERIC_READ | GENERIC_WRITE`, 공유 없음, `FILE_FLAG_OVERLAPPED`
  - 각 write 는 65바이트 버퍼(`buffer[0] = ReportId(3)`)로 전송
- Report ID 는 반드시 `3` 이어야 한다. `0` 또는 `2` 로 보내면 즉시 `ERROR_INVALID_PARAMETER`(87) 로 거부된다.

이 방식은 `src/RSoft.MacroPad.Cli/Usb/RawWinHidUsb.cs` 에 구현되어 있다.

## 메시지 레이아웃

모든 메시지는 65바이트다. 아래 오프셋은 Report ID 를 포함한 **wire 기준**이다
(`wire[0]` = Report ID).

```
wire[0]      0x03            Report ID
wire[1]      0xFD            bind 커맨드(매직 바이트)
wire[2]      key id          키/노브 식별자 (아래 표 참조)
wire[3]      layer + 1       레이어 (0-based 인덱스에 +1)
wire[4]      macro type      0x01 키보드 / 0x02 미디어 / 0x03 마우스
wire[5..9]   0x00            패딩
wire[10..]   payload         타입별 페이로드 (아래)
```

### key id (버튼/노브 식별자)

| 물리 입력 | key id |
| --- | --- |
| 버튼 1 ~ 6 | `0x01` ~ `0x06` |
| 노브 왼쪽 회전 | `0x10` |
| 노브 푸시 | `0x11` |
| 노브 오른쪽 회전 | `0x12` |

> 주의: `RSoft.MacroPad.BLL` 의 `InputActionMapper` 는 노브를 `0x0D`~`0x0F` 로 매핑하지만,
> 이 장치의 실제 노브 식별자는 `0x10`~`0x12` 다. 버튼(`0x01`~`0x06`)은 BLL 매핑과 일치한다.
> 노브 식별자는 후보값(`0x07~0x09`, `0x0D~0x0F`, `0x10~0x12`)에 서로 다른 키를 매핑한 뒤
> 실제 회전/푸시로 입력되는 문자를 관찰하여 확정했다.

### 키보드 페이로드 (macro type `0x01`)

```
wire[10]     press count     시퀀스 내 키 개수
wire[11..]   (modifier, keycode) 쌍의 반복
```

- `modifier` 비트: `0x01` Ctrl, `0x02` Shift, `0x04` Alt, `0x08` Win (오른쪽 modifier 는 상위 4비트)
- `keycode` 는 HID Usage(프로토콜 전용 KeyCode). BLL `KeyCode` enum 값과 동일.

예) 버튼 1 에 `/model ` (7글자) 매핑, layer 0:

```
03 FD 01 01 01 00 00 00 00 00 07 00 38 00 10 00 12 00 07 00 08 00 0F 00 2C ...
                                    │  └ '/'  └ 'm'  └ 'o'  └ 'd'  └ 'e'  └ 'l'  └ ' '
                                    └ count = 7
```

### 마우스 휠 페이로드 (macro type `0x03`)

```
wire[10]     0x03            마우스 액션 = 휠
wire[11]     modifier
wire[12..14] 0x00            패딩
wire[15]     delta           휠 델타 (부호 있는 값을 unsigned 로: 업 = 0x01, 다운 = 0xFF)
```

예) 노브 오른쪽 회전(`0x12`) = 스크롤 업, layer 0:

```
03 FD 12 01 03 00 00 00 00 00 03 00 00 00 00 01
                              │              └ delta = +1 (업)
                              └ 마우스 휠 액션
```

노브 왼쪽 회전(`0x10`) = 스크롤 다운:

```
03 FD 10 01 03 00 00 00 00 00 03 00 00 00 00 FF
                                             └ delta = -1(0xFF) (다운)
```

### 기타 마우스 액션 (참고, 미검증)

동일 계열 장치를 다룬 [ch57x-keyboard-tool](https://github.com/kriomant/ch57x-keyboard-tool)
기준의 레이아웃. 이 프로젝트에서는 휠만 검증했다.

- 클릭 (`wire[10] = 0x01`): `wire[11]` modifier, `wire[12]` 버튼 마스크(bit0 좌/bit1 우/bit2 중간)
- 이동/드래그 (`wire[10] = 0x05`): `wire[11]` modifier, `wire[12]` 버튼 마스크, `wire[13]` dx, `wire[14]` dy

### 미디어 페이로드 (macro type `0x02`, 참고)

```
wire[10..11] media code (little-endian u16)
```

## 커밋 시퀀스

바인딩 1건을 쓴 뒤 아래 3개 메시지로 확정한다.

```
03 AA AA            separator
03 FD FE FF         finalize (플래시 반영)
03 AA AA            terminator
```

이후 펌웨어가 반영할 시간(약 200ms)을 두는 것이 안전하다.

> 키보드 바인딩은 `03 FD FE FF` (finalize) 만으로도 반영되는 것을 확인했으나,
> 마우스 휠 바인딩은 위 3단계 전체 시퀀스로 반영을 확인했다.

## BLL 과의 차이 요약

`RSoft.MacroPad.BLL` 의 `ExtendedReport` 가 생성하는 리포트와 이 장치의 실제 요구사항 차이:

| 항목 | BLL 가정 | 실제 장치 | 보정 위치 |
| --- | --- | --- | --- |
| 매직 바이트 (`wire[1]`) | `0xFE` | `0xFD` | `RawWinHidUsb.Write` 에서 치환 |
| 레이어 (`wire[3]`) | 0-based | 1-based (`layer + 1`) | 설정에서 `layer: 1` 사용 |
| 노브 key id | `0x0D`~`0x0F` | `0x10`~`0x12` | CLI 에서 직접 페이로드 지정 |
| 마우스 macro type (`wire[4]`) | `0x02`(Multimedia) | `0x03`(Mouse) | CLI 에서 직접 페이로드 지정 |
| 마우스 휠 델타 위치 | `wire[13]` 부근 | `wire[15]` | CLI 에서 직접 페이로드 지정 |
| 쓰기 방식 | `HidLibrary`/`HidSharp` | overlapped `WriteFile` | `RawWinHidUsb` |

## 적용된 매핑 (Claude Code 제어용)

| 입력 | 동작 |
| --- | --- |
| 버튼 1 | `/model ` 타이핑 |
| 버튼 2 | `/rewind ` 타이핑 |
| 버튼 3 | `/resume ` 타이핑 |
| 버튼 4 | `/compact ` 타이핑 |
| 버튼 5 | Ctrl+C |
| 버튼 6 | `/new ` 타이핑 |
| 노브 오른쪽 회전 | 스크롤 업 |
| 노브 왼쪽 회전 | 스크롤 다운 |

버튼 매핑은 `src/RSoft.MacroPad.Cli/macros.yaml` (layer `1`) + `apply` 명령으로 기록했고,
노브 스크롤은 위 마우스 휠 프로토콜로 기록했다.

## 설정 조회(read-back) 가능 여부

이 펌웨어에는 **저장된 매핑을 되돌려주는 커맨드가 없다.** 설정용 인터페이스(`mi_00`)는
쓰기만 받는다. 따라서 현재 매핑을 확인하려면 키를 눌러 실제로 나오는 입력을 관찰해야 한다.

HID 레벨 직접 읽기도 불가능하다. 실제 장치에서 각 컬렉션을 열어본 결과:

| 컬렉션 | 장치 클래스 | 열기 결과 |
| --- | --- | --- |
| `MI_01 COL01` | Keyboard | 실패 (`kbdhid` 독점) |
| `MI_01 COL02` | Mouse | 실패 (`mouhid` 독점) |
| `MI_01 COL03` | Keyboard | 실패 |
| `MI_01 COL04` | HIDClass | 성공 (input 3바이트) |
| `MI_00` | HIDClass | 성공 (설정용, 65바이트) |

버튼 입력(`COL01`/`COL03`)과 노브 스크롤(`COL02`)이 모두 Windows 드라이버에 독점 점유되어
있어, 입력 관찰은 **Win32 Raw Input API**(`RIDEV_INPUTSINK` + message-only 윈도우)로만 가능하다.
`RSoft.MacroPad.Cli` 의 `dump-input` 명령이 이 방식으로 구현되어 있다
(`src/RSoft.MacroPad.Cli/Diagnostics/`).

Raw Input 등록은 장치 단위가 아니라 usage page 단위이므로 다른 키보드/마우스 리포트도
프로세스에 도달한다. `dump-input` 은 디코딩 전에 장치 핸들로 필터링하여 대상 매크로패드
입력만 출력한다. 또한 Raw Input 은 대화형 window station 에 종속되므로, 서비스나 session 0
컨텍스트에서 실행하면 장치 목록이 비어 있게 나온다.

## 참고 자료

- [kriomant/ch57x-keyboard-tool](https://github.com/kriomant/ch57x-keyboard-tool) — 1189:8840 포함 지원, 마우스/휠 레이아웃 출처
- [mikhailvs/macropad](https://github.com/mikhailvs/macropad) — 키보드 바인딩 및 커밋 프로토콜 리버스엔지니어링
