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
import time
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


def is_repeated_segment(value: str, recent: list[str]) -> bool:
    normalized = re.sub(r"[^\w]", "", value, flags=re.UNICODE).lower()
    if len(normalized) < 6:
        return False
    if any(normalized == re.sub(r"[^\w]", "", item, flags=re.UNICODE).lower() for item in recent[-6:]):
        return True
    words = re.findall(r"[\w]+", value.lower(), flags=re.UNICODE)
    if len(words) >= 8:
        window = " ".join(words[:4])
        if window and " ".join(words).count(window) >= 3:
            return True
    return False


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
                if preview_bytes >= rate * channels * audio.get_sample_size(pyaudio.paInt16) * 4:
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


def decode_audio_mono_16k(audio_path: str):
    import av
    import numpy as np

    resampler = av.AudioResampler(format="flt", layout="mono", rate=16000)
    samples = []
    with av.open(audio_path) as container:
        for frame in container.decode(audio=0):
            for converted in resampler.resample(frame):
                samples.append(converted.to_ndarray().reshape(-1).astype(np.float32, copy=False))
        for converted in resampler.resample(None):
            samples.append(converted.to_ndarray().reshape(-1).astype(np.float32, copy=False))
    if not samples:
        raise RuntimeError("오디오 샘플을 읽지 못했습니다.")
    return np.concatenate(samples)


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


def vad_settings(quality_profile: str, vad_profile: str = "balanced") -> dict[str, object]:
    if vad_profile == "noisy":
        return {"threshold": 0.60, "min_silence_duration_ms": 500, "speech_pad_ms": 300}
    if vad_profile == "sensitive":
        return {"threshold": 0.30, "min_silence_duration_ms": 900, "speech_pad_ms": 500}
    if quality_profile == "cpu-fast":
        return {"threshold": 0.50, "min_silence_duration_ms": 500, "speech_pad_ms": 350}
    return {"threshold": 0.35, "min_silence_duration_ms": 800, "speech_pad_ms": 500}


def is_model_prepared(model_dir: str, model: str) -> bool:
    normalized = model.lower()
    return any(normalized in str(path).lower() and path.stat().st_size > 1_000_000 for path in Path(model_dir).rglob("model.bin"))


def transcribe(audio_path: str, model: str, language: str, language_mode: str, model_dir: str, beam_size: int, initial_prompt: str, hotwords: str, content_profile: str, quality_profile: str, hallucination_guard: bool, diarization_mode: str, speaker_count: int) -> None:
    started_at = time.perf_counter()
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
        "condition_on_previous_text": False,
        "initial_prompt": initial_prompt or None,
        "hotwords": hotwords or None,
        "temperature": 0.0,
        "word_timestamps": True,
        "compression_ratio_threshold": 2.4,
        "log_prob_threshold": -1.0,
        "no_speech_threshold": 0.6,
    }
    if hallucination_guard:
        options.update({"repetition_penalty": 1.05, "hallucination_silence_threshold": 2.0})
    segments, info = engine.transcribe(audio_path, **options)
    duration = max(float(getattr(info, "duration", 0.0) or 0.0), 0.001)
    result_segments: list[dict[str, object]] = []
    recent_texts: list[str] = []
    rejected_repetitions = 0
    stream_segments = diarization_mode == "off"
    for segment in segments:
        text = sanitize_transcript_text(segment.text)
        if not text:
            continue
        if hallucination_guard and is_repeated_segment(text, recent_texts):
            rejected_repetitions += 1
            continue
        recent_texts.append(text)
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
    if rejected_repetitions:
        emit("warning", message=f"반복·환각 의심 구간 {rejected_repetitions}개를 제외했습니다")
    processing_seconds = time.perf_counter() - started_at
    emit(
        "complete",
        segments=len(result_segments),
        language=getattr(info, "language", language),
        language_probability=float(getattr(info, "language_probability", 0.0) or 0.0),
        top_languages=[{"language": item[0], "probability": float(item[1])} for item in language_probs[:5]],
        processing_seconds=round(processing_seconds, 2),
        realtime_factor=round(duration / max(processing_seconds, 0.001), 2),
    )


def live_transcribe(model: str, language: str, language_mode: str, model_dir: str, quality_profile: str, vad_profile: str) -> None:
    try:
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("faster-whisper가 설치되지 않았습니다. setup-python.ps1을 실행하세요.") from exc
    emit("live_loading", message=f"실시간 초안용 {model} 모델을 준비하고 있습니다")
    threads = cpu_thread_count(quality_profile)
    engine = WhisperModel(model, device="cpu", compute_type="int8", download_root=model_dir, cpu_threads=threads, num_workers=1)
    live_beam = 1
    emit("live_ready", message=f"임시 자막 준비됨 · {model} · CPU {threads}스레드")
    last_clean_text = ""
    for line in sys.stdin:
        path = ""
        try:
            request = json.loads(line)
            if request.get("command") == "stop":
                break
            if request.get("command") != "chunk":
                continue
            path = str(request.get("path", ""))
            offset = float(request.get("start", 0.0))
            chunk_started_at = time.perf_counter()
            segments, _ = engine.transcribe(
                path,
                language=language if language_mode == "fixed" and language != "auto" else None,
                beam_size=live_beam,
                vad_filter=True,
                vad_parameters=vad_settings(quality_profile, vad_profile),
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
            processing_seconds = time.perf_counter() - chunk_started_at
            audio_seconds = 0.0
            try:
                with wave.open(path, "rb") as reader:
                    audio_seconds = reader.getnframes() / float(reader.getframerate())
            except (OSError, wave.Error):
                pass
            emit("live_chunk_done", processing_seconds=round(processing_seconds, 2), audio_seconds=round(audio_seconds, 2), speech=bool(texts), realtime_factor=round(max(audio_seconds, 0.001) / max(processing_seconds, 0.001), 2))
            try:
                Path(path).unlink(missing_ok=True)
            except OSError:
                pass
        except Exception as exc:
            try:
                Path(path).unlink(missing_ok=True)
            except OSError:
                pass
            emit("error", message=f"실시간 초안 구간 처리 실패: {exc}", type=type(exc).__name__)


def crisper_transcribe(audio_path: str, model: str, language: str, mode: str, chunk_seconds: int) -> None:
    try:
        from crisperwhisper import CrisperWhisperModel
    except ImportError as exc:
        raise RuntimeError("CrisperWhisper가 설치되지 않았습니다. setup-python.ps1을 다시 실행하세요.") from exc

    emit("audio_quality", **analyze_audio(audio_path))
    emit("stage", name=f"CrisperWhisper 2.0 {model} 모델 준비 · CPU 정밀 후처리", percent=-1.0)
    engine = CrisperWhisperModel(model, backend="transformers", device="cpu", compute_type="float32")
    emit("stage", name="CrisperWhisper 장문 연속 전사 · 실시간 초안과 별도로 실행", percent=0.02)
    started_at = time.perf_counter()
    audio = decode_audio_mono_16k(audio_path)
    duration = max(len(audio) / 16000.0, 0.001)
    block_samples = max(15, min(chunk_seconds, 120)) * 16000
    overlap_samples = 5 * 16000
    starts = list(range(0, len(audio), max(block_samples - overlap_samples, 1)))
    recent_texts: list[str] = []
    rejected = 0
    accepted = 0
    detected_language = language
    failed_blocks = 0
    for block_index, sample_start in enumerate(starts):
        sample_end = min(sample_start + block_samples, len(audio))
        block_start = sample_start / 16000.0
        try:
            result = engine.transcribe(
                audio[sample_start:sample_end],
                sr=16000,
                language=language if language != "auto" else "ko",
                mode=mode,
                longform_strategy="continuation",
                timestamp_aware_drop=True,
                temperature_fallback=True,
                hallucination_mitigation=False,
                early_eot_recovery=True,
                word_timestamps=False,
            )
            detected_language = result.language
            chunks = result.chunks or []
            if not chunks:
                chunks = [type("Chunk", (), {"start_sec": 0.0, "end_sec": result.duration, "text": result.text})()]
            for chunk in chunks:
                start = block_start + float(chunk.start_sec)
                end = min(block_start + float(chunk.end_sec), duration)
                if block_index > 0 and end <= block_start + 5.0:
                    continue
                text = sanitize_transcript_text(str(chunk.text))
                if not text:
                    continue
                if is_repeated_segment(text, recent_texts):
                    rejected += 1
                    continue
                recent_texts.append(text)
                accepted += 1
                emit("segment", start=start, end=end, text=text, speaker="", percent=min(0.03 + end / duration * 0.93, 0.96))
        except Exception as exc:
            failed_blocks += 1
            emit("warning", message=f"{block_start / 60:.1f}분 구간 처리 실패 · 다음 구간을 계속합니다: {exc}")
        if sample_end >= len(audio):
            break
    if rejected:
        emit("warning", message=f"CrisperWhisper 반복·환각 의심 구간 {rejected}개를 제외했습니다")
    if failed_blocks:
        emit("warning", message=f"전체 {len(starts)}개 장문 구간 중 {failed_blocks}개가 실패했습니다. 성공 구간은 보존했습니다")
    processing_seconds = time.perf_counter() - started_at
    emit(
        "complete",
        segments=accepted,
        language=detected_language,
        language_probability=0.0,
        processing_seconds=round(processing_seconds, 2),
        realtime_factor=round(duration / max(processing_seconds, 0.001), 2),
    )


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


def crisper_health() -> None:
    available = importlib.util.find_spec("crisperwhisper") is not None and importlib.util.find_spec("av") is not None
    emit("crisper_health", python=sys.version.split()[0], crisperwhisper=available)


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("health")
    sub.add_parser("crisper-health")
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
    live.add_argument("--vad-profile", choices=["balanced", "noisy", "sensitive"], default="balanced")
    crisper = sub.add_parser("crisper-transcribe")
    crisper.add_argument("--input", required=True)
    crisper.add_argument("--model", choices=["small", "medium", "turbo", "large"], default="small")
    crisper.add_argument("--language", default="ko")
    crisper.add_argument("--mode", choices=["intended", "verbatim"], default="intended")
    crisper.add_argument("--chunk-seconds", type=int, default=30)
    args = parser.parse_args()
    try:
        if args.command == "health": health()
        elif args.command == "crisper-health": crisper_health()
        elif args.command == "devices": list_devices()
        elif args.command == "record": record_audio(args.output, args.device, args.live_preview)
        elif args.command == "transcribe": transcribe(args.input, args.model, args.language, args.language_mode, args.model_dir, args.beam_size, args.initial_prompt, args.hotwords, args.content_profile, args.quality_profile, args.hallucination_guard, args.diarization_mode, args.speaker_count)
        elif args.command == "live": live_transcribe(args.model, args.language, args.language_mode, args.model_dir, args.quality_profile, args.vad_profile)
        elif args.command == "crisper-transcribe": crisper_transcribe(args.input, args.model, args.language, args.mode, args.chunk_seconds)
        return 0
    except Exception as exc:
        emit("error", message=str(exc), type=type(exc).__name__)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
