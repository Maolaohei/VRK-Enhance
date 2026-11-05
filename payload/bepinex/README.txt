BepInEx 5.4.23.6 (win x64)
==========================

Bundled so the installer can work offline.

Source archive (unmodified):
  https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6
  file:   BepInEx_win_x64_5.4.23.6.zip
  size:   642076 bytes
  sha256: 6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755

Contents: winhttp.dll (UnityDoorstop 4.6.0), doorstop_config.ini, .doorstop_version,
          BepInEx/core/* (BepInEx 5.4.23.6, HarmonyX 2.9.0.0, MonoMod), BepInEx/plugins/,
          BepInEx/config/, changelog.txt

Licenses (they differ per component - do not treat the zip as a single license):
  BepInEx 5.4.23.6 core / HarmonyX / MonoMod ...... MIT   (c) 2018 Bepis
    full text: LICENSE-BepInEx-MIT.txt
    upstream:  https://github.com/BepInEx/BepInEx/blob/v5-lts/LICENSE
  UnityDoorstop 4.6.0 (winhttp.dll) ............... LGPL-2.1
    full text: LICENSE-UnityDoorstop-LGPL-2.1.txt
    source:    https://github.com/NeighTools/UnityDoorstop/tree/v4.6.0
    note: winhttp.dll is a standalone, replaceable file - you may swap or delete it.

See THIRD-PARTY-NOTICES.md in the release root for the full compliance statement.

How the installer uses it: it extracts this zip into the game root only when
BepInEx/core/BepInEx.Preloader.dll is missing, and records every extracted file in
state/manifest.json so the restore script can remove it again.

Doorstop note: 4.x uses the new ini layout ([General] target_assembly=...), which the shipped
doorstop_config.ini already matches. If a game folder still carries a 3.x configuration, the
installer keeps it (it only writes files that are missing) - in that case delete
doorstop_config.ini first and reinstall.
