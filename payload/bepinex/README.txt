BepInEx 5.4.23.6 (win x64)
==========================

Bundled so the installer can work offline. Source:
  https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6
  file: BepInEx_win_x64_5.4.23.6.zip
  sha256: 6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755
  size: 642076 bytes
  contents: winhttp.dll (Doorstop 4.6), doorstop_config.ini, .doorstop_version,
            BepInEx/core/*, BepInEx/plugins/, BepInEx/config/, changelog.txt

License: LGPL-2.1 — see https://github.com/BepInEx/BepInEx/blob/master/LICENSE
The archive is redistributed unmodified. The installer only extracts it into the
game root when no BepInEx installation is found there (the check is
BepInEx/core/BepInEx.Preloader.dll) and records every extracted file in
state/manifest.json so the restore script can remove it again.

Doorstop note: 4.x uses the new ini layout ([General] target_assembly=...),
which the shipped doorstop_config.ini already matches. If a game folder still
carries a 3.x configuration, the installer keeps it (it only writes files that
are missing) — in that case delete doorstop_config.ini first and reinstall.
