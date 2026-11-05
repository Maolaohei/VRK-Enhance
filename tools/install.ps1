# install.ps1 - 兼容英文文件名的转发器，参数原样透传给 setup.ps1
$setup = Join-Path $PSScriptRoot "setup.ps1"
& $setup @args
exit $LASTEXITCODE
