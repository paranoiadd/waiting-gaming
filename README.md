# Codex 等待小游戏 · 2048

在 Windows Codex 桌面版发送消息后，回合持续超过 4 秒时，触发任务的 Codex 窗口上会出现一个游戏邀请。点击「开始游戏」打开 2048，点击「本次不玩」跳过本轮。邀请出现时保留当前输入焦点，回合收尾或中断时会取消尚未接受的邀请。

第一次打开游戏时，棋盘位于触发任务的 Codex 窗口所在屏幕中央。你可以拖动游戏窗口，关闭后，下次会恢复上次关闭时的位置。点击游戏窗口后用方向键移动，棋盘和分数会在每次有效移动后保存。点击「重新开局」可以重开并保留最高分。

计时从本机异步开始事件记录时算起。收到 `Stop` Hook 时，已经打开的游戏显示「回合收尾中」，可以切回 Codex 看回复。其他 Hook 仍可能要求这轮任务继续。手动取消回合时，游戏会显示中断状态并保留棋盘。多个本地会话共用同一份棋盘和一个游戏窗口。[OpenAI Hooks 文档](https://learn.chatgpt.com/docs/hooks)

多个 Codex 窗口同时打开时，插件根据任务开始时的前台 Codex 窗口判断邀请所属窗口，可能与实际触发会话所属窗口不同。只有所属窗口在前台时才显示邀请，切换到其他窗口或最小化时暂时隐藏，返回后恢复。无法确定所属窗口时跳过本轮邀请。

## 构建与预览

从 GitHub 克隆项目，在 PowerShell 中构建并运行测试。已有源码时，从项目目录运行最后一条命令即可。构建脚本使用 Windows 自带的 .NET Framework 4.x C# 编译器，游戏无需联网。

```powershell
git clone https://github.com/paranoiadd/waiting-gaming.git
Set-Location waiting-gaming
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

想先试玩法，可以直接启动预览。预览使用临时棋盘，关闭后清理。

```powershell
.\dist\marketplace\plugins\waiting-gaming\bin\WaitingGaming.exe --preview
```

## 安装到 Codex

需要在 Windows 桌面登录并使用 Codex 本地会话，以便 Hook 和游戏窗口在同一桌面会话中运行。

可以直接从 GitHub 安装仓库内的 Windows 预构建包，然后审查 Hooks 并新开本地会话。

```powershell
codex plugin marketplace add paranoiadd/waiting-gaming --ref main
codex plugin add waiting-gaming@waiting-gaming
```

如果要从源码构建并安装本地版本，按下面的步骤操作。

1. 在同一目录用 `codex` 注册构建生成的本地插件目录。

   ```powershell
   codex plugin marketplace add (Resolve-Path .\dist\marketplace).Path
   ```

2. 重启 Codex 桌面版。在插件目录里选择 **Waiting Gaming Local**，安装「等待小游戏 · 2048」。
3. 打开 **设置 → Hooks**，查看并信任这个插件的 `UserPromptSubmit`、`Stop` 和 `Interrupt` Hooks。
4. 开一个本地 Codex 会话，发送一个通常需要几秒以上的任务。在所属 Codex 窗口中等待邀请出现，先确认输入焦点还在，再点击「开始游戏」，移动游戏窗口并关闭，下一轮验证位置恢复。

登记本地目录后，也可以通过 CLI 安装插件，然后打开桌面版完成 Hook 审查。

```powershell
codex plugin add waiting-gaming@waiting-gaming-local
```

本地插件的安装与 Hook 信任是两步。修改 Hook 后，Codex 可能要求重新审查。[OpenAI 插件打包文档](https://developers.openai.com/plugins/build/plugins) · [OpenAI Hooks 文档](https://learn.chatgpt.com/docs/hooks)

## 停用与排查

- **看不到插件**。确认构建成功，并运行 `codex plugin marketplace list` 检查本地目录是否登记。然后重启 Codex 桌面版。
- **预览正常，聊天时没有邀请**。先检查插件是否启用、三个 Hooks 是否已信任，以及当前是否为 Windows 本地 Codex 会话。等待时保持触发任务的 Codex 窗口在前台。短回合和无法确定所属窗口的回合会跳过邀请。
- **停用插件**。在 Codex 插件目录的已安装列表中停用或卸载插件，已经打开的小窗可以直接关闭。若以后也无需从这份本地目录安装，运行 `codex plugin marketplace remove waiting-gaming-local`。

运行期间，插件的数据目录保存回合标识的哈希、事件类型、时间、所属窗口及其进程标识，以及棋盘、分数和上次关闭位置。

## 发布与许可证

项目采用 [MIT 许可证](LICENSE)，安全问题请通过 [私密报告入口](https://github.com/paranoiadd/waiting-gaming/security/advisories/new)提交。

`package/` 是用于仓库插件目录分发的完整安装包，包含清单、Hooks、图标和 Windows 可执行程序。运行 `build.ps1 -Test` 会重新构建并更新 `package/bin/`，发布时将这两个程序与对应源码一起提交。
