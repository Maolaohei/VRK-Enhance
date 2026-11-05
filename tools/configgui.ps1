# VRK Enhance - visual editor for BepInEx plugin configs.
# Reads BepInEx/config/vrk.*.cfg, edits every key, saves with .bak backups.
param([string]$GameDir = '', [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$RepoDir   = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GameDir)) { $GameDir = Split-Path -Parent $RepoDir }
$CfgDir    = Join-Path $GameDir 'BepInEx/config'
$DefDir    = Join-Path $RepoDir 'payload/config'
$BackupDir = Join-Path $CfgDir '_backup'
$CRLF      = [string]([char]13) + [string]([char]10)
$LFONLY    = [string]([char]10)

function Get-CfgFiles {
    if (-not (Test-Path -LiteralPath $CfgDir)) { return @() }
    Get-ChildItem -LiteralPath $CfgDir -Filter 'vrk.*.cfg' -File | Sort-Object Name
}

function Read-CfgFile([string]$path) {
    $raw = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $nl = if ($raw.Contains($CRLF)) { $CRLF } else { $LFONLY }
    $lines = $raw -split "[\r\n]+"
    $entries = [System.Collections.Generic.List[object]]::new()
    $section = 'top'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].Trim()
        if ($t -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        $entries.Add([pscustomobject]@{
            Index   = $i
            Section = $section
            Key     = $t.Substring(0, $eq).Trim()
            Value   = $t.Substring($eq + 1).Trim()
        })
    }
    [pscustomobject]@{ Path = $path; Lines = $lines; Entries = $entries; NewLine = $nl }
}

function Get-DefaultValue([string]$fileName, [string]$section, [string]$key) {
    $p = Join-Path $DefDir $fileName
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    $d = Read-CfgFile $p
    foreach ($e in $d.Entries) {
        if ($e.Section -eq $section -and $e.Key -eq $key) { return $e.Value }
    }
    return $null
}

$script:Files   = [ordered]@{}
$script:Dirty   = $false
$script:Current = $null

$form = New-Object Windows.Forms.Form
$form.Text = 'VRK Enhance - plugin settings'
$form.Size = New-Object Drawing.Size(1020, 660)
$form.StartPosition = 'CenterScreen'
$form.MinimumSize = New-Object Drawing.Size(840, 540)
$form.Font = New-Object Drawing.Font('Segoe UI', 9)

$hint = New-Object Windows.Forms.Label
$hint.Dock = 'Top'
$hint.Height = 44
$hint.Padding = New-Object Windows.Forms.Padding(10, 6, 10, 0)
$hint.Text = 'Pick a key on the left, edit the value on the right, click Save. A .bak copy is made first. Restart the game to apply.'
$form.Controls.Add($hint)

$status = New-Object Windows.Forms.Label
$status.Dock = 'Bottom'
$status.Height = 26
$status.Padding = New-Object Windows.Forms.Padding(10, 3, 10, 0)
$status.Text = ''
$form.Controls.Add($status)

$split = New-Object Windows.Forms.SplitContainer
$split.Dock = 'Fill'
$split.SplitterDistance = 340
$form.Controls.Add($split)
$split.Panel1.Padding = New-Object Windows.Forms.Padding(8)
$split.Panel2.Padding = New-Object Windows.Forms.Padding(8)
$split.BringToFront()

$tree = New-Object Windows.Forms.TreeView
$tree.Dock = 'Fill'
$tree.HideSelection = $false
$tree.Font = New-Object Drawing.Font('Consolas', 9.5)
$split.Panel1.Controls.Add($tree)

$lblFile = New-Object Windows.Forms.Label
$lblFile.Dock = 'Top'
$lblFile.Height = 24
$lblFile.Text = '(nothing selected)'
$lblFile.Font = New-Object Drawing.Font('Consolas', 9.5, [Drawing.FontStyle]::Bold)
$split.Panel2.Controls.Add($lblFile)

$lblKey = New-Object Windows.Forms.Label
$lblKey.Dock = 'Top'
$lblKey.Height = 30
$lblKey.Text = ''
$lblKey.Font = New-Object Drawing.Font('Consolas', 11)
$split.Panel2.Controls.Add($lblKey)

$box = New-Object Windows.Forms.Panel
$box.Dock = 'Top'
$box.Height = 130
$split.Panel2.Controls.Add($box)

$chkVal = New-Object Windows.Forms.CheckBox
$chkVal.Dock = 'Top'
$chkVal.Height = 30
$chkVal.Text = 'true'
$chkVal.Visible = $false
$box.Controls.Add($chkVal)

$txtVal = New-Object Windows.Forms.TextBox
$txtVal.Dock = 'Top'
$txtVal.Height = 110
$txtVal.Multiline = $true
$txtVal.ScrollBars = 'Vertical'
$txtVal.Font = New-Object Drawing.Font('Consolas', 11)
$box.Controls.Add($txtVal)

$lblDef = New-Object Windows.Forms.Label
$lblDef.Dock = 'Top'
$lblDef.Height = 26
$lblDef.Text = ''
$lblDef.ForeColor = [Drawing.Color]::DimGray
$split.Panel2.Controls.Add($lblDef)

$flow = New-Object Windows.Forms.FlowLayoutPanel
$flow.Dock = 'Top'
$flow.Height = 46
$flow.Padding = New-Object Windows.Forms.Padding(0, 8, 0, 0)
$split.Panel2.Controls.Add($flow)

function Add-Btn([string]$text, [scriptblock]$onClick) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $text
    $b.AutoSize = $true
    $b.Margin = New-Object Windows.Forms.Padding(0, 0, 8, 0)
    $b.Add_Click($onClick)
    $flow.Controls.Add($b)
}

function Load-All {
    $tree.Nodes.Clear()
    $script:Files = [ordered]@{}
    foreach ($f in (Get-CfgFiles)) {
        $doc = Read-CfgFile $f.FullName
        $script:Files[$f.Name] = $doc
        $nodeFile = $tree.Nodes.Add($f.Name)
        $nodeFile.Tag = @{ File = $f.Name; Section = $null; Key = $null; Index = -1 }
        $sections = [ordered]@{}
        foreach ($e in $doc.Entries) {
            if (-not $sections.Contains($e.Section)) { $sections[$e.Section] = [System.Collections.Generic.List[object]]::new() }
            $sections[$e.Section].Add($e)
        }
        foreach ($secObj in @($sections.Keys)) {
            $sec = [string]$secObj
            $nodeSec = $nodeFile.Nodes.Add($sec)
            $nodeSec.Tag = @{ File = $f.Name; Section = $sec; Key = $null; Index = -1 }
            foreach ($e in $sections[$sec]) {
                $n = $nodeSec.Nodes.Add(('{0}  =  {1}' -f $e.Key, $e.Value))
                $n.Tag = @{ File = $f.Name; Section = $e.Section; Key = $e.Key; Index = $e.Index }
            }
        }
    }
    $tree.ExpandAll()
    $total = 0
    foreach ($k in $script:Files.Keys) { $total += $script:Files[$k].Entries.Count }
    $status.Text = '{0} file(s), {1} key(s)  |  {2}' -f $script:Files.Count, $total, $CfgDir
}

function Find-Entry($tag) {
    $doc = $script:Files[$tag.File]
    if ($null -eq $doc) { return $null }
    foreach ($x in $doc.Entries) { if ($x.Index -eq $tag.Index) { return $x } }
    return $null
}

function Show-Entry($tag) {
    $e = Find-Entry $tag
    if ($null -eq $e) { return }
    $script:Current = @{ File = $tag.File; Index = $tag.Index }
    $lblFile.Text = '{0}   [{1}]' -f $tag.File, $e.Section
    $lblKey.Text = $e.Key
    $def = Get-DefaultValue $tag.File $e.Section $e.Key
    if ($null -eq $def) { $lblDef.Text = 'default: (this key is not in payload/config)' }
    else { $lblDef.Text = 'default: {0}' -f $def }
    if ($e.Value -eq 'true' -or $e.Value -eq 'false') {
        $txtVal.Visible = $false
        $chkVal.Visible = $true
        $chkVal.Checked = ($e.Value -eq 'true')
        $chkVal.Text = 'enabled  (file says {0})' -f $e.Value
    } else {
        $chkVal.Visible = $false
        $txtVal.Visible = $true
        $txtVal.Text = $e.Value
    }
}

$tree.Add_AfterSelect({
    $n = $tree.SelectedNode
    if ($null -eq $n) { return }
    if ($null -eq $n.Tag.Key) {
        $lblFile.Text = '{0}   [{1}]' -f $n.Tag.File, $(if ($null -eq $n.Tag.Section) { 'all sections' } else { $n.Tag.Section })
        $script:Current = $null
        return
    }
    Show-Entry $n.Tag
})

function Apply-Current {
    if ($null -eq $script:Current) { return $false }
    $doc = $script:Files[$script:Current.File]
    foreach ($e in $doc.Entries) {
        if ($e.Index -ne $script:Current.Index) { continue }
        $newVal = $null
        if ($chkVal.Visible) {
            if ($chkVal.Checked) { $newVal = 'true' } else { $newVal = 'false' }
        } else {
            $newVal = $txtVal.Text.Trim()
        }
        if ($newVal -eq $e.Value) { return $false }
        $doc.Lines[$e.Index] = '{0} = {1}' -f $e.Key, $newVal
        $e.Value = $newVal
        $script:Dirty = $true
        return $true
    }
    return $false
}

function Save-All {
    Apply-Current | Out-Null
    if (-not $script:Dirty) { $status.Text = 'nothing changed.'; return }
    if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $saved = 0
    foreach ($name in $script:Files.Keys) {
        $doc = $script:Files[$name]
        $orig = [IO.File]::ReadAllText($doc.Path, [Text.Encoding]::UTF8)
        $now = $doc.Lines -join $doc.NewLine
        if ($orig -eq $now) { continue }
        Copy-Item -LiteralPath $doc.Path -Destination (Join-Path $BackupDir ($name + '.' + $stamp + '.bak')) -Force
        [IO.File]::WriteAllText($doc.Path, $now, (New-Object Text.UTF8Encoding($false)))
        $saved++
    }
    $script:Dirty = $false
    Load-All
    $status.Text = 'saved {0} file(s); backups: {1}' -f $saved, $BackupDir
}

Add-Btn 'Save' { Save-All }
Add-Btn 'Reset to default' {
    if ($null -eq $script:Current) { $status.Text = 'pick a key first.'; return }
    $e = Find-Entry $script:Current
    if ($null -eq $e) { return }
    $def = Get-DefaultValue $script:Current.File $e.Section $e.Key
    if ($null -eq $def) { $status.Text = 'this key has no shipped default.'; return }
    if ($chkVal.Visible) { $chkVal.Checked = ($def -eq 'true') } else { $txtVal.Text = $def }
    Apply-Current | Out-Null
    $status.Text = 'reset {0} to default {1} (click Save to write)' -f $e.Key, $def
}
Add-Btn 'Reload' { $script:Dirty = $false; Load-All }
Add-Btn 'Open config folder' { if (Test-Path -LiteralPath $CfgDir) { Start-Process explorer.exe $CfgDir } }
Add-Btn 'Close' { $form.Close() }

$form.Add_FormClosing({
    if ($script:Dirty) {
        $r = [Windows.Forms.MessageBox]::Show('Unsaved changes. Save before closing?', 'VRK Enhance', 'YesNoCancel')
        if ($r -eq 'Cancel') { $_.Cancel = $true; return }
        if ($r -eq 'Yes') { Save-All }
    }
})

Load-All

if ($SelfTest) {
    $out = @()
    $out += 'SELFTEST files=' + $script:Files.Count
    $sum = 0
    foreach ($k in $script:Files.Keys) { $sum += $script:Files[$k].Entries.Count }
    $out += 'SELFTEST keys=' + $sum
    if ($script:Files.Count -gt 0) {
        $first = [string](@($script:Files.Keys)[0])
        $out += 'SELFTEST firstfile=' + $first
        $e0 = @($script:Files[$first].Entries)[0]
        $out += 'SELFTEST firstkey=' + $e0.Key + '=' + $e0.Value
        $out += 'SELFTEST default=' + (Get-DefaultValue $first $e0.Section $e0.Key)
        $out += 'SELFTEST newline=' + ($(if ($script:Files[$first].NewLine -eq $CRLF) { 'crlf' } else { 'lf' }))
    }
    $out += 'SELFTEST cfgdir=' + $CfgDir
    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'configgui-selftest.txt'), $out, (New-Object Text.UTF8Encoding($false)))
    $form.Dispose()
    exit 0
}

[void]$form.ShowDialog()
