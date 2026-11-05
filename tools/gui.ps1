param(
    [string]$GameDir = '',
    [switch]$SelfTest
)

# VRK-Enhance 图形安装面板（WinForms，无外部依赖）
# 所有安装/还原逻辑复用 tools/setup.ps1，本文件只负责界面与调用。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$RepoDir  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$SetupPs1 = Join-Path $RepoDir 'tools/setup.ps1'
$StateDir = Join-Path $RepoDir 'state'
if (-not (Test-Path -LiteralPath $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
$LogFile = Join-Path $StateDir 'gui-last.log'

$script:GameDir = $GameDir

function Get-GameRoot {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $SetupPs1 -EchoGameDir 2>&1
    if (-not $out) { return '' }
    return ([string]($out | Select-Object -First 1)).Trim()
}

function Get-Catalog {
    $catalogFile = Join-Path $StateDir 'catalog.txt'
    if (Test-Path -LiteralPath $catalogFile) { Remove-Item -LiteralPath $catalogFile -Force }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $SetupPs1 -List -ListFile $catalogFile | Out-Null
    if (-not (Test-Path -LiteralPath $catalogFile)) { throw 'setup.ps1 没能写出组件表' }
    $lines = [System.IO.File]::ReadAllLines($catalogFile, [System.Text.Encoding]::UTF8)
    $out = @()
    foreach ($ln in $lines) {
        if ([string]::IsNullOrWhiteSpace($ln)) { continue }
        $f = $ln -split ([char]9)
        if ($f.Count -lt 4) { continue }
        $out += [pscustomobject]@{ Id = $f[0]; Title = $f[1]; Note = $f[2]; Installed = ($f[3] -eq '1') }
    }
    if ($out.Count -eq 0) { throw '组件表为空' }
    return $out
}

function Write-Log([System.Windows.Forms.RichTextBox]$box, [string]$text) {
    $line = '[' + (Get-Date).ToString('HH:mm:ss') + '] ' + $text
    $box.AppendText($line + [Environment]::NewLine)
    $box.SelectionStart = $box.TextLength
    $box.ScrollToCaret()
    try { Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch { }
    [System.Windows.Forms.Application]::DoEvents()
}

function Invoke-Setup([string[]]$componentIds, [string]$mode, [bool]$dryRun) {
    $q = [char]34
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ($q + $SetupPs1 + $q), '-Yes')
    if ($componentIds -and $componentIds.Count -gt 0) { $argList += @('-Components', ($componentIds -join ',')) }
    if ($mode) { $argList += @('-Mode', $mode) }
    if ($dryRun) { $argList += '-DryRun' }
    if (-not [string]::IsNullOrWhiteSpace($script:GameDir)) { $argList += @('-GameDir', ($q + $script:GameDir + $q)) }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = 'powershell.exe'
    $psi.Arguments = ($argList -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; StdOut = $stdout; StdErr = $stderr }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'VRK Enhance - 一键安装 / 还原'
$form.Size = New-Object System.Drawing.Size(880, 720)
$form.MinimumSize = New-Object System.Drawing.Size(760, 620)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Text = '勾选需要的组件，然后点【安装选中】；想撤销就点【还原选中】。首次使用建议先点【安装推荐】。'
$lblInfo.Location = New-Object System.Drawing.Point(14, 10)
$lblInfo.Size = New-Object System.Drawing.Size(840, 22)
$lblInfo.Anchor = 'Top,Left,Right'
$form.Controls.Add($lblInfo)

$grp = New-Object System.Windows.Forms.GroupBox
$grp.Text = '组件（[已装] 表示当前已生效）'
$grp.Location = New-Object System.Drawing.Point(14, 38)
$grp.Size = New-Object System.Drawing.Size(840, 320)
$grp.Anchor = 'Top,Left,Right'
$form.Controls.Add($grp)

$panelList = New-Object System.Windows.Forms.Panel
$panelList.Location = New-Object System.Drawing.Point(10, 22)
$panelList.Size = New-Object System.Drawing.Size(816, 286)
$panelList.AutoScroll = $true
$panelList.Anchor = 'Top,Left,Right,Bottom'
$grp.Controls.Add($panelList)

$script:CheckBoxes = @()
$script:Catalog = @()

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = '正在读取组件表…'
$lblStatus.Location = New-Object System.Drawing.Point(18, 434)
$lblStatus.Size = New-Object System.Drawing.Size(840, 20)
$lblStatus.Anchor = 'Top,Left,Right'
$lblStatus.ForeColor = [System.Drawing.Color]::DimGray
$form.Controls.Add($lblStatus)

function Build-CheckList {
    $panelList.Controls.Clear()
    $script:CheckBoxes = @()
    $script:Catalog = @(Get-Catalog)
    $y = 4
    foreach ($c in $script:Catalog) {
        $cb = New-Object System.Windows.Forms.CheckBox
        $suffix = if ($c.Installed) { '  [已装]' } else { '' }
        $cb.Text = $c.Title + $suffix
        $cb.Tag = $c.Id
        $cb.Location = New-Object System.Drawing.Point(8, $y)
        $cb.Size = New-Object System.Drawing.Size(780, 22)
        $cb.AutoEllipsis = $true
        $tip = New-Object System.Windows.Forms.ToolTip
        $tip.SetToolTip($cb, $c.Note)
        $panelList.Controls.Add($cb)
        $script:CheckBoxes += $cb
        $y += 24
    }
    $installedCount = @($script:Catalog | Where-Object { $_.Installed }).Count
    $root = Get-GameRoot
    if ($root) {
        if (-not [string]::IsNullOrWhiteSpace($script:GameDir) -and $script:GameDir -ne $root) {
            $lblStatus.Text = '共 ' + $script:Catalog.Count + ' 个组件，已安装 ' + $installedCount + ' 个；游戏目录（手动指定）：' + $script:GameDir
        } else {
            $lblStatus.Text = '共 ' + $script:Catalog.Count + ' 个组件，已安装 ' + $installedCount + ' 个；游戏目录（自动定位）：' + $root
        }
    } else {
        $lblStatus.Text = '共 ' + $script:Catalog.Count + ' 个组件，已安装 ' + $installedCount + ' 个；没找到游戏目录，请点【选择目录…】'
        $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick
    }
}

$btnRow = New-Object System.Windows.Forms.FlowLayoutPanel
$btnRow.Location = New-Object System.Drawing.Point(14, 364)
$btnRow.Size = New-Object System.Drawing.Size(840, 40)
$btnRow.Anchor = 'Top,Left,Right'
$form.Controls.Add($btnRow)

function New-Btn([string]$text, [int]$w) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b.Size = New-Object System.Drawing.Size($w, 32)
    $b.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 0)
    return $b
}

$btnAll     = New-Btn '全选' 80
$btnNone    = New-Btn '全不选' 80
$btnInstall = New-Btn '安装选中' 100
$btnRestore = New-Btn '还原选中' 100
$btnRec     = New-Btn '安装推荐' 100
$btnPick    = New-Btn '选择目录…' 110
$btnClose   = New-Btn '关闭' 80
foreach ($b in @($btnAll, $btnNone, $btnInstall, $btnRestore, $btnRec, $btnPick, $btnClose)) { $btnRow.Controls.Add($b) }

$btnPick.add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = '选择 VR Kanojo 的游戏根目录（里面有 VR_Kanojo.exe）'
    $dlg.ShowNewFolderButton = $false
    if ($script:GameDir) { $dlg.SelectedPath = $script:GameDir }
    elseif (Test-Path -LiteralPath $RepoDir) { $dlg.SelectedPath = $RepoDir }
    if ($dlg.ShowDialog() -ne 'OK') { return }
    $picked = $dlg.SelectedPath
    if (-not (Test-Path -LiteralPath (Join-Path $picked 'VR_Kanojo.exe'))) {
        [void][System.Windows.Forms.MessageBox]::Show('这个目录里没有 VR_Kanojo.exe，请选择游戏根目录。', 'VRK Enhance')
        return
    }
    $script:GameDir = $picked
    $lblStatus.ForeColor = [System.Drawing.Color]::DimGray
    Write-Log $tbLog ('游戏目录已指定：' + $picked)
    Build-CheckList
})

$chkDry = New-Object System.Windows.Forms.CheckBox
$chkDry.Text = '试运行（DryRun：只显示会改什么，不动文件）'
$chkDry.Location = New-Object System.Drawing.Point(18, 408)
$chkDry.Size = New-Object System.Drawing.Size(420, 22)
$chkDry.Anchor = 'Top,Left'
$form.Controls.Add($chkDry)

$tbLog = New-Object System.Windows.Forms.RichTextBox
$tbLog.Location = New-Object System.Drawing.Point(14, 458)
$tbLog.Size = New-Object System.Drawing.Size(840, 214)
$tbLog.Anchor = 'Top,Left,Right,Bottom'
$tbLog.ReadOnly = $true
$tbLog.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
$tbLog.ForeColor = [System.Drawing.Color]::Gainsboro
$tbLog.Font = New-Object System.Drawing.Font('Consolas', 9)
$tbLog.WordWrap = $false
$form.Controls.Add($tbLog)

function Get-CheckedIds {
    $ids = @()
    foreach ($cb in $script:CheckBoxes) { if ($cb.Checked) { $ids += [string]$cb.Tag } }
    return $ids
}

function Append-SetupOutput($result) {
    foreach ($ln in ($result.StdOut -split "`r?`n")) {
        if ($ln.Trim()) { $tbLog.AppendText('    ' + $ln + [Environment]::NewLine) }
    }
    if ($result.StdErr.Trim()) { $tbLog.AppendText('    [stderr] ' + $result.StdErr.Trim() + [Environment]::NewLine) }
    $tbLog.SelectionStart = $tbLog.TextLength
    $tbLog.ScrollToCaret()
}

$btnAll.add_Click({ foreach ($cb in $script:CheckBoxes) { $cb.Checked = $true } })
$btnNone.add_Click({ foreach ($cb in $script:CheckBoxes) { $cb.Checked = $false } })
$btnClose.add_Click({ $form.Close() })

$btnRec.add_Click({
    $recommended = @('flatfps', 'upscale', 'perf', 'prewarm', 'texplus', 'launcher')
    foreach ($cb in $script:CheckBoxes) { $cb.Checked = ($recommended -contains [string]$cb.Tag) }
    Write-Log $tbLog '已勾选推荐组合：核心 / 超分 / 优化 / 预热 / 贴图 / 启动器'
})

$btnInstall.add_Click({
    $ids = Get-CheckedIds
    if ($ids.Count -eq 0) { [void][System.Windows.Forms.MessageBox]::Show('先勾选要安装的组件。', 'VRK Enhance'); return }
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $btnInstall.Enabled = $false
    $btnRestore.Enabled = $false
    $mark = ''
    if ($chkDry.Checked) { $mark = '（试运行）' }
    Write-Log $tbLog ('安装：' + ($ids -join ', ') + $mark)
    try {
        $r = Invoke-Setup -componentIds $ids -mode '' -dryRun $chkDry.Checked
        Append-SetupOutput $r
        Write-Log $tbLog ('安装结束，退出码 ' + $r.ExitCode)
    } catch {
        Write-Log $tbLog ('安装出错：' + $_.Exception.Message)
    } finally {
        $btnInstall.Enabled = $true
        $btnRestore.Enabled = $true
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
        Build-CheckList
    }
})

$btnRestore.add_Click({
    $ids = Get-CheckedIds
    if ($ids.Count -eq 0) { [void][System.Windows.Forms.MessageBox]::Show('先勾选要还原的组件。', 'VRK Enhance'); return }
    $msg = '确定还原这些组件吗？' + [Environment]::NewLine + ($ids -join ', ')
    $ans = [System.Windows.Forms.MessageBox]::Show($msg, 'VRK Enhance', 'OKCancel', 'Question')
    if ($ans -ne 'OK') { return }
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $btnInstall.Enabled = $false
    $btnRestore.Enabled = $false
    Write-Log $tbLog ('还原：' + ($ids -join ', '))
    try {
        $r = Invoke-Setup -componentIds $ids -mode 'restore' -dryRun $chkDry.Checked
        Append-SetupOutput $r
        Write-Log $tbLog ('还原结束，退出码 ' + $r.ExitCode)
    } catch {
        Write-Log $tbLog ('还原出错：' + $_.Exception.Message)
    } finally {
        $btnInstall.Enabled = $true
        $btnRestore.Enabled = $true
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
        Build-CheckList
    }
})

$form.add_Shown({
    try {
        Build-CheckList
        Write-Log $tbLog ('面板就绪：组件表读取成功，游戏目录 ' + (Split-Path -Parent $RepoDir))
    } catch {
        Write-Log $tbLog ('读取组件表失败：' + $_.Exception.Message)
    }

    if ($SelfTest) {
        Write-Log $tbLog '自检：对 flatfps 执行 DryRun 安装'
        $lines = @()
        try {
            $r = Invoke-Setup -componentIds @('flatfps') -mode '' -dryRun $true
            $first = ($r.StdOut -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
            $lines += 'SELFTEST catalog=' + $script:Catalog.Count
            $lines += 'SELFTEST checkboxes=' + $script:CheckBoxes.Count
            $lines += 'SELFTEST dryrun=' + $chkDry.Text.Length
            $lines += 'SELFTEST logbox=' + $tbLog.GetType().Name
            $lines += 'SELFTEST exitcode=' + $r.ExitCode
            $lines += 'SELFTEST firstline=' + $first
        } catch {
            $lines += 'SELFTEST error=' + $_.Exception.Message
        }
        $outFile = Join-Path $StateDir 'gui-selftest.txt'
        $lines | Set-Content -LiteralPath $outFile -Encoding UTF8
        $lines | ForEach-Object { Write-Output $_ }
        $form.Dispose()
        return
    }
})

[void]$form.ShowDialog()
