# 선택 기능: 화자 분리 설치

MeetingFlow의 기본 STT는 화자 분리 없이도 동작한다. 화자 A·B·C 구분이 필요할 때만 아래 구성 요소를 설치한다.

## 준비

1. [pyannote Community-1](https://huggingface.co/pyannote/speaker-diarization-community-1) 페이지에서 사용 조건에 동의한다.
2. Hugging Face 읽기 토큰을 만든다.
3. MeetingFlow 설정의 `화자 분리` 카드에 토큰을 입력한다.

## 설치 명령

프로젝트 폴더에서 다음 명령을 실행한다.

```powershell
.\python-stt\.venv\Scripts\python.exe -m pip install --upgrade pyannote.audio
```

설치 후 MeetingFlow를 다시 시작한다. 설정 화면에 `pyannote Community-1 엔진 설치됨`이 표시되면 사용할 수 있다.

## 사용 방식

- `자동 감지`: 입력한 수를 최대 화자 수로 사용한다.
- `화자 수 고정`: 정확히 입력한 수의 화자로 분리한다.
- 결과는 `화자 A`, `화자 B`처럼 표시된다. 이름은 사용자가 전사 검토 단계에서 바꿀 수 있다.

## 주의

- 첫 실행은 모델 다운로드 때문에 오래 걸릴 수 있다.
- CPU에서도 가능하지만 긴 회의는 GPU가 유리하다.
- 겹쳐 말하기, 한 마이크를 멀리서 공유하는 환경, 매우 짧은 발화에서는 화자 구분이 틀릴 수 있다.
- 설치나 토큰에 문제가 생겨도 MeetingFlow는 화자 없는 STT 결과를 보존한다.

