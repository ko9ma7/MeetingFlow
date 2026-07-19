# MeetingFlow 기여 가이드

## 개발 환경

- Windows 10/11
- .NET 8 SDK
- Python 3.13
- Visual Studio 2022 또는 호환 IDE

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-python.ps1
dotnet build .\MeetingFlow.slnx
dotnet test .\MeetingFlow.slnx
```

## 변경 원칙

- 녹음과 STT의 로컬 우선 원칙을 유지합니다.
- 원본 STT를 AI 결과로 덮어쓰지 않습니다.
- 전사 검토 전에 AI API를 자동 호출하지 않습니다.
- 화자 분리에 실패했을 때 가짜 화자 라벨을 만들지 않습니다.
- 모든 저장 텍스트는 UTF-8로 처리합니다.
- API 키, 토큰, 실제 회의 기록, 오디오를 커밋하지 않습니다.

## 제출 전 확인

1. 관련 기능의 실패·빈 상태·취소 상태를 확인합니다.
2. `dotnet build`와 `dotnet test`를 실행합니다.
3. Python 변경은 `python -m py_compile python-stt/meetingflow_stt.py`로 확인합니다.
4. 실제 회의 대신 합성 문장이나 공개 사용이 허용된 샘플을 사용합니다.
5. UI 변경은 1024×700과 1280×820에서 스크롤과 키보드 포커스를 확인합니다.

## 이슈 작성

재현 단계, 기대 결과, 실제 결과, 앱 버전, Windows 버전, 선택한 STT 프리셋을 적어 주세요. 민감한 오디오와 전사 원문은 첨부하지 마세요.

