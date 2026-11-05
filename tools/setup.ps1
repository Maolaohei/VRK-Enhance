<#
.SYNOPSIS
    VRK-Enhance installer / restorer.

.DESCRIPTION
    Installs selected components (BepInEx plugins + native upscaler + launcher + runtime
    "fix" toggles) into a VR Kanojo installation, or restores it back.

    Every action is recorded in state/manifest.json and every file we overwrite is
    copied into state/backup/ first, so -Mode restore can undo exactly what we did.

    Paths here use forward slashes on purpose: PowerShell and .NET accept them, and it
    keeps this file free of escape-sequence surprises.

.PARAMETER Mode
    install (default) or restore.

.PARAMETER Components
    Comma separated ids, or "all". Omit for the interactive menu.
    ids: flatfps upscale perf prewarm texplus unlock launcher openvr devtools

.PARAMETER GameDir
    Game root. Defaults to the parent folder of this repository.

.PARAMETER DryRun
    Print what would happen, touch nothing.

.PARAMETER Yes
    Do not ask for confirmation.
#>
[CmdletBinding()]
param(
    [ValidateSet('install','restore')]$Mode = 'install',
    [string]$Components,
    [string]$GameDir = '',
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$List,
    [string]$ListFile = '',
    [switch]$EchoGameDir,
    [string]$Lang = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "i18n.ps1")
$script:VRKLang = Resolve-VrkLang $Lang
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# --------------------------------------------------------------------------- paths
$RepoDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# ---- 定位游戏根目录：显式 -GameDir > 仓库所在目录 > 向上 4 层 > Steam 库 > 各盘 common 位置 ----
function Norm-Path([string]$path) {
    # VDF 里路径是双反斜杠转义的，统一折叠成一个（用字符码拼，避免写死转义序列）
    $bs = [string][char]92
    return $path.Replace($bs + $bs, $bs)
}

function Test-GameRoot([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir 'VR_Kanojo.exe'))
}

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $item = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            foreach ($prop in @('SteamPath', 'InstallPath')) {
                $val = $item.$prop
                if (-not [string]::IsNullOrWhiteSpace($val)) { $roots += $val }
            }
        } catch { }
    }

    $libs = @()
    foreach ($r in $roots) {
        $base = $r.Replace('/', '\')
        if ($libs -notcontains $base) { $libs += $base }
        $vdf = Join-Path $base 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            try {
                $text = [System.IO.File]::ReadAllText($vdf)
                foreach ($line in ($text -split "`n")) {
                    if ($line -notmatch '"path"') { continue }
                    $m = [regex]::Match($line, '"([A-Za-z]:[^"]*)"')
                    if ($m.Success) {
                        $lib = $m.Groups[1].Value.Replace('\', '\')
                        if ($libs -notcontains $lib) { $libs += $lib }
                    }
                }
            } catch { }
        }
    }
    return $libs
}

function Resolve-GameRoot([string]$explicitDir) {
    # 1) 显式指定
    if (-not [string]::IsNullOrWhiteSpace($explicitDir)) {
        if (Test-GameRoot $explicitDir) {
            return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $explicitDir).Path; How = 'explicit -GameDir' }
        }
        return $null
    }

    # 2) 仓库自己就在游戏根目录下
    if (Test-GameRoot $RepoDir) {
        return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $RepoDir).Path; How = 'repo folder is the game root' }
    }

    # 3) 往上看 4 层
    $probe = Split-Path -Parent $RepoDir
    for ($i = 0; $i -lt 4 -and $probe; $i++) {
        if (Test-GameRoot $probe) {
            return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $probe).Path; How = 'parent folder of the repo' }
        }
        $probe = Split-Path -Parent $probe
    }

    # 4) Steam 库（注册表 SteamPath + libraryfolders.vdf）
    foreach ($rawLib in (Get-SteamLibraries)) {
        $lib = Norm-Path $rawLib
        $cand = Join-Path $lib 'steamapps\common\VR_Kanojo'
        if (Test-GameRoot $cand) {
            return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $cand).Path; How = ('Steam library: ' + $lib) }
        }
    }

    # 5) 各盘符常见位置
    try {
        foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
            if (-not $drive.Root) { continue }
            foreach ($rel in @('SteamLibrary\steamapps\common\VR_Kanojo', 'Steam\steamapps\common\VR_Kanojo', 'Games\VR_Kanojo', 'VR_Kanojo')) {
                $cand = Join-Path $drive.Root $rel
                if (Test-GameRoot $cand) {
                    return [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $cand).Path; How = ('drive scan: ' + $drive.Root) }
                }
            }
        }
    } catch { }

    return $null
}

$resolvedRoot = Resolve-GameRoot $GameDir

if ($EchoGameDir) {
    if ($resolvedRoot) { Write-Output $resolvedRoot.Path; exit 0 }
    exit 1
}

if ($resolvedRoot) {
    $GameDir = $resolvedRoot.Path
    if ($resolvedRoot.How -ne 'explicit -GameDir') {
        Write-Host ((M '  game folder (auto-detected)' '  游戏目录（自动定位）') + ' [' + $resolvedRoot.How + ']: ' + $GameDir) -ForegroundColor DarkGray
    }
} else {
    $GameDir = $null
}

$Payload   = Join-Path $RepoDir 'payload'
$StateDir  = Join-Path $RepoDir 'state'
$BackupDir = Join-Path $StateDir 'backup'
$ManifestPath  = Join-Path $StateDir 'manifest.json'

function Write-Head($text) {
    Write-Host ''
    Write-Host ('=' * 68) -ForegroundColor DarkGray
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 68) -ForegroundColor DarkGray
}
function Write-Step($text) { Write-Host "  .   $text" -ForegroundColor Gray }
function Write-Ok($text)   { Write-Host "  OK  $text" -ForegroundColor Green }
function Write-Skip($text) { Write-Host "  --  $text" -ForegroundColor DarkYellow }
function Write-Warn2($text){ Write-Host "  !!  $text" -ForegroundColor Yellow }
function Write-Err2($text) { Write-Host "  XX  $text" -ForegroundColor Red }

# --------------------------------------------------------------------------- component table
# op kinds:
#   copy   : repo relative source -> game relative destination
#   rename : game relative path -> same path + Suffix   (a runtime toggle, reversible)
$Catalog = [ordered]@{
    bepinex = @{
        Title = (M 'BepInEx 5.4 runtime (install if missing)' 'BepInEx 5.4 运行时（缺失时安装）')
        Note  = (M 'Needed by every plugin. Skipped when BepInEx/core/BepInEx.Preloader.dll already exists' '所有插件的前置。已有 BepInEx/core/BepInEx.Preloader.dll 时自动跳过')
        Ops   = @(
            @{ Kind = 'unzip'; Src = 'payload/bepinex/BepInEx_win_x64_5.4.23.6.zip' }
        )
    }
    flatfps = @{
        Title = (M 'No-VR core (white screen / camera / keys / clicking / crosshair)' '免 VR 核心（白屏修复 / 相机 / 键位 / 点击 / 准星）')
        Note  = (M 'Required without a headset: white screen fix, eye height, WASD, Alt frees the cursor, E interacts' '没有头显时必装：修白屏、补眼高、WASD、Alt 释放指针、E 交互')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_FlatFPS.dll'; Dst = 'BepInEx/plugins/VRK_FlatFPS.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.flatfps.cfg';  Dst = 'BepInEx/config/vrk.flatfps.cfg'; OnlyIfMissing = $true }
        )
    }
    upscale = @{
        Title = (M 'FSR1 upscaling (EASU + RCAS)' 'FSR1 超分（EASU + RCAS）')
        Note  = (M 'Needs VR_Kanojo_Data/Plugins/vrkup.dll; default RenderScale 0.85' '需要 VR_Kanojo_Data/Plugins/vrkup.dll；默认 RenderScale 0.85')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_UpScale.dll'; Dst = 'BepInEx/plugins/VRK_UpScale.dll' }
            @{ Kind = 'copy'; Src = 'payload/native/vrkup.dll';        Dst = 'VR_Kanojo_Data/Plugins/vrkup.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.upscale.cfg';  Dst = 'BepInEx/config/vrk.upscale.cfg'; OnlyIfMissing = $true }
        )
    }
    perf = @{
        Title = (M 'Frame pacing (vsync off / no frame cap / loading GC)' '帧率优化（关 VSync / 解除限帧 / 加载期 GC）')
        Note  = (M 'No quality loss; on 240Hz panels pair it with a driver-side frame cap' '不降画质；240Hz 屏建议配合驱动侧限帧')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_Perf.dll'; Dst = 'BepInEx/plugins/VRK_Perf.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.perf.cfg';  Dst = 'BepInEx/config/vrk.perf.cfg'; OnlyIfMissing = $true }
        )
    }
    prewarm = @{
        Title = (M 'Loading prewarm (shaders / materials / textures, frame budgeted)' '加载期预热（着色器 / 材质 / 贴图，分帧带预算）')
        Note  = (M 'Removes the hitch burst right after a scene loads' '降低进场景后的突发掉帧')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_PreWarm.dll'; Dst = 'BepInEx/plugins/VRK_PreWarm.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.prewarm.cfg';  Dst = 'BepInEx/config/vrk.prewarm.cfg'; OnlyIfMissing = $true }
        )
    }
    texplus = @{
        Title = (M 'Texture boost (full resolution / 16x anisotropic / trilinear)' '贴图增强（全分辨率 / 各向异性 16x / 三线性）')
        Note  = (M 'Trades VRAM for sharpness; 12GB is plenty' '显存换画质，12GB 显存足够')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_TexPlus.dll'; Dst = 'BepInEx/plugins/VRK_TexPlus.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.texplus.cfg';  Dst = 'BepInEx/config/vrk.texplus.cfg'; OnlyIfMissing = $true }
        )
    }
    unlock = @{
        Title = (M 'Unlock everything (story / interactive / pose table)' '解锁全部内容（剧情 / 互动 / 体位表）')
        Note  = (M 'Calls the game own ALLClearData(); saved when the title screen loads' '调用游戏自带 ALLClearData()，下次进标题界面自动写入存档')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'payload/plugins/VRK_Unlock.dll'; Dst = 'BepInEx/plugins/VRK_Unlock.dll' }
            @{ Kind = 'copy'; Src = 'payload/config/vrk.unlock.cfg';  Dst = 'BepInEx/config/vrk.unlock.cfg'; OnlyIfMissing = $true }
        )
    }
    launcher = @{
        Title = (M 'Launcher (SHA-1 crash fix + SteamVR preflight + two entries)' '启动器（修 SHA-1 崩溃 + SteamVR 预检 + 两个入口）')
        Note  = (M 'Installs into the game root; launch through it, a plain exe double-click can still crash' '装到游戏根目录；必须用启动器启动，直接双击 exe 仍会随机崩')
        Ops   = @(
            @{ Kind = 'copy'; Src = 'launcher/VR_Kanojo_fix.exe'; Dst = 'VR_Kanojo_fix.exe' }
            @{ Kind = 'copy'; Src = 'launcher/启动_免VR模式.bat'; Dst = '启动_免VR模式.bat' }
            @{ Kind = 'copy'; Src = 'launcher/启动_VR模式.bat';   Dst = '启动_VR模式.bat' }
            @{ Kind = 'shortcut'; Target = '启动_免VR模式.bat'; Name = 'VR Kanojo (免VR).lnk'; Desc = (M 'No-VR mode: SHA-1 crash fix + openvr off + all plugins' '免 VR 模式：修 SHA-1 崩溃 + 关 openvr + 全部插件') }
            @{ Kind = 'shortcut'; Target = '启动_VR模式.bat';   Name = 'VR Kanojo (VR).lnk';   Desc = (M 'VR mode: make sure SteamVR is up, then launch' 'VR 模式：先检测/拉起 SteamVR 再启动游戏') }
        )
    }
    openvr = @{
        Title = (M 'No-VR: disable openvr_api.dll (kills the ~100s startup hang)' '免 VR：禁用 openvr_api.dll（消灭启动时约 100 秒未响应）')
        Note  = (M 'Renamed to .disabled, restorable any time; the no-VR bat does this too' '改名为 .disabled，可随时还原；启动_免VR模式.bat 也会自动做这件事')
        Ops   = @(
            @{ Kind = 'rename'; Src = 'VR_Kanojo_Data/Plugins/openvr_api.dll'; Suffix = '.disabled' }
        )
    }
    devtools = @{
        Title = (M 'Disable dev tools (F1 config UI / runtime debugger / broken ScriptLoader / old no-VR plugin)' '关闭开发工具（F1 配置界面 / 运行时调试器 / 坏掉的 ScriptLoader / 旧免VR插件）')
        Note  = (M 'No files deleted, only a .disabled suffix; restorable any time' '不删文件，只加 .disabled 后缀；随时可还原')
        Ops   = @(
            @{ Kind = 'rename'; Src = 'BepInEx/plugins/ConfigurationManager.dll'; Suffix = '.disabled' }
            @{ Kind = 'rename'; Src = 'BepInEx/plugins/RuntimeUnityEditor/RuntimeUnityEditor.Bepin5.dll'; Suffix = '.disabled' }
            @{ Kind = 'rename'; Src = 'BepInEx/plugins/ScriptLoader.dll'; Suffix = '.disabled' }
            @{ Kind = 'rename'; Src = 'BepInEx/plugins/VRK_PlayWithoutVR.dll'; Suffix = '.disabled' }
        )
    }
}

function Test-Payload {
    $missing = @()
    foreach ($sub in @('plugins', 'native', 'config')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Payload $sub))) { $missing += "payload/$sub" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $RepoDir 'launcher/VR_Kanojo_fix.exe'))) { $missing += 'launcher/VR_Kanojo_fix.exe' }
    return $missing
}

function Get-Manifest {
    if (Test-Path -LiteralPath $ManifestPath) {
        try {
            $raw = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
            if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
            $obj = $raw | ConvertFrom-Json
            if ($null -eq $obj) { return @() }
            return @($obj)
        } catch { return @() }
    }
    return @()
}

function Save-Manifest($entries) {
    if ($DryRun) { return }
    if (-not (Test-Path -LiteralPath $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    $json = ConvertTo-Json -InputObject @($entries) -Depth 6
    Set-Content -LiteralPath $ManifestPath -Value $json -Encoding UTF8
}

function Backup-File($fullPath, $relPath) {
    if ($DryRun) { return '' }
    if (-not (Test-Path -LiteralPath $fullPath)) { return '' }
    $dest = Join-Path $BackupDir $relPath
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item -LiteralPath $fullPath -Destination $dest -Force
    return $dest
}

# --------------------------------------------------------------------------- install
function Invoke-Install([string[]]$ids) {
    Write-Head (M "installing into: $GameDir" "安装到: $GameDir")
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($e in (Get-Manifest)) { $entries.Add($e) }

    if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx/core/BepInEx.Preloader.dll'))) {
        $pluginIds = @($ids | Where-Object { $_ -ne 'bepinex' -and $_ -ne 'launcher' -and $_ -ne 'openvr' -and $_ -ne 'devtools' })
        if ($pluginIds.Count -gt 0 -and ($ids -notcontains 'bepinex')) {
            Write-Warn2 (M 'BepInEx is not installed in the game folder - adding the bepinex component automatically.' '游戏目录里没有 BepInEx，已自动加入 bepinex 组件。')
            $ids = @('bepinex') + $ids
        }
    }

    foreach ($id in $ids) {
        $comp = $Catalog[$id]
        if (-not $comp) { Write-Err2 (M "unknown component: $id" "未知组件: $id"); continue }

        Write-Host ''
        Write-Host "  [$id] $($comp.Title)" -ForegroundColor White
        Write-Host "        $($comp.Note)" -ForegroundColor DarkGray

        foreach ($op in $comp.Ops) {
            if ($op.Kind -eq 'copy') {
                $src = Join-Path $RepoDir $op.Src
                $dst = Join-Path $GameDir $op.Dst
                if (-not (Test-Path -LiteralPath $src)) { Write-Err2 (M "missing file: $($op.Src)" "缺少文件: $($op.Src)"); continue }

                if ($op.OnlyIfMissing -and (Test-Path -LiteralPath $dst)) {
                    Write-Skip (M "$($op.Dst) already exists (your config is kept)" "$($op.Dst) 已存在（保留你的配置）")
                    continue
                }

                $known = $entries | Where-Object { $_.Id -eq $id -and ((-not $_.Gd) -or ($_.Gd -eq $GameDir)) -and $_.Dst -eq $op.Dst -and $_.Kind -eq 'copy' }
                if (-not $known) {
                    $backup = Backup-File $dst $op.Dst
                    $entries.Add([pscustomobject]@{
                        Id     = $id
                        Gd     = $GameDir
                        Kind   = 'copy'
                        Dst    = $op.Dst
                        Backup = $backup
                        Time   = (Get-Date).ToString('s')
                    })
                }

                if ($DryRun) { Write-Step (M "would copy $($op.Src) -> $($op.Dst)" "会复制 $($op.Src) -> $($op.Dst)"); continue }
                $dstDir = Split-Path -Parent $dst
                if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
                Copy-Item -LiteralPath $src -Destination $dst -Force
                Write-Ok (M "copied $($op.Dst)" "复制 $($op.Dst)")
            }
            elseif ($op.Kind -eq 'unzip') {
                $zip = Join-Path $RepoDir $op.Src
                if (-not (Test-Path -LiteralPath $zip)) { Write-Err2 (M "missing archive: $($op.Src)" "缺少压缩包: $($op.Src)"); continue }
                $probe = Join-Path $GameDir 'BepInEx/core/BepInEx.Preloader.dll'
                if (Test-Path -LiteralPath $probe) { Write-Skip (M 'BepInEx is already present in the game folder' '游戏目录里已经有 BepInEx，跳过'); continue }

                $known = $entries | Where-Object { $_.Id -eq $id -and ((-not $_.Gd) -or ($_.Gd -eq $GameDir)) -and $_.Kind -eq 'unzip' }
                if ($DryRun) { Write-Step (M "would extract $($op.Src) into the game folder" "会解包 $($op.Src) 到游戏目录"); continue }

                $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('vrk-bepinex-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
                if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
                New-Item -ItemType Directory -Path $tmp -Force | Out-Null
                try {
                    if (Get-Command Expand-Archive -ErrorAction SilentlyContinue) {
                        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
                    } else {
                        Add-Type -AssemblyName System.IO.Compression.FileSystem
                        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
                    }
                    $sep = [char[]]@(92, 47)
                    $files = [System.Collections.Generic.List[string]]::new()
                    Get-ChildItem -LiteralPath $tmp -Recurse -File | ForEach-Object {
                        $rel = $_.FullName.Substring($tmp.Length).TrimStart($sep)
                        $target = Join-Path $GameDir $rel
                        $tdir = Split-Path -Parent $target
                        if (-not (Test-Path -LiteralPath $tdir)) { New-Item -ItemType Directory -Path $tdir -Force | Out-Null }
                        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
                        $files.Add($rel)
                    }
                    if (-not $known) {
                        $entries.Add([pscustomobject]@{
                            Id     = $id
                            Gd     = $GameDir
                            Kind   = 'unzip'
                            Dst    = '.'
                            Files  = @($files)
                            Backup = ''
                            Time   = (Get-Date).ToString('s')
                        })
                    }
                    Write-Ok (M "extracted BepInEx ($($files.Count) files)" "已解包 BepInEx（$($files.Count) 个文件）")
                } finally {
                    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            elseif ($op.Kind -eq 'shortcut') {
                if ($NoShortcut) { Write-Skip (M "skip shortcut $($op.Name) (-NoShortcut)" "跳过快捷方式 $($op.Name)（-NoShortcut）"); continue }

                $targetFull = Join-Path $GameDir $op.Target
                if (-not (Test-Path -LiteralPath $targetFull)) { Write-Err2 (M "missing launch script: $($op.Target)" "缺少启动脚本: $($op.Target)"); continue }

                $lnkDir = if ([string]::IsNullOrWhiteSpace($ShortcutDir)) { [Environment]::GetFolderPath('Desktop') } else { $ShortcutDir }
                if ([string]::IsNullOrWhiteSpace($lnkDir)) { Write-Err2 (M 'desktop folder not found, pass -ShortcutDir' '找不到桌面目录，用 -ShortcutDir 指定'); continue }
                $lnk = Join-Path $lnkDir $op.Name

                $known = $entries | Where-Object { $_.Id -eq $id -and ((-not $_.Gd) -or ($_.Gd -eq $GameDir)) -and $_.Dst -eq $lnk -and $_.Kind -eq 'shortcut' }
                if (-not $known) {
                    $entries.Add([pscustomobject]@{
                        Id     = $id
                        Gd     = $GameDir
                        Kind   = 'shortcut'
                        Dst    = $lnk
                        Backup = ''
                        Time   = (Get-Date).ToString('s')
                    })
                }

                if ($DryRun) { Write-Step (M "would create shortcut $lnk -> $($op.Target)" "会创建快捷方式 $lnk -> $($op.Target)"); continue }

                if (-not (Test-Path -LiteralPath $lnkDir)) { New-Item -ItemType Directory -Path $lnkDir -Force | Out-Null }
                $ws = New-Object -ComObject WScript.Shell
                $sc = $ws.CreateShortcut($lnk)
                $sc.TargetPath = $targetFull
                $sc.WorkingDirectory = $GameDir
                $sc.WindowStyle = 1
                $sc.Description = $op.Desc
                $icon = Join-Path $GameDir 'VR_Kanojo.exe'
                if (Test-Path -LiteralPath $icon) { $sc.IconLocation = $icon + ',0' }
                $sc.Save()
                Write-Ok (M "created shortcut $lnk" "创建快捷方式 $lnk")
            }
            elseif ($op.Kind -eq 'rename') {
                $src = Join-Path $GameDir $op.Src
                $dstRel = $op.Src + $op.Suffix
                $dst = Join-Path $GameDir $dstRel

                if (-not (Test-Path -LiteralPath $src)) {
                    if (Test-Path -LiteralPath $dst) { Write-Skip (M "$($op.Src) is already disabled" "$($op.Src) 已是 disabled") }
                    else { Write-Skip (M "$($op.Src) is not there (skipped)" "$($op.Src) 不存在（跳过）") }
                    continue
                }

                $known = $entries | Where-Object { $_.Id -eq $id -and ((-not $_.Gd) -or ($_.Gd -eq $GameDir)) -and $_.Dst -eq $dstRel -and $_.Kind -eq 'rename' }
                if (-not $known) {
                    $entries.Add([pscustomobject]@{
                        Id     = $id
                        Gd     = $GameDir
                        Kind   = 'rename'
                        From   = $op.Src
                        Dst    = $dstRel
                        Backup = ''
                        Time   = (Get-Date).ToString('s')
                    })
                }

                if ($DryRun) { Write-Step (M "would rename $($op.Src) -> $($dstRel)" "会改名 $($op.Src) -> $($dstRel)"); continue }
                Move-Item -LiteralPath $src -Destination $dst -Force
                Write-Ok (M "renamed $($op.Src) -> $($dstRel)" "改名 $($op.Src) -> $($dstRel)")
            }
        }
    }

    Save-Manifest $entries
    Write-Host ''
    if ($DryRun) { Write-Warn2 (M 'DryRun: no files were changed' 'DryRun：没有改动任何文件') }
    else {
        Write-Ok (M 'Installed. Launch the game with the no-VR or VR bat.' '安装完成。用 启动_免VR模式.bat 或 启动_VR模式.bat 启动游戏。')
        Write-Host (M '     Run the restore script any time to undo.' '     随时可以用 一键还原.cmd 撤销。') -ForegroundColor DarkGray
    }
}

# --------------------------------------------------------------------------- restore
function Invoke-Restore([string[]]$ids) {
    Write-Head (M "restoring: $GameDir" "还原: $GameDir")
    $entries = Get-Manifest
    if ($entries.Count -eq 0) {
        Write-Warn2 (M 'No install record (state/manifest.json missing or empty), nothing to restore.' '没有安装记录（state/manifest.json 不存在或为空），无需还原。')
        return
    }

    $keep = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        if ($ids.Count -gt 0 -and ($ids -notcontains $entry.Id)) { $keep.Add($entry); continue }
        if ($entry.Gd -and ($entry.Gd -ne $GameDir)) { $keep.Add($entry); continue }

        if ($entry.Kind -eq 'shortcut') {
            if ($DryRun) { Write-Step (M "would delete shortcut $($entry.Dst)" "会删除快捷方式 $($entry.Dst)"); continue }
            if (Test-Path -LiteralPath $entry.Dst) {
                Remove-Item -LiteralPath $entry.Dst -Force
                Write-Ok (M "deleted shortcut $($entry.Dst)" "删除快捷方式 $($entry.Dst)")
            } else {
                Write-Skip (M "$($entry.Dst) is gone, skipped" "$($entry.Dst) 已不在，跳过")
            }
            continue
        }

        $dstFull = Join-Path $GameDir $entry.Dst

        if ($entry.Kind -eq 'copy') {
            if ($DryRun) {
                Write-Step (M "would remove $($entry.Dst)" "会移除 $($entry.Dst)")
                if ($entry.Backup) { Write-Step (M "would roll back backup -> $($entry.Dst)" "会回滚备份 -> $($entry.Dst)") }
                continue
            }
            if (Test-Path -LiteralPath $dstFull) { Remove-Item -LiteralPath $dstFull -Force }
            if ($entry.Backup -and (Test-Path -LiteralPath $entry.Backup)) {
                $dstDir = Split-Path -Parent $dstFull
                if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
                Copy-Item -LiteralPath $entry.Backup -Destination $dstFull -Force
                Write-Ok (M "removed and rolled back $($entry.Dst)" "移除并回滚 $($entry.Dst)")
            } else {
                Write-Ok (M "removed $($entry.Dst)" "移除 $($entry.Dst)")
            }
        }
        elseif ($entry.Kind -eq 'unzip') {
            $cnt = @($entry.Files).Count
            if ($DryRun) { Write-Step (M "would remove the $cnt file(s) extracted for BepInEx" "会删除 BepInEx 解出的 $cnt 个文件"); continue }
            $n = 0
            foreach ($rel in @($entry.Files)) {
                $f = Join-Path $GameDir $rel
                if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; $n++ }
            }
            Write-Ok (M "removed $n BepInEx file(s)" "已删除 $n 个 BepInEx 文件")
        }
        elseif ($entry.Kind -eq 'rename') {
            if ($DryRun) { Write-Step (M "would rename back $($entry.From)" "会改名回 $($entry.From)"); continue }
            $fromFull = Join-Path $GameDir $entry.From
            if (Test-Path -LiteralPath $dstFull) {
                if (Test-Path -LiteralPath $fromFull) { Remove-Item -LiteralPath $fromFull -Force }
                Move-Item -LiteralPath $dstFull -Destination $fromFull -Force
                Write-Ok (M "renamed back $($entry.From)" "改名回 $($entry.From)")
            } else {
                Write-Skip (M "$($entry.From) is gone, skipped" "$($entry.From) 已不在，跳过")
            }
        }
    }

    Save-Manifest $keep

    if (-not $DryRun) {
        foreach ($d in @('BepInEx/plugins', 'BepInEx/config', 'BepInEx/core', 'BepInEx')) {
            $df = Join-Path $GameDir $d
            if ((Test-Path -LiteralPath $df) -and -not (Get-ChildItem -LiteralPath $df -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                Remove-Item -LiteralPath $df -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not $DryRun -and (Test-Path -LiteralPath $BackupDir)) {
        Get-ChildItem -LiteralPath $BackupDir -Directory -Recurse -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName -Descending |
            ForEach-Object {
                if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                }
            }
    }

    Write-Host ''
    if ($DryRun) { Write-Warn2 (M 'DryRun: no files were changed' 'DryRun：没有改动任何文件') } else { Write-Ok (M 'Restore finished.' '还原完成。') }
}

# --------------------------------------------------------------------------- menu
function Show-Menu {
    $entries = Get-Manifest
    Write-Head (M 'VR Kanojo - one-click installer' 'VR Kanojo 一键安装')
    Write-Host (M "  game folder: $GameDir" "  游戏目录: $GameDir") -ForegroundColor DarkGray
    Write-Host ''
    $i = 0
    $map = @{}
    foreach ($id in $Catalog.Keys) {
        $i++
        $map[$i] = $id
        $installed = @($entries | Where-Object { $_.Id -eq $id }).Count -gt 0
        $mark = if ($installed) { (M '[on]' '[已装]') } else { '     ' }
        Write-Host ("  {0,2}) {1} {2,-9} {3}" -f $i, $mark, $id, $Catalog[$id].Title) -ForegroundColor White
        Write-Host ("        {0}" -f $Catalog[$id].Note) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host (M '  Enter numbers, comma separated (e.g. 1,2,7); a = all; Enter = cancel' '  输入编号，逗号分隔（例: 1,2,7）；a=全部；直接回车=取消') -ForegroundColor Yellow
    $sel = Read-Host (M '  choose' '  选择')
    if ([string]::IsNullOrWhiteSpace($sel)) { return @() }
    if ($sel.Trim() -eq 'a' -or $sel.Trim() -eq 'A') { return @($Catalog.Keys) }

    $ids = @()
    foreach ($part in ($sel -split '[,，\s]+')) {
        $n = 0
        if ([int]::TryParse($part, [ref]$n) -and $map.ContainsKey($n)) { $ids += $map[$n] }
    }
    return $ids
}

# --------------------------------------------------------------------------- main
if ($List) {
    $installedIds = @{}
    foreach ($e in (Get-Manifest)) { $installedIds[$e.Id] = $true }
    $rows = @()
    $lines = @()
    foreach ($id in $Catalog.Keys) {
        $on = [bool]$installedIds[$id]
        if ($id -eq 'bepinex') { $on = Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx/core/BepInEx.Preloader.dll') }
        $rows += [pscustomobject]@{ Id = $id; Title = $Catalog[$id].Title; Note = $Catalog[$id].Note; Installed = $on }
        $flag = '0'
        if ($on) { $flag = '1' }
        $lines += ($id + [char]9 + $Catalog[$id].Title + [char]9 + $Catalog[$id].Note + [char]9 + $flag)
    }
    if (-not [string]::IsNullOrWhiteSpace($ListFile)) {
        $enc = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllLines($ListFile, $lines, $enc)
    } else {
        ConvertTo-Json -InputObject $rows -Depth 4
    }
    exit 0
}


if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'VR_Kanojo.exe'))) {
    Write-Err2 (M 'Cannot find the VR Kanojo install folder (it must contain VR_Kanojo.exe)' '找不到 VR Kanojo 的安装目录（其中要有 VR_Kanojo.exe）')
    if (-not [string]::IsNullOrWhiteSpace($GameDir)) { Write-Host ((M '  tried: ' '  试过: ') + $GameDir) -ForegroundColor DarkGray }
    Write-Host (M '  Two options:' '  两种办法：') -ForegroundColor Yellow
    Write-Host (M '    1) Put the whole VRK-Enhance folder into the game root (next to VR_Kanojo.exe); it is found automatically' '    1) 把 VRK-Enhance 整个文件夹放进游戏根目录（与 VR_Kanojo.exe 同级），脚本会自动找到') -ForegroundColor DarkGray
    Write-Host (M '    2) manual: powershell -File tools/setup.ps1 -GameDir "D:\SteamLibrary\steamapps\common\VR_Kanojo"' '    2) 手动指定：powershell -File tools/setup.ps1 -GameDir "D:\SteamLibrary\steamapps\common\VR_Kanojo"') -ForegroundColor DarkGray
    exit 1
    exit 1
}

$missing = Test-Payload
if ($missing.Count -gt 0) {
    Write-Err2 (M "payload is incomplete, missing: $($missing -join ’, ’)" "payload 不完整，缺: $($missing -join ', ')")
    Write-Host (M '  This is part of the release package; please get the complete package again.' '  这是发布包必需的部分，请重新获取完整仓库。') -ForegroundColor DarkGray
    exit 1
}

$ids = @()
if (-not [string]::IsNullOrWhiteSpace($Components)) {
    if ($Components.Trim() -eq 'all') { $ids = @($Catalog.Keys) }
    else {
        foreach ($p in ($Components -split '[,; ]+')) {
            $p = $p.Trim()
            if ($p -and $Catalog.Contains($p)) { $ids += $p }
            elseif ($p) { Write-Warn2 (M "ignoring unknown component: $p" "忽略未知组件: $p") }
        }
    }
} elseif ($Mode -eq 'install') {
    $ids = Show-Menu
}

if ($Mode -eq 'install' -and $ids.Count -eq 0) { Write-Warn2 (M 'No components selected, exiting.' '没有选择任何组件，退出。'); exit 0 }

if (-not $Yes -and $ids.Count -gt 0) {
    Write-Host ''
    Write-Host (M "  about to run $Mode on: $($ids -join ’, ’)" "  将对: $($ids -join ', ') 执行 $Mode") -ForegroundColor Yellow
    $ok = Read-Host (M '  Confirm? (Y/n)' '  确认？(Y/n)')
    if ($ok -and $ok.Trim().ToLower() -eq 'n') { Write-Warn2 (M 'Cancelled.' '已取消。'); exit 0 }
}

if ($Mode -eq 'install') { Invoke-Install $ids } else { Invoke-Restore $ids }
