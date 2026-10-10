param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

function Find-Iscc {
    $candidates = @(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    if (!$candidates) { throw 'ISCC.exe (Inno Setup 6) was not found. Install Inno Setup 6 first.' }
    return $candidates | Select-Object -First 1
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gitRoot = $repoRoot.Replace('\', '/')
$project = Join-Path $repoRoot 'src/Eslee.OneKey.App/Eslee.OneKey.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
if ($projectXml.Project.PropertyGroup.Version -ne $Version) { throw 'Tag/package version differs from project version' }
$iscc = Find-Iscc
$notes = Join-Path $repoRoot "docs/releases/v$Version.md"
if (!(Test-Path -LiteralPath $notes)) { throw 'Release notes are required' }
$sourceSha = & git -c "safe.directory=$gitRoot" -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source SHA' }
$dirty = @(& git -c "safe.directory=$gitRoot" -C $repoRoot status --porcelain).Count -gt 0
if ($env:CI -eq 'true' -and $dirty) { throw 'CI release source must be clean' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:ONEKEY_AUDIO_SMOKE = '0'
$env:ONEKEY_DISCORD_RPC_SMOKE = '0'
$output = Join-Path $repoRoot "artifacts/release/$Version"
$results = Join-Path $output 'TestResults'
$stage = Join-Path $repoRoot ('artifacts/staging/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results, $stage -Force | Out-Null
Push-Location $repoRoot
try {
    & dotnet test tests/Eslee.OneKey.Tests/Eslee.OneKey.Tests.csproj --configuration Release --logger 'trx;LogFileName=tests.trx' --results-directory $results --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Safe test suite failed; no package produced' }
    & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $stage --configfile NuGet.Config --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed' }
    if (!(Test-Path -LiteralPath (Join-Path $stage 'Eslee.OneKey.App.exe'))) { throw 'Published executable missing' }
    $archiveName = "eslee-OneKey-v$Version-win-x64-Portable.zip"
    $archive = Join-Path $output $archiveName
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if (!($zip.Entries.FullName -contains 'Eslee.OneKey.App.exe')) { throw 'Executable absent from portable archive' }
    } finally { $zip.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

    # 설치형은 포터블 zip과 같은 self-contained 게시본을 그대로 담는다.
    & $iscc '/Q' "/DAppVersion=$Version" "/DSourceDir=$stage" "/DOutputDir=$output" (Join-Path $repoRoot 'installer/eslee-onekey.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }
    $installerName = "eslee-OneKey-v$Version-win-x64-Setup.exe"
    $installer = Join-Path $output $installerName
    if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not created: $installer" }
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash

    @("$installerHash  $installerName", "$archiveHash  $archiveName") | Set-Content -LiteralPath (Join-Path $output 'sha256.txt') -Encoding ascii
    [xml]$testResults = Get-Content -LiteralPath (Join-Path $results 'tests.trx') -Raw
    $counters = $testResults.SelectSingleNode("//*[local-name()='Counters']")
    $binaryHashes = @(Get-ChildItem -LiteralPath $stage -Recurse -File |
        Where-Object { $_.Extension -in '.dll', '.exe' } | Sort-Object FullName |
        ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    [ordered]@{
        version = $Version
        source_sha = $sourceSha
        source_working_tree_dirty = $dirty
        configuration = 'Release'
        runtime = 'win-x64'
        self_contained = $true
        archive = $archiveName
        archive_sha256 = $archiveHash
        installer = $installerName
        installer_sha256 = $installerHash
        tests = [ordered]@{ total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed; skipped = $testResults.SelectNodes("//*[local-name()='UnitTestResult'][@outcome='NotExecuted']").Count }
        smoke_policy = 'Real audio/RPC opt-ins disabled in packaging; no user session/process operations'
        binary_hashes = $binaryHashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'validation-manifest.json') -Encoding utf8
    Copy-Item -LiteralPath $notes -Destination (Join-Path $output 'release-notes.md') -Force
    Write-Output "Installer ready: $installer"
    Write-Output "SHA256: $installerHash"
    Write-Output "Portable package ready: $archive"
    Write-Output "SHA256: $archiveHash"
} finally {
    Pop-Location
    $stageRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/staging'))
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($stage))) -eq $stageRoot -and (Split-Path -Leaf $stage) -match '^[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
