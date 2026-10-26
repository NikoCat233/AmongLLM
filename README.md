# AmongLLM

Among Us 游戏控制桥：一个本机 HTTP MCP server，以及与之配套的 BepInEx IL2CPP mod。服务端支持多个游戏实例；插件通过 WebSocket 注册、上报状态、执行命令并自动重连。

## 构建和运行

需要 .NET 10 SDK。游戏插件目标为安装内的 .NET 6 / BepInEx IL2CPP，并使用实际游戏生成的 interop DLL。

```powershell
./scripts/Build.ps1 -GamePath D:\Game\20260929
./scripts/Start-McpServer.ps1
```

`artifacts/AmongLLM-game-plugin.zip` 是安装包：将其中的 `BepInEx` 文件夹合并到游戏目录。包中保留 Roslyn 等托管依赖。构建脚本只生成安装包；安装后的插件在游戏启动时自动接入服务。

默认地址是 `http://127.0.0.1:26767/mcp`，端口被占用时服务自动尝试 `26768`。游戏插件会检查两个端口的服务身份。服务端以 JSON 日志输出实际监听地址、实例连接、命令发送和结果摘要。

仓库的 `.codex/config.toml` 已按 HTTP MCP 配置 `amongllm`，默认使用 26767。在服务启动后运行下列命令，可以检测实际端口并更新这个项目的配置；其他 MCP 客户端使用输出的 URL 注册即可：

```powershell
./scripts/Select-McpEndpoint.ps1
```

项目级配置在受信任的项目中生效。客户端须重新加载 MCP 配置后才会发现新工具。配置依据：[Codex MCP 官方文档](https://developers.openai.com/codex/mcp/)。

## 能力

| 类别 | 工具 |
| --- | --- |
| 多实例与状态 | `list_instances`、`get_game_status` |
| 游戏画面与窗口 | `capture_game_screenshot`、`set_window_state` |
| 结构化 UI | `get_screen`、`set_text`、`click_button`、`click_sequence` |
| 账号 | `get_account_info`、`set_itch_auth_token_and_login` |
| 游戏消息 | `send_chat_message`、`send_rpc`、`send_hazel_message` |
| 位置与网络对象 | `snap_to`、`list_net_objects`、`read_net_object_properties`、`write_net_object_property` |
| 诊断与扩展 | `execute_csharp`、`list_loaded_mods`、`read_game_logs` |

所有游戏命令指定 `instanceId`。点击序列支持依次点击、固定等待和等待按钮出现；单个游戏实例中的命令按顺序执行。断线、取消和超时会停止尚未执行的步骤，已完成的操作不会自动重放。

每个 `click` / `waitForButton` 步骤应由 agent 明确给出 `timeoutMs`，省略时默认 5000 毫秒；按钮在上限内未出现就停止序列。注册、实例列表与状态中包含客户端的 `processId` 和 `workingDirectory`，便于定位进程和目录。

房主可以读写房间内所有已注册网络对象，并对其他玩家 SnapTo。普通客户端可以读取对象，写入和 RPC 限于自己拥有的对象。属性修改返回本地读回结果；要同步给其他玩家，需要调用游戏对应的 RPC 或序列化路径。

Itch 账号切换在运行时检查 `Platforms.StandaloneItch`，将 Itch API key 写入 `EOSManager.platformAuthToken` 后直接执行 `LoginWithCorrectPlatform()`。实际 interop 把原始 private 字段生成为带 IL2CPP 写屏障的访问属性。只有登录流程完成且 `LoginStatus == LoggedIn` 才返回成功。Steam 源码中的平台常量不用于替代目标游戏的运行时判断。

`send_rpc` 直接使用 `StartRpcImmediately` / `FinishRpcImmediately`。`send_hazel_message` 接受完整的类型化 writer 指令，包含原始 Hazel 标签嵌套。C# 片段在游戏进程内运行；正常异步续体回到 Unity 主线程，超时不能安全中断同步死循环。

`read_game_logs` 按游标读取 Unity 和全部 BepInEx log source 的事件。完整实时流从插件初始化开始镜像到 `%LOCALAPPDATA%/AmongLLM/logs/<instanceId>.jsonl`；更早的启动日志路径也会返回。内存环形缓冲区和磁盘写入异常都有明确的丢失计数。

两端协议和参数示例见 [服务端说明](mcp-server/README.md)，游戏 API、安装依赖和行为限制见 [插件说明](game-plugin/README.md)。

## 验证范围

`Build.ps1` 运行服务端与插件传输测试，再发布两端及 mod ZIP。测试覆盖真实 HTTP MCP 初始化/工具发现、两端 WebSocket 协议、多实例、端口回退、断线与取消、日志游标/镜像、主线程续体和嵌套属性写回。

插件已使用 `D:\Game\20260929` 的实际 interop 构建。自动化测试不启动游戏；截图、账号切换、房主操作及 Hazel 远端接收仍需要游戏内验收，构建与托管协议测试不构成这部分运行证据。
