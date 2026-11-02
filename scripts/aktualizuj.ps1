# Aktualizator modow do Burglin' Gnomes
# - znajduje folder gry (Steam),
# - instaluje BepInEx 5, jesli go nie ma,
# - pobiera najnowszy kod modow z GitHuba i kompiluje je na tym komputerze,
# - wylaczone mody (.dll.off) zostaja wylaczone.
# Plik celowo bez polskich znakow (PowerShell 5.1).

param([string]$StartDir = "", [switch]$WaitForGame, [switch]$Relaunch)

$ErrorActionPreference = "Stop"
$Repo = "Viamio/mody-do-gnomow-"
$Branch = "main"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }

function Test-GameDir($dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir "Gnomium.exe"))
}

function Find-GameDir {
    if (Test-GameDir $StartDir) { return (Resolve-Path -LiteralPath $StartDir).Path }
    if ($StartDir) {
        $parent = Split-Path -Parent $StartDir.TrimEnd('\')
        if (Test-GameDir $parent) { return (Resolve-Path -LiteralPath $parent).Path }
    }
    $libs = New-Object System.Collections.Generic.List[string]
    try {
        $steam = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -Name SteamPath -ErrorAction Stop).SteamPath
        if ($steam) {
            $libs.Add(($steam -replace '/', '\'))
            $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
                }
            }
        }
    } catch { }
    $libs.Add("C:\Program Files (x86)\Steam")
    foreach ($lib in $libs) {
        $dir = Join-Path $lib "steamapps\common\Burglin' Gnomes"
        if (Test-GameDir $dir) { return $dir }
    }
    return $null
}

Say ""
Say "=== Aktualizator modow Burglin' Gnomes ===" "Cyan"

$game = Find-GameDir
if (-not $game) {
    Say "[BLAD] Nie znaleziono gry. Wrzuc AKTUALIZUJ.bat do folderu gry (tam gdzie Gnomium.exe) i uruchom ponownie." "Red"
    exit 1
}
Say "Gra: $game"

if ($WaitForGame) {
    Say "Czekam, az gra sie zamknie..."
    $waited = 0
    while ((Get-Process -Name "Gnomium" -ErrorAction SilentlyContinue) -and $waited -lt 60) { Start-Sleep -Seconds 1; $waited++ }
    Start-Sleep -Seconds 2
}
if (Get-Process -Name "Gnomium" -ErrorAction SilentlyContinue) {
    Say "[BLAD] Gra jest uruchomiona. Zamknij ja i uruchom aktualizator ponownie." "Red"
    exit 1
}

$managed = Join-Path $game "Gnomium_Data\Managed"
$core    = Join-Path $game "BepInEx\core"
$plugins = Join-Path $game "BepInEx\plugins"
$tmp     = Join-Path $env:TEMP ("gnome_mods_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    # --- BepInEx ---
    if (-not (Test-Path -LiteralPath (Join-Path $core "BepInEx.dll"))) {
        Say "Brak BepInEx - instaluje..." "Yellow"
        $rel = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/BepInEx/BepInEx/releases/latest" -Headers @{ "User-Agent" = "gnome-mods-updater" }
        $asset = $rel.assets | Where-Object { $_.name -match '^BepInEx_win_x64_5\..*\.zip$' } | Select-Object -First 1
        if (-not $asset) { throw "Nie znaleziono paczki BepInEx 5 x64 na GitHubie." }
        $bz = Join-Path $tmp "bepinex.zip"
        Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $bz
        Expand-Archive -LiteralPath $bz -DestinationPath $game -Force
        Say "BepInEx zainstalowany ($($asset.name))." "Green"
    }
    if (-not (Test-Path -LiteralPath $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

    # --- kompilator ---
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
    if (-not (Test-Path -LiteralPath $csc)) { throw "Brak kompilatora csc.exe (.NET Framework 4)." }

    # --- kod modow ---
    Say "Pobieram najnowsze mody z GitHuba..."
    $zip = Join-Path $tmp "mods.zip"
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$Repo/archive/refs/heads/$Branch.zip" -OutFile $zip
    Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
    $src = Get-ChildItem -LiteralPath $tmp -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "src") } | Select-Object -First 1
    if (-not $src) { throw "W pobranej paczce nie ma folderu src." }
    $srcDir = Join-Path $src.FullName "src"

    $version = ""
    $verFile = Join-Path $src.FullName "VERSION"
    if (Test-Path -LiteralPath $verFile) { $version = (Get-Content -LiteralPath $verFile -Raw).Trim() }

    $refs = @(
        "$managed\mscorlib.dll", "$managed\netstandard.dll", "$managed\System.dll", "$managed\System.Core.dll",
        "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.IMGUIModule.dll",
        "$managed\UnityEngine.InputLegacyModule.dll", "$managed\UnityEngine.AudioModule.dll",
        "$managed\UnityEngine.UnityWebRequestModule.dll", "$managed\UnityEngine.UnityWebRequestAudioModule.dll",
        "$managed\UnityEngine.PhysicsModule.dll",
        "$core\BepInEx.dll", "$core\0Harmony.dll"
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $ok = 0; $fail = 0
    foreach ($cs in Get-ChildItem -LiteralPath $srcDir -Filter "*.cs") {
        $name = [IO.Path]::GetFileNameWithoutExtension($cs.Name)
        $outDll = Join-Path $plugins "$name.dll"
        $offDll = "$outDll.off"
        $target = $outDll
        if ((Test-Path -LiteralPath $offDll) -and -not (Test-Path -LiteralPath $outDll)) { $target = $offDll }

        $built = Join-Path $tmp "$name.dll"
        $cscArgs = @("/nologo", "/target:library", "/optimize+", "/nostdlib+", "/noconfig", "/codepage:65001", "/out:$built")
        foreach ($r in $refs) { $cscArgs += "/r:$r" }
        $cscArgs += $cs.FullName

        Say "Kompiluje $name..."
        $output = & $csc @cscArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            $fail++
            Say "[BLAD] $name sie nie skompilowal:" "Red"
            $output | ForEach-Object { Say "   $_" "Red" }
            continue
        }
        Copy-Item -LiteralPath $built -Destination $target -Force
        $state = ""; if ($target -eq $offDll) { $state = " (wylaczony w menu - zostaje wylaczony)" }
        Say "  OK: $name$state" "Green"
        $ok++
    }

    if ($version) { Set-Content -LiteralPath (Join-Path $plugins "gnome-mods.version") -Value $version -Encoding ASCII }

    Say ""
    if ($fail -eq 0) {
        Say "[OK] Zaktualizowano $ok modow$(if ($version) { " (wersja $version)" })." "Green"
        if ($Relaunch) {
            Say "Uruchamiam gre ponownie..."
            Start-Sleep -Seconds 2
            Start-Process "steam://rungameid/3844970"
        } else {
            Say "Uruchom gre przez Steam. F1 = menu modow, F2 = AK (host), F6-F9 = muzyka i radio w bazie."
        }
    } else {
        Say "[UWAGA] $ok modow OK, $fail z bledem. Wyslij zrzut ekranu autorowi modow." "Yellow"
    }
}
catch {
    Say "[BLAD] $($_.Exception.Message)" "Red"
    exit 1
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
