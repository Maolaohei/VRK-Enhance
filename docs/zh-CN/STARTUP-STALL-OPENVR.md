# 启动“未响应”100 秒：SteamVR 客户端阻塞（与插件无关）

## 证据
Auto-dump（`E:\tmp\rk\dumps\watch.log`，17:52:22 起，pid 9748）：

```
+40s  state: responding=False ws=212MB cpu=1.2s
+100s state: responding=False ws=212MB cpu=1.4s
```

主线程（tid 28444）栈，由 `E:\tmp\rk\dmpstack.py` 解出：

```
ntdll+0xC8889
  KERNELBASE+0x73F1E
    VCRUNTIME140+0x1CE01
      vrclient_x64.dll  (十余层)
    ucrtbase+0x33B39
  vrclient_x64.dll+0x4A0000
  openvr_api.dll+0x1D72
  VR_Kanojo.exe+0x6FB613      <- Unity 的 OpenVR 渲染后端
  VR_Kanojo.exe+0x104C828
```

即 **Unity 的 OpenVR 后端 `openvr_api.dll` 去连 SteamVR，`vrclient_x64.dll` 在等 vrserver**，
主线程被按住 100 秒。游戏日志里也一直有
`VR: OpenVR Error! OpenVR failed initialization with error code Unknown error (310)`。
`cpu` 40 秒只涨 0.2 秒 —— 纯等待，不是死循环。

Windows 的判定标准是“5 秒没有消息泵就把窗口标成未响应”，所以这 100 秒整段都显示未响应。

## 处理
`VR_Kanojo_Data/Plugins/openvr_api.dll` 改名为 `openvr_api.dll.disabled`。
Unity 找不到该模块就不会初始化 OpenVR，启动立即进入场景；VR Kanojo 本身是 Oculus
（`OVRPlugin.dll`）作品，免 VR 游玩不需要 OpenVR。

回滚：把 `.disabled` 改回 `.dll`。

## 附带发现
`VR_Kanojo_Data/Plugins/openvr_api.dll` 是 2025-02-08 的文件，与 2016 年的游戏本体不同代，
Unity 5.4.5f1 调用它的旧接口时本就会失败（error 310），失败前的那次连接尝试才是卡顿来源。
