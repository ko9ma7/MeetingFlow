param(
    [switch]$SkipCrisper
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $root 'python-stt\.venv'
$crisperVenv = Join-Path $root 'python-stt\.crisper-venv'

if (Get-Command py -ErrorAction SilentlyContinue) {
    & py -3.13 -m venv $venv
} else {
    & python -m venv $venv
}

$python = Join-Path $venv 'Scripts\python.exe'
& $python -m pip install --upgrade pip
& $python -m pip install -r (Join-Path $root 'python-stt\requirements.txt')
& $python (Join-Path $root 'python-stt\meetingflow_stt.py') health

if ($SkipCrisper) {
    Write-Host 'faster-whisper runtime is ready.'
    exit 0
}

if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw 'CrisperWhisper Windows 환경에는 Python 3.12가 필요합니다. Python 3.12를 설치한 뒤 다시 실행하세요.'
}
& py -3.12 -m venv $crisperVenv
$crisperPython = Join-Path $crisperVenv 'Scripts\python.exe'
& $crisperPython -m pip install --upgrade pip
& $crisperPython -m pip install -r (Join-Path $root 'python-stt\requirements-crisper.txt')
& $crisperPython (Join-Path $root 'python-stt\meetingflow_stt.py') crisper-health
