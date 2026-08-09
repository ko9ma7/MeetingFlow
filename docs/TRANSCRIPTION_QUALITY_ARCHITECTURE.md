# MeetingFlow 음성 인식 품질 아키텍처

## 목표

- 원본 오디오는 로컬에 보존하고 STT 원문과 AI 편집본을 분리한다.
- 한국어·영어·중국어·일본어·독일어·프랑스어를 포함한 다국어 입력을 지원한다.
- 언어 자동 감지보다 주 언어 고정을 기본값으로 사용해 원치 않는 언어 혼입을 줄인다.
- 뉴스, 기술 회의, 인터뷰, 강의처럼 서로 다른 콘텐츠에 동일한 용어 프롬프트를 강제하지 않는다.
- STT 또는 Gemini 결과가 불확실할 때 사실처럼 교정하지 않고 확인 필요 상태를 남긴다.

## 현재 구현된 처리 흐름

1. PyAudio 마이크 또는 NAudio WASAPI 루프백으로 WAV를 로컬 저장한다.
2. 평균/최대 음량과 클리핑을 분석해 녹음 품질 경고를 만든다.
3. 사용자가 faster-whisper, CrisperWhisper 또는 이중 검증을 선택한다.
4. 고정·자동·혼합 언어 모드에 따라 언어 감지 정책을 적용한다.
5. faster-whisper는 Beam Search, VAD, 이전 문맥 비의존 디코딩, 반복·무음 환각 억제로 실행한다.
6. CrisperWhisper는 별도 Python 3.12 환경에서 1~10분 안전 구간과 5초 겹침으로 처리하며, 한 구간이 실패해도 성공 구간을 보존한다.
7. 이중 검증은 faster-whisper 결과를 먼저 확보하고 CrisperWhisper 실패 시 빠른 결과로 안전하게 완료한다.
8. 두 결과는 시간 겹침 구간의 토큰 집합 유사도로 비교하되 자동으로 사실을 합성하지 않는다.
9. 감지 언어와 신뢰도, 음질 진단, 처리 속도, 경고, 타임스탬프 원문을 기록에 함께 저장한다.
10. 사용자가 선택한 시간 범위와 보고서 형식만 Gemini에 전달한다.
11. Gemini JSON Schema 응답을 검증해 완성형 Markdown과 요약 데이터로 분리 저장한다.

## 언어 정책

- `fixed`: 선택한 주 언어를 Whisper에 강제한다. 한 언어 중심 회의·뉴스에 권장한다.
- `auto`: 전체 오디오의 주 언어를 자동 감지한다.
- `mixed`: faster-whisper의 다국어 감지를 활성화한다. 코드 스위칭 음성에 사용할 수 있으나 짧은 발화는 오인식할 수 있으므로 감지 신뢰도와 허용 언어 경고를 반드시 확인한다.

지원 목록은 한국어, 영어, 중국어, 일본어, 독일어, 프랑스어, 스페인어, 이탈리아어, 포르투갈어, 러시아어이며 Whisper 언어 코드로 변환해 전달한다.

## 콘텐츠 프로필

- 일반 회의·대화
- 뉴스·미디어
- 업무·프로젝트
- 기술·설계
- 인터뷰·면접
- 강의·교육
- 고객·영업 상담
- 법률·규정

사용자 기술 용어 사전은 기본적으로 꺼져 있다. 기술·제품 콘텐츠에서 사용자가 명시적으로 켠 경우에만 `initial_prompt`와 `hotwords`에 전달한다.

## 엔진 확장 전략

실시간 초안은 CPU에서도 현실적으로 실행 가능한 `faster-whisper` tiny/base를 사용한다. 확정 엔진은 faster-whisper, CrisperWhisper 2.0, 이중 검증, Whisper.net 폴백 중 선택한다. CrisperWhisper는 Windows CPU에서 정밀 후처리 역할이며 실시간으로 표시하지 않는다.

향후 엔진은 동일한 `LocalTranscript` 결과 계약으로 추가한다.

- Qwen3-ASR: 52개 언어·방언, 언어 식별과 타임스탬프가 필요한 GPU 고정확도 선택지. 별도 Python 환경과 모델 용량 확인 후 설치한다.
- WhisperX: 화자 분리와 단어 단위 정렬이 필요한 회의용 후처리 선택지. pyannote 토큰 및 GPU 요구 조건을 별도 설정으로 둔다.
- SeamlessM4T: 음성 번역 및 다국어 번역본 생성이 필요한 경우에만 선택적으로 사용한다.
- Voxtral: 클라우드 전송을 사용자가 명시적으로 허용한 경우에만 선택하는 API 엔진이다.
- NVIDIA Parakeet: 유럽 언어 중심 로컬 엔진 후보이며 한국어·중국어·일본어 기본 엔진으로 사용하지 않는다.

## 장문·실시간 안정성

- 실시간 초안은 한 번에 한 구간만 처리한다. 처리 중 다음 구간이 도착하면 임시 WAV만 지우고 원본 녹음은 계속 저장한다.
- skipped 구간 수와 실제 처리 배속을 UI 진단 로그에 남긴다.
- 확정 전사는 15초 간격으로 부분 원문을 회의 JSON에 원자적으로 저장한다.
- CrisperWhisper는 기본 1분 단위로 처리하고 5초를 겹쳐 경계 손실을 줄인다.
- 동일 문장 재출력, 저정보 문자 반복, 반복 n-gram 구간은 원문 저장 전에 제외하고 경고 수를 기록한다.
- 이중 검증은 두 원문을 모두 보존한다. 불일치가 있다는 이유만으로 Gemini나 규칙이 임의의 제3 문장을 만들지 않는다.

대용량 모델과 GPU 종속성은 앱 설치 과정에서 몰래 설치하지 않는다. 엔진 관리자 화면에서 예상 다운로드 크기, 장치 요구 사항, 개인정보 전송 여부를 보여준 뒤 사용자가 설치를 시작하도록 설계한다.

## 품질 평가 계획

- 언어별로 10분 이상의 뉴스·회의·인터뷰 검증 세트를 만든다.
- 정답 전사와 비교해 WER/CER, 고유명사 오류율, 숫자·날짜 오류율을 기록한다.
- 루프백과 마이크를 분리해 음량, 잡음, 클리핑 조건별 결과를 비교한다.
- AI 보고서는 사실 추가율, 미지원 추론, 결정사항·담당자 환각 여부를 별도로 평가한다.
- 모델이나 프롬프트가 바뀌면 같은 검증 세트로 회귀 테스트한 뒤 기본값을 변경한다.

## 참고한 공식 자료

- OpenAI Whisper: https://openai.com/index/whisper/
- faster-whisper: https://github.com/SYSTRAN/faster-whisper
- CrisperWhisper: https://github.com/nyrahealth/CrisperWhisper
- Gemini Structured Output: https://ai.google.dev/gemini-api/docs/structured-output
- WhisperX: https://github.com/m-bain/whisperX
- Qwen3-ASR: https://github.com/QwenLM/Qwen3-ASR
- Meta SeamlessM4T: https://ai.meta.com/blog/seamless-m4t/
- Mistral Voxtral: https://docs.mistral.ai/capabilities/audio_transcription/
