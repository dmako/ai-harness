param(
    [string] $SourceRepository = 'C:/repos/personal/ai-harness',
    [string] $Go = 'go'
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path "$PSScriptRoot/../../../..").Path
$output = Join-Path $repository 'artifacts/storage-oracle'
$source = Join-Path $output 'source'
if (Test-Path -LiteralPath $source) { throw "Use a fresh output directory: $source already exists." }
New-Item -ItemType Directory -Force $source | Out-Null
$archive = Join-Path $output 'source.tar'
& git -C $SourceRepository archive -o $archive 9cb401dd2de4f85dd089fea86a0b139d81b178c6
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the pinned Go source.' }
& tar -xf $archive -C $source
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract the pinned Go source.' }
$bridge = Join-Path $source 'cmd/storage-compat'
New-Item -ItemType Directory -Force $bridge | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/main.go" -Destination "$bridge/main.go"
$env:GOCACHE = Join-Path $output 'cache'
$env:GOMODCACHE = Join-Path $output 'modules'
$env:GOTELEMETRY = 'off'
$env:GOENV = 'off'
$env:GOTOOLCHAIN = 'local'
$binary = Join-Path $output $(if ($IsWindows) { 'storage-oracle.exe' } else { 'storage-oracle' })
Push-Location $source
try {
    & $Go build -mod=readonly -buildvcs=false -trimpath -o $binary ./cmd/storage-compat
    if ($LASTEXITCODE -ne 0) { throw 'Go storage oracle build failed.' }
} finally {
    Pop-Location
}
"Set HARNESS_GO_STORAGE_ORACLE=$binary before running integration tests."
