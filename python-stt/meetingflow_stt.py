from __future__ import annotations

import argparse
import importlib.util
import json
import math
import os
import re
import sys
import tempfile
import threading
import wave
from array import array
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


def emit(event: str, **payload: object) -> None:
    print(json.dumps({"event": event, **payload}, ensure_ascii=False), flush=True)


def sanitize_transcript_text(value: str) -> str:
    text = value.replace("\ufffd", " ").strip()
    original_meaningful = [char for char in text if char.isalnum()]
    if len(original_meaningful) >= 8 and len(set(original_meaningful)) <= 2:
        return ""
    text = re.sub(r"[ㅋㅎㅠㅜ]{4,}", " ", text)
    text = re.sub(r"([^\s])\1{7,}", lambda match: match.group(1) * 3, text)
    text = re.sub(r"(?P<unit>[^\s]{1,6})(?:\s*(?P=unit)){4,}", lambda match: match.group("unit"), text)
    text = re.sub(r"\s+", " ", text).strip()
    if not text or re.fullmatch(r"[ㅋㅎㅠㅜ!?.~,\s]+", text):
        return ""
    meaningful = [char for char in text if char.isalnum()]
    if not meaningful or (len(meaningful) >= 8 and len(set(meaningful)) <= 2):
        return ""
    return text


def require_pyaudio():
    try:
        import pyaudio
        return pyaudio
    except ImportError as exc:
        raise RuntimeError("PyAudio가 설치되지 않았습니다. setup-python.ps1을 실행하세요.") from exc


def list_devices() -> None:
    pyaudio = require_pyaudio()
    audio = pyaudio.PyAudio()
    try:
        devices = []
        for index in range(audio.get_device_count()):
            info = audio.get_device_info_by_index(index)
            if int(info.get("maxInputChannels", 0)) > 0:
                devices.append({"index": index, "name": info.get("name", f"Input {index}")})
        emit("devices", devices=devices)
    finally:
        audio.terminate()


def record_audio(output: str, device: int | None, live_preview: bool = False) -> None:
    pyaudio = require_pyaudio()
    rate, channels, chunk = 16000, 1, 1600
    audio = pyaudio.PyAudio()
    stop = threading.Event()

    def wait_for_stop() -> None:
        for line in sys.stdin:
            if line.strip().lower() == "stop":
                stop.set()
                return

    threading.Thread(target=wait_for_stop, daemon=True).start()
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    stream = audio.open(
        format=pyaudio.paInt16,
        channels=channels,
        rate=rate,
        input=True,
        input_device_index=device,
        frames_per_buffer=chunk,
    )
    writer = wave.open(output, "wb")
    writer.setnchannels(channels)
    writer.setsampwidth(audio.get_sample_size(pyaudio.paInt16))
    writer.setframerate(rate)
    preview_writer = None
    preview_path = ""
    preview_bytes = 0
    preview_start = 0.0

    def open_preview_chunk():
        nonlocal preview_writer, preview_path, preview_bytes
        folder = Path(tempfile.gettempdir()) / "MeetingFlow" / "LiveDraft"
        folder.mkdir(parents=True, exist_ok=True)
        preview_path = str(folder / f"chunk-{os.getpid()}-{int(preview_start * 1000):09d}.wav")
        preview_writer = wave.open(preview_path, "wb")
        preview_writer.setnchannels(channels)
        preview_writer.setsampwidth(audio.get_sample_size(pyaudio.paInt16))
        preview_writer.setframerate(rate)
        preview_bytes = 0

    def complete_preview_chunk(create_next: bool):
        nonlocal preview_writer, preview_bytes, preview_start
        if preview_writer is None:
            return
        preview_writer.close()
        preview_writer = None
        duration = preview_bytes / float(rate * channels * audio.get_sample_size(pyaudio.paInt16))
        if duration >= 1.0:
            emit("preview_chunk", path=preview_path, start=preview_start, end=preview_start + duration)
            preview_start += duration
        else:
            try:
                Path(preview_path).unlink(missing_ok=True)
            except OSError:
                pass
        preview_bytes = 0
        if create_next:
            open_preview_chunk()

    if live_preview:
        open_preview_chunk()
    emit("recording_started", output=output, rate=rate)
    try:
        while not stop.is_set():
            data = stream.read(chunk, exception_on_overflow=False)
            writer.writeframesraw(data)
            if preview_writer is not None:
                preview_writer.writeframesraw(data)
                preview_bytes += len(data)
                if preview_bytes >= rate * channels * audio.get_sample_size(pyaudio.paInt16) * 8:
                    complete_preview_chunk(True)
            samples = array("h", data)
            peak = max((abs(value) for value in samples), default=0) / 32768.0
            emit("level", value=round(peak, 4))
    finally:
        complete_preview_chunk(False)
        stream.stop_stream()
        stream.close()
        writer.close()
        audio.terminate()
        emit("recording_stopped", output=output)


def analyze_audio(audio_path: str) -> dict[str, object]:
    try:
        import av
        import numpy as np

        total, sum_squares, peak, clipped = 0, 0.0, 0.0, 0
        with av.open(audio_path) as container:
            for frame in container.decode(audio=0):
                values = frame.to_ndarray().astype(np.float32, copy=False)
                if np.issubdtype(frame.to_ndarray().dtype, np.integer):
                    values /= float(np.iinfo(frame.to_ndarray().dtype).max)
                values = np.nan_to_num(values, copy=False)
                absolute = np.abs(values)
                total += int(values.size)
                sum_squares += float(np.sum(values * values, dtype=np.float64))
                peak = max(peak, float(np.max(absolute, initial=0.0)))
                clipped += int(np.count_nonzero(absolute >= 0.995))
        if total == 0:
            return {"rms_db": -120.0, "peak_db": -120.0, "clipping_percent": 0.0, "warning": "오디오 샘플을 읽지 못했습니다."}
        rms = math.sqrt(sum_squares / total)
        rms_db = 20.0 * math.log10(max(rms, 1e-6))
        peak_db = 20.0 * math.log10(max(peak, 1e-6))
        clipping_percent = clipped * 100.0 / total
        warnings = []
        if rms_db < -38.0:
            warnings.append("음량이 매우 작아 인식 오류가 늘 수 있습니다")
        if clipping_percent > 0.5:
            warnings.append("소리가 찌그러지는 클리핑이 감지되었습니다")
        return {
            "rms_db": round(rms_db, 1),
            "peak_db": round(peak_db, 1),
            "clipping_percent": round(clipping_percent, 3),
            "warning": " · ".join(warnings),
        }
    except Exception as exc:
        return {"rms_db": 0.0, "peak_db": 0.0, "clipping_percent": 0.0, "warning": f"음질 분석 생략: {exc}"}


def assign_speakers(audio_path: str, segments: list[dict[str, object]], mode: str, speaker_count: int) -> tuple[int, str, str]:
    if mode == "off":
        return 0, "사용 안 함", ""
    token = os.environ.get("MEETINGFLOW_HF_TOKEN", "").strip()
    if not token:
        return 0, "미실행", "Hugging Face 토큰이 없어 화자 분리를 건너뛰었습니다. 원문 정확도에는 영향이 없습니다."
    try:
        from pyannote.audio import Pipeline
        emit("stage", name="pyannote Community-1 화자 음성 구간 분석 중", percent=0.94)
        pipeline = Pipeline.from_pretrained("pyannote/speaker-diarization-community-1", token=token)
        if mode == "fixed":
            output = pipeline(audio_path, num_speakers=max(1, speaker_count))
        else:
            output = pipeline(audio_path, min_speakers=1, max_speakers=max(2, speaker_count))
        annotation = getattr(output, "exclusive_speaker_diarization", None) or output.speaker_diarization
        turns = [(float(turn.start), float(turn.end), str(speaker)) for turn, _, speaker in annotation.itertracks(yield_label=True)]
        label_map: dict[str, str] = {}
        for segment in segments:
            start, end = float(segment["start"]), float(segment["end"])
            overlaps = [(max(0.0, min(end, turn_end) - max(start, turn_start)), speaker) for turn_start, turn_end, speaker in turns]
            _, raw_label = max(overlaps, default=(0.0, ""))
            if raw_label:
                if raw_label not in label_map:
                    index = len(label_map)
                    label_map[raw_label] = chr(ord("A") + index) if index < 26 else str(index + 1)
                segment["speaker"] = label_map[raw_label]
        return len(label_map), "완료", ""
    except ImportError:
        return 0, "미설치", "pyannote.audio가 설치되지 않아 화자 분리를 건너뛰었습니다. 설정 화면의 설치 안내를 확인하세요."
    except Exception as exc:
        return 0, "실패", f"화자 분리 실패: {exc}"


def cpu_thread_count(quality_profile: str) -> int:
    logical_cores = max(2, os.cpu_count() or 8)
    profile_cap = {"cpu-fast": 6, "cpu-accurate": 8, "cpu-maximum": 10}.get(quality_profile, 8)
    return max(2, min(profile_cap, max(2, logical_cores // 2)))


def vad_settings(quality_profile: str) -> dict[str, object]:
    if quality_profile == "cpu-fast":
        return {"threshold": 0.50, "min_silence_duration_ms": 500, "speech_pad_ms": 350}
    return {"threshold": 0.35, "min_silence_duration_ms": 800, "speech_pad_ms": 500}


def is_model_prepared(model_dir: str, model: str) -> bool:
    normalized = model.lower()
    return any(normalized in str(path).lower() and path.stat().st_size > 1_000_000 for path in Path(model_dir).rglob("model.bin"))


def transcribe(audio_path: str, model: str, language: str, language_mode: str, model_dir: str, beam_size: int, initial_prompt: str, hotwords: str, content_profile: str, quality_profile: str, hallucination_guard: bool, diarization_mode: str, speaker_count: int) -> None:
    try:
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("faster-whisper가 설치되지 않았습니다. setup-python.ps1을 실행하세요.") from exc

    emit("audio_quality", **analyze_audio(audio_path))
    threads = cpu_thread_count(quality_profile)
    model_stage = "설치된 모델을 메모리에 불러오는 중" if is_model_prepared(model_dir, model) else "최초 모델 다운로드 중 · 완료 후에는 다시 받지 않습니다"
    emit("stage", name=f"faster-whisper {model} · {model_stage}", percent=-1.0)
    engine = WhisperModel(model, device="cpu", compute_type="int8", download_root=model_dir, cpu_threads=threads, num_workers=1)
    emit("stage", name=f"CPU 고정밀 분석 시작 · {model} · int8 · {threads} 스레드", percent=0.02)
    options = {
        "language": language if language_mode == "fixed" and language != "auto" else None,
        "multilingual": language_mode == "mixed",
        "vad_filter": True,
        "vad_parameters": vad_settings(quality_profile),
        "beam_size": max(1, beam_size),
        "patience": 1.2,
        "condition_on_previous_text": True,
        "initial_prompt": initial_prompt or None,
        "hotwords": hotwords or None,
        "temperature": 0.0,
        "word_timestamps": True,
    }
    if hallucination_guard:
        options.update({"repetition_penalty": 1.05, "hallucination_silence_threshold": 2.0})
    segments, info = engine.transcribe(audio_path, **options)
    duration = max(float(getattr(info, "duration", 0.0) or 0.0), 0.001)
    result_segments: list[dict[str, object]] = []
    stream_segments = diarization_mode == "off"
    for segment in segments:
        text = sanitize_transcript_text(segment.text)
        if not text:
            continue
        item = {"start": float(segment.start), "end": float(segment.end), "text": text, "speaker": ""}
        result_segments.append(item)
        segment_progress = min(0.03 + (float(segment.end) / duration) * 0.89, 0.92)
        if stream_segments:
            emit("segment", start=item["start"], end=item["end"], text=item["text"], speaker="", percent=segment_progress)
        else:
            emit("stage", name=f"음성 전사 중 · {float(segment.end):.0f}/{duration:.0f}초", percent=segment_progress)
    detected_speakers, diarization_status, diarization_warning = assign_speakers(audio_path, result_segments, diarization_mode, speaker_count)
    emit("diarization", status=diarization_status, speaker_count=detected_speakers, warning=diarization_warning)
    if not stream_segments:
        for segment in result_segments:
            emit(
                "segment",
                start=segment["start"],
                end=segment["end"],
                text=segment["text"],
                speaker=segment["speaker"],
                percent=0.97,
            )
    language_probs = getattr(info, "all_language_probs", None) or []
    emit(
        "complete",
        segments=len(result_segments),
        language=getattr(info, "language", language),
        language_probability=float(getattr(info, "language_probability", 0.0) or 0.0),
        top_languages=[{"language": item[0], "probability": float(item[1])} for item in language_probs[:5]],
    )


def live_transcribe(model: str, language: str, language_mode: str, model_dir: str, quality_profile: str) -> None:
    try:
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("faster-whisper가 설치되지 않았습니다. setup-python.ps1을 실행하세요.") from exc
    emit("live_loading", message=f"실시간 초안용 {model} 모델을 준비하고 있습니다")
    threads = cpu_thread_count(quality_profile)
    engine = WhisperModel(model, device="cpu", compute_type="int8", download_root=model_dir, cpu_threads=threads, num_workers=1)
    live_beam = 1 if quality_profile == "cpu-fast" else 3
    emit("live_ready", message=f"임시 자막 준비됨 · {model} · CPU {threads}스레드")
    last_clean_text = ""
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("command") == "stop":
                break
            if request.get("command") != "chunk":
                continue
            path = str(request.get("path", ""))
            offset = float(request.get("start", 0.0))
            segments, _ = engine.transcribe(
                path,
                language=language if language_mode == "fixed" and language != "auto" else None,
                beam_size=live_beam,
                vad_filter=True,
                vad_parameters=vad_settings(quality_profile),
                condition_on_previous_text=False,
                temperature=0.0,
            )
            texts, local_end = [], 0.0
            for segment in segments:
                value = sanitize_transcript_text(segment.text)
                if value:
                    texts.append(value)
                    local_end = max(local_end, float(segment.end))
            if texts:
                clean_text = sanitize_transcript_text(" ".join(texts))
                comparable = re.sub(r"[^\w]", "", clean_text, flags=re.UNICODE).lower()
                previous = re.sub(r"[^\w]", "", last_clean_text, flags=re.UNICODE).lower()
                if clean_text and comparable != previous:
                    emit("live_segment", start=offset, end=offset + local_end, text=clean_text)
                    last_clean_text = clean_text
            try:
                Path(path).unlink(missing_ok=True)
            except OSError:
                pass
        except Exception as exc:
            emit("error", message=f"실시간 초안 구간 처리 실패: {exc}", type=type(exc).__name__)


def health() -> None:
    result = {"python": sys.version.split()[0], "pyaudio": False, "faster_whisper": False, "pyannote": False}
    try:
        import pyaudio  # noqa: F401
        result["pyaudio"] = True
    except ImportError:
        pass
    result["pyannote"] = importlib.util.find_spec("pyannote") is not None and importlib.util.find_spec("pyannote.audio") is not None
    try:
        import faster_whisper  # noqa: F401
        result["faster_whisper"] = True
    except ImportError:
        pass
    emit("health", **result)


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("health")
    sub.add_parser("devices")
    record = sub.add_parser("record")
    record.add_argument("--output", required=True)
    record.add_argument("--device", type=int)
    record.add_argument("--live-preview", action="store_true")
    stt = sub.add_parser("transcribe")
    stt.add_argument("--input", required=True)
    stt.add_argument("--model", default="small")
    stt.add_argument("--language", default="ko")
    stt.add_argument("--language-mode", choices=["fixed", "auto", "mixed"], default="fixed")
    stt.add_argument("--model-dir", required=True)
    stt.add_argument("--beam-size", type=int, default=8)
    stt.add_argument("--initial-prompt", default="")
    stt.add_argument("--hotwords", default="")
    stt.add_argument("--content-profile", default="general")
    stt.add_argument("--quality-profile", choices=["cpu-fast", "cpu-accurate", "cpu-maximum"], default="cpu-accurate")
    stt.add_argument("--hallucination-guard", action="store_true")
    stt.add_argument("--diarization-mode", choices=["off", "auto", "fixed"], default="off")
    stt.add_argument("--speaker-count", type=int, default=2)
    live = sub.add_parser("live")
    live.add_argument("--model", default="base")
    live.add_argument("--language", default="ko")
    live.add_argument("--language-mode", choices=["fixed", "auto", "mixed"], default="fixed")
    live.add_argument("--model-dir", required=True)
    live.add_argument("--quality-profile", choices=["cpu-fast", "cpu-accurate", "cpu-maximum"], default="cpu-accurate")
    args = parser.parse_args()
    try:
        if args.command == "health": health()
        elif args.command == "devices": list_devices()
        elif args.command == "record": record_audio(args.output, args.device, args.live_preview)
        elif args.command == "transcribe": transcribe(args.input, args.model, args.language, args.language_mode, args.model_dir, args.beam_size, args.initial_prompt, args.hotwords, args.content_profile, args.quality_profile, args.hallucination_guard, args.diarization_mode, args.speaker_count)
        elif args.command == "live": live_transcribe(args.model, args.language, args.language_mode, args.model_dir, args.quality_profile)
        return 0
    except Exception as exc:
        emit("error", message=str(exc), type=type(exc).__name__)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
