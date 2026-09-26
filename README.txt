Toggle Interact / 按一下持续干活
==============================================

Graveyard Keeper 2 (守墓人 2) mod —— 把「按一下推进一次进度」的干活动作，
改成按一下就持续干、再按一下停。

A BepInEx plugin for Graveyard Keeper 2 that turns single-press work actions
into a toggle: press once to keep working, press again to stop.


Requirements 环境要求
---------------------
- Graveyard Keeper 2 (守墓人 2)
- BepInEx 5.4.23.5（Unity Mono 版），已安装在游戏目录
  BepInEx 5.4.23.5 (Unity Mono), already installed in the game folder.


Installation 安装
-----------------
1. 确认 BepInEx 已安装（游戏目录下应有 winhttp.dll、doorstop_config.ini 和 BepInEx 文件夹）。
   Make sure BepInEx is installed (winhttp.dll, doorstop_config.ini and the
   BepInEx folder should live next to the game executable).

2. 把 ToggleInteract.dll 放进 / Drop ToggleInteract.dll into:

       <游戏目录 Game folder>\BepInEx\plugins\

3. 启动一次游戏，会自动生成配置文件 / Start the game once to generate:

       <游戏目录 Game folder>\BepInEx\config\gk2.toggleinteract.cfg

4. 重启游戏生效（BepInEx 只在启动时读取配置）。
   Restart the game (BepInEx reads configs on startup).


怎么用 How to use
-----------------
对着目标按一下工作键 -> 开始持续工作（角色会自动走到工作位再开工）。
再按一下同一个键 -> 停止。

Press the work key once on a target -> it keeps working (the character walks to
the spot and starts automatically). Press the same key again -> stop.

想中途走开时，有几种方式立刻脱身 / Several ways to break out at any time:
  - 推摇杆 / 按 WASD                                  Move the stick / press WASD
  - 再按一下工作键                                    Press the work key again
  - 按交互键（并且这次交互会立即生效，可直接选下一个操作）
    Press the interact key (that interaction still goes through, so you can pick
    the next action right away)


覆盖范围 Coverage
-----------------
判据是「这个动作会不会显示进度条」，所以凡是带进度条的干活动作都会持续化：
砍树、挖矿、挖黏土 / 挖沙、耕地、收割、打水、施肥、种植、坟墓（拆部件）、
制作台、纺车 / 各种工作站。

The rule is "does this action show a progress bar?". Everything with a progress
bar is covered: chopping, mining, clay / sand digging, tilling, harvesting,
drawing water, fertilizing, planting, graves, crafting stations, spinning wheel.

开门、开箱、拿取 / 放置、检查、建造窗口完全不受影响（它们没有进度条）。
Doors, chests, take / put, inspect and the building window are untouched - they
have no progress bar.

形态转化会自动衔接：树砍倒变成树桩、矿脉挖到下一形态都会接着干，直到转化后的
目标也消失才收手；而且只跟着同一个资源点，不会跑去挖旁边的东西。
Trees turn into stumps and ore nodes into the next form; the plugin keeps going
until the transformed target is gone too, and it only follows the same resource
node, never wandering to a neighbour.


Configuration 配置说明
----------------------
[1. General 总开关]
  Enabled            false 时本 mod 完全不生效，行为等同原版。
                     false makes the mod a no-op, behaving exactly like vanilla.

[2. Stop rules 停止判定]
  IdleTimeoutMs      被界面 / 剧情打断后，多久判定为结束（毫秒）。
                     How long out of the work state before giving up (ms).
  LostTargetGraceMs  目标消失后保持待命多久（毫秒），用来等树桩之类的下一形态出现。
                     Standby time waiting for the next form to appear (ms).
  AnchorRadius       判定「同一个资源点」时允许的最大偏移。
                     Max offset when deciding "same resource node".

[3. Diagnostics 诊断]
  Verbose            true 时输出 [probe] 之类的诊断日志，稳定后可关。
                     Diagnostic logging; turn off once things work for you.


Notes 说明
----------
- 不写入任何存档数据，随时可以安全移除。
  Nothing is written to your save file; it can be removed at any time.
- 判据基于「进度条是否在走」，所以不依赖游戏里的具体按键名；如果你改过按键绑定，
  插件依然按你当前绑定的工作键生效。
  The rule is based on the progress bar, not on specific key names, so custom
  key bindings keep working.


Source code 源码
----------------
https://github.com/z2431643195/GraveyardKeeper2-toggle-interact
