param([ValidateSet('auto','desktop','browser')][string]$Backend = 'auto')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$pythonPath = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonPath)) {
    throw 'Run python -m venv .venv, then .venv\Scripts\python.exe -m pip install -r requirements.txt from this folder first.'
}
& $pythonPath -m deskweave serve --backend $Backend --open
exit $LASTEXITCODE
