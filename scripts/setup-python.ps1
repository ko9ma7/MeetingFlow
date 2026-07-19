$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $root 'python-stt\.venv'

if (Get-Command py -ErrorAction SilentlyContinue) {
    & py -3.13 -m venv $venv
} else {
    & python -m venv $venv
}

$python = Join-Path $venv 'Scripts\python.exe'
& $python -m pip install --upgrade pip
& $python -m pip install -r (Join-Path $root 'python-stt\requirements.txt')
& $python (Join-Path $root 'python-stt\meetingflow_stt.py') health
