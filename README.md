# Toggle Interact / 按一下持续干活

**Graveyard Keeper 2（守墓人 2）** 的 BepInEx 插件：把"按一下推进一次进度"的干活动作，改成**按一下就持续干，再按一下停**。

A BepInEx plugin for **Graveyard Keeper 2** that turns single-press work actions into a toggle: **press once to keep working, press again to stop**.

[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-blue)](https://github.com/BepInEx/BepInEx)
[![Target](https://img.shields.io/badge/target-net472-lightgrey)]()

---

## 功能 Features

原版里砍树、挖矿、挖黏土、打水、制作这些动作都要反复按键。装上本插件后：

| 操作 | 结果 |
|---|---|
| 对着目标按一下工作键 | **持续工作**（角色会自动走到工作位再开始） |
| 再按一下同一个键 | 停止 |
| 推摇杆 / 按 WASD | 停止（想走就走，不会被锁住） |
| 按交互键 | 停止，并且这次交互**立即生效**，可以直接选下一个操作 |

In vanilla, chopping, mining, crafting etc. all need repeated key presses. With this plugin:

| Action | Result |
|---|---|
| Press the work key once on a target | **Keeps working** (the character walks to the spot and starts automatically) |
| Press the same key again | Stop |
| Move the stick / press WASD | Stop (you are never locked in place) |
| Press the interact key | Stop, and that interaction **still goes through**, so you can pick the next action right away |

### 覆盖范围 Coverage

判据是「**这个动作会不会显示进度条**」，所以凡是游戏里带进度条的干活动作都覆盖：

砍树、挖矿、挖黏土 / 挖沙、耕地、收割、打水、施肥、种植、坟墓（拆部件）、制作台、纺车 / 各种工作站。

开门、开箱、拿取 / 放置、检查、建造窗口**完全不受影响**——它们没有进度条。

The rule is "**does this action show a progress bar?**". Everything with a progress bar is covered
(chopping, mining, clay / sand digging, tilling, harvesting, drawing water, fertilizing, planting,
graves, crafting stations, spinning wheel, ...). Doors, chests, take / put, inspect and the building
window are untouched — they have no progress bar.

### 形态转化自动衔接 Turns follow through

树砍倒后会变成树桩、矿脉会挖到下一形态——插件会**接着干**，直到转化后的目标也消失才收手，
并且只会跟着**同一个资源点**，不会顺手跑去挖旁边的东西。

当一处的活干完（进度条走完），插件会自动收手；资源类目标会多等 2 秒以覆盖"树倒下"的动画。

Trees turn into stumps, ore nodes into the next form — the plugin keeps going until the transformed
target is gone too, and it only follows **the same resource node**, never wandering to a neighbour.
When the work is actually finished it releases automatically (resource-type targets get a 2 s window
to cover the falling animation).

---

## 环境要求 Requirements

- Graveyard Keeper 2（守墓人 2）
- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases) —— **Unity Mono** 版本，已安装到游戏目录

---

## 安装 Installation

1. 确认 BepInEx 已装好（游戏目录下应有 `winhttp.dll`、`doorstop_config.ini` 和 `BepInEx` 文件夹）。
2. 把 `ToggleInteract.dll` 放进 `<游戏目录>\BepInEx\plugins\`。
3. 启动一次游戏，会自动生成 `BepInEx\config\gk2.toggleinteract.cfg`。
4. 按需修改配置（可选），重启游戏生效。

1. Make sure BepInEx is installed (`winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder
   next to the game executable).
2. Drop `ToggleInteract.dll` into `<Game folder>\BepInEx\plugins\`.
3. Start the game once to generate `BepInEx\config\gk2.toggleinteract.cfg`.
4. Adjust the config if you want, then restart the game.

---

## 配置 Configuration

`BepInEx\config\gk2.toggleinteract.cfg`

```ini
[1. General 总开关]
Enabled = true

[2. Stop rules 停止判定]
IdleTimeoutMs = 3500        # 离开工作状态多久后收手
LostTargetGraceMs = 2500    # 目标消失后待命多久（等下一形态）
AnchorRadius = 2.5          # 同一资源点允许的最大偏移

[3. Diagnostics 诊断]
Verbose = true              # 记录详细日志，稳定后可关
```

| 键 Key | 说明 Description |
|---|---|
| `Enabled` | `false` 时插件完全不生效，行为等同原版。`false` disables the plugin entirely. |
| `IdleTimeoutMs` | 被界面 / 剧情打断后，多久判定为结束。How long out of the work state before giving up. |
| `LostTargetGraceMs` | 目标消失后保持待命多久，用来等树桩之类的下一形态出现。Standby time waiting for the next form. |
| `AnchorRadius` | 判定"同一个资源点"时允许的最大偏移（配合资源族名使用）。Max offset for "same resource node". |
| `Verbose` | 输出 `[probe]` 之类的诊断日志。Diagnostic logging. |

---

## 卸载 Uninstallation

删掉 `BepInEx\plugins\ToggleInteract.dll`（配置可一并删除）。
插件不写入任何存档数据，随时可以安全移除。

Delete `BepInEx\plugins\ToggleInteract.dll` (the config file too, if you like).
Nothing is written to your save file, so it can be removed at any time.

---

## 实现说明 How it works

给想看代码的人（也给以后回来改的自己）：

- **判据**：`PlayerWorkComponent.WorkInProgress` —— 游戏推进进度条时它才是 `true`，
  所以开门 / 开箱这类没有进度条的动作天然被排除，不需要维护动作白名单。
- **接管入口**：`WorkPlayerState.OnEnter()`。注意不能挂 `WGOInteractionHandlerBase.Interact2`——
  玩家按工作键时是 SSM 看到 `CanEnter`（`IsActive && CanWork()`）就直接进状态，
  **根本不调用任何 handler 方法**，挂在那里对所有工作台都无效。
- **维持**：Harmony patch `LazyInput.GetKey(GameKey.Action)` 让它一直返回 `true`
  （`WorkPlayerState.IsActive` 正是靠 `GetKey(Action) || WorkIsTookControl` 成立的）。
- **形态转化**：记录原目标坐标作为锚点，新目标若**位置几乎未动**，或**资源族名相同且偏移在
  `AnchorRadius` 内**，就判定为同一资源点，继续工作。
  族名 = 去掉类型前缀（`tree_` / `stump_` / `rock_` / `ore_` …）和末尾形态号（`_v1` / `_02`）。
- **干完判定**：读目标的 `hpComponent.hp`，归零且目标没有换形态即视为这一处完工；
  资源类目标给 2 秒窗口覆盖倒地动画，其它类型 0.4 秒快速收手。
- **脱身**：原生输入检测移动意图（`LazyInput.GetDirection` + `Input.GetAxisRaw`）；
  按交互键时先解除持续，再用一个短窗口把这次交互键**补还**给游戏，避免"按 A 被吞一次"。

---

## 源码 Source

https://github.com/z2431643195/GraveyardKeeper2-toggle-interact

## 从源码构建 Building from source

需要 .NET SDK 与游戏本体（构建时引用游戏目录下的 DLL）：

```powershell
dotnet build ToggleInteract.csproj -c Release
# 游戏路径不是默认值时：
dotnet build ToggleInteract.csproj -c Release -p:GameDir="D:\Your\Game\Folder"
```

编译产物会自动拷到 `BepInEx\plugins`。打包发布用 `pack.bat`（生成 `release\ToggleInteract-v*.zip`）。

## 许可 License

MIT，见 [LICENSE.txt](LICENSE.txt)。
