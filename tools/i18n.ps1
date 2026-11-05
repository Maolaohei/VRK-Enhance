# VRK Enhance - language helper shared by setup.ps1 / gui.ps1 / configgui.ps1
# Language resolution order: explicit -Lang > env VRK_LANG > Windows UI culture (zh* -> zh, else en)

function Resolve-VrkLang([string]$Lang) {
    if (-not [string]::IsNullOrWhiteSpace($Lang)) {
        if ($Lang -match '^(?i)(zh|cn|chs|cht)') { return 'zh' }
        if ($Lang -match '^(?i)(en|eng)') { return 'en' }
    }
    $env:Lang = $null
    $fromEnv = [Environment]::GetEnvironmentVariable('VRK_LANG')
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        if ($fromEnv -match '^(?i)(zh|cn|chs|cht)') { return 'zh' }
        if ($fromEnv -match '^(?i)(en|eng)') { return 'en' }
    }
    try {
        $ui = [System.Globalization.CultureInfo]::CurrentUICulture.Name
        if ($ui -like 'zh*') { return 'zh' }
    } catch { }
    return 'en'
}

# M <english> <chinese> -> the string for the active language
function M([string]$En, [string]$Zh) {
    if ($script:VRKLang -eq 'zh') { return $Zh }
    return $En
}
