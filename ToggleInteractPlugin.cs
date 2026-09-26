using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace ToggleInteract
{
    /*
     * ============================================================================
     *  Toggle Interact —— 按一下持续干活，再按一下停
     * ============================================================================
     *
     *  ★ 放行判据 = 「屏幕上会出现进度条的动作」
     *      进度条的源头（反编译 PlayerWorkComponent 确认）：
     *          IComponent comp = GetComponentForInteraction(currentWgo);
     *          if (comp is CraftComponent) SetCraftActivity(...);   // 手工制作 -> craft 进度
     *          else                        SetHPActivity(...);       // 砍树/挖矿/施工 -> 目标 HP
     *
     *          // UpdateInteraction():
     *          if ((toolComponent.IsActionActive || TryStartInteraction())
     *              && toolComponent.IsActionActive) { toolComponent.UpdateInteraction(); workInProgress = true; }
     *
     *      只要游戏在推进进度条，playerWorkComponent.WorkInProgress 就是 true。
     *      有进度条的动作（砍树 / 挖矿 / 挖黏土沙 / 耕地 / 收割 / 工地施工 /
     *      打水 / 施肥 / 种植 / 坟墓 / 制作 …）都会置位；
     *      开门、开箱、拿取这类没有进度条的动作不会，所以不会被接管。
     *
     *      提示文本名单（hint_*）保留作为兜底：极短的一次点击可能来不及观察到
     *      WorkInProgress，此时用「刚命中过干活动作」来补齐。
     *
     *  持续机制（反编译 WorkPlayerState 确认）：
     *      IsActive => LazyInput.GetKey(GameKey.Action) || playerWorkComponent.WorkIsTookControl
     *      只要让「工作键一直被按住」成立，玩家就会一直工作。
     *
     *  ★ 转化目标（v10）
     *      树被砍倒后会变成树桩、矿脉会切换到下一形态，游戏自己在
     *      WorkPlayerState.Update 里换 handler 继续：
     *          if (targetWgo != playerWorkComponent.Wgo && playerWorkComponent.Wgo != null)
     *              -> OnInteractionTargetExit() / OnInteractionTargetEnter()
     *      所以「转化」不需要我们插手，插手反而会打断它。
     *
     *      难点是区分「同一格上的形态转化」和「游戏顺手吸附到的旁边资源」。
     *      实测日志：
     *          tree_fir_clr1_l_v1   --砍倒-->  stump_fir_clr1_l_v1     <- 同格转化，继续挖
     *          stump_fir_clr1_l_v1  --挖掉-->  tree_fir_clr1_xs_v1      <- 旁边小树，隔 1.4，要停
     *      形态转化时位置根本不动，而旁边那棵隔了 1.4（约一格多），
     *      所以直接用「同一格」判定（AnchorRadius = 1）：
     *          · 距离 <= AnchorRadius -> 同一格上的转化，继续（锚点跟随）
     *          · 超出                -> 转化链结束，停止
     *      即：树 -> 树桩 -> 树桩被挖掉才停；矿 -> 下一形态 -> 挖空才停。
     *      资源族名（FamilyOf）保留在日志里，方便核对是哪种情况。
     *
     *  停止（先到先停）：
     *      1. 想移动（推摇杆 / 按 WASD）
     *         · 这类交互目标会一直在、进度一直在走（坟地、建造、反复敲击类），
     *           只靠「目标消失」会把人永久锁在工作状态里，所以检测到移动意图就放人。
     *         · 但只在「真正开工过」之后才判定：原版对着需要站到特定位置的工作台
     *           按一下，角色会自动走过去再开工（TryStartInteraction -> StartMovement），
     *           这段寻路期间玩家往往还推着摇杆，一上来就判停会变成「按一下挪一步」。
     *      2. 再按一次开启时那个键
     *      3. 目标彻底消失（挖空/砍完）持续 LostTargetGraceMs（默认 0.25 秒）
     *      4. 离开工作状态持续 IdleTimeoutMs
     *      5. 目标跳到同一格之外（转化结束，换到了旁边的资源）
     *      6. 游戏抛「资源不足」事件（体力 / 精神）—— 订阅 OnNotEnoughResOccurred
     *      7. 进度停滞 StallSeconds（只对能读到真实 HP 进度的目标生效）
     *      8. 按了功能键（暂停 / 角色 / 科技树 / 任务 / 地图 / 灵感 / Tab / Esc）
     *         —— 立刻收手，并把这一次按键补还给游戏，让界面照常打开。
     *
     *         · 「哪个键 = 哪个 GameKey」直接读游戏自己的绑定表
     *           （LazyInput.GameBindings.keyBindings / gamepadBindings），
     *           所以玩家在游戏里改键也自动跟随；配置里那两张表只是可选覆盖。
     *           （一个物理键可能绑多个 GameKey，所以建表时只收功能键，
     *             否则会被同键的其它 GameKey 覆盖 —— 实测踩过。）
     *         · 干活期间游戏不处理玩家输入（UpdateInput 所在玩家状态
     *           IsActive => IsControlsEnabled，而 ByWork 暂停把它压成 false），
     *           所以这次按键会被整体丢掉。收手后把那次 GameKey 伪造回去
     *           （让 GetKeyDown 返回一次 true），游戏自己就会开对应的界面 ——
     *           mod 不复制任何界面逻辑，开什么、页签解没解锁全由游戏决定。
     *         · 键盘的 GetKeyDown 在干活时其实仍然有效，只有手柄读不到
     *           （手柄不是当前活动输入设备），所以两条路径各走各的。
     *         · 这类收手走 StopForUi()：顺手清掉 SuppressWhileInWork 与
     *           LastStopTime 两个防抖标记，否则关掉界面后要等一会儿才能重新持续化。
     *         · 另有一条兜底：界面接管信号 ByControl 也会触发收手。
     *      9. 工作台缺材料（CraftElementsQueue[0].CraftStatus == NotEnoughResources）
     *
     *  特例：hint_build 是「打开建筑选择窗口」（BuildManager.TryEnable -> OpenBuildingWindow），
     *        不是干活动作，显式排除，确保永远只弹一次窗。
     * ============================================================================
     */

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ToggleInteractPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "gk2.toggleinteract";
        public const string PluginName = "Toggle Interact";
        public const string PluginVersion = "1.0.2";

        internal static ToggleInteractPlugin Instance;

        /// <summary>配置里唯一的开关。</summary>
        internal static ConfigEntry<bool> Enabled;

        // ---- 内部参数：不暴露到配置文件，需要时改这里重新编译 ----
        // Internal tuning values: not exposed in the config file, change here and rebuild.
        private const float IdleSecondsConst = 3.5f;             // 离开工作状态多久后收手
        private const float LostTargetGraceSecondsConst = 2.5f;  // 目标消失后待命多久（等下一形态）
        private const float RadiusConst = 2.5f;                  // 同一资源点允许的最大偏移
        private const float StallSecondsConst = 4.5f;            // 进度停滞多久判定为「卡住了」
        private const bool VerboseConst = false;                 // 详细诊断日志

        internal static bool WorkActive;              // 是否正在维持持续工作
        internal static int ActionKeyId = 101;        // GameKey.Action 的数值
        internal static int InteractionKeyId = -1;    // GameKey.Interaction 的数值
        internal static bool ReplayInteractionPending; // 需要把一次交互键补还给游戏
        internal static float ReplayInteractionAt;

        internal static float ForceReleaseUntil;      // 这段时间内强制「工作键已松开」
        internal static float SuppressWhileInWork;    // >0 表示退出工作状态前不再接管
        internal static float LastWorkActionTime;     // 最近一次「干活」动作的时间
        internal static int TriggerGameKeyId = -1;    // 开启时用的 GameKey（同键取消）
        internal static float ResShortageTime;        // 游戏抛「资源不足」事件的时刻
        internal static string ResShortageId;         // "energy" / "insanity"
        internal static float UiTookControlTime;      // 游戏把控制权交给界面（ByControl）的时刻
        internal static string UiTookControlReason;   // 触发来源（ByControl / key:xxx）

        // ---------- 功能键（暂停 / 角色 / 科技树 / 地图 …）的补还机制 ----------
        // 干活期间 LazyInput 不采集按键（实测按钮 6 连 CharacterWindow / Inventory
        // 都读不到，即 GetKeyDown 对所有 GameKey 都是 false），而玩家的按键处理
        // UpdateInput() 这时也被跳过 —— 所以整次按键会被丢掉。
        //
        // 做法：正常游玩时学会「原始手柄按钮 -> GameKey」；干活时认出原始按钮，
        //       收手后把那次 GameKey 伪造回去（让 GetKeyDown 返回一次 true），
        //       游戏自己的 UpdateInput() 就会照常处理它 —— 等于「把按键递给游戏」。
        //       开什么界面、页签解没解锁，全部仍由游戏自己判断。
        internal static int ReplayKeyId = -1;          // 待补还给游戏的 GameKey（-1 = 无）
        internal static float ReplayKeyAt;             // 早于这个时间不放行（等状态退出）

        internal static readonly Dictionary<int, int> RawButtonToGameKey = new Dictionary<int, int>();
        internal static readonly Dictionary<KeyCode, int> KeyCodeToGameKey = new Dictionary<KeyCode, int>();

        internal static bool BindingsLoaded;          // 游戏绑定表是否已成功读到
        internal static float NextBindingsRetry;      // 下次重试时刻
        private static bool _bindingsErrorLogged;

        /// <summary>「按钮编号=GameKey名」的持久化映射（手柄），游戏里正常按一次键就会学习并写回。</summary>
        internal static ConfigEntry<string> GamepadMap;

        /// <summary>「键盘按键=GameKey名」的持久化映射，同样会自动学习并写回。</summary>
        internal static ConfigEntry<string> KeyboardKeyMap;

        /// <summary>需要「收手 + 把按键递回游戏」的 GameKey。</summary>
        internal static readonly GameKey[] SpecialKeys =
        {
            GameKey.InGameMenu,
            GameKey.CharacterWindow,
            GameKey.Inventory,
            GameKey.TechTree,
            GameKey.QuestTree,
            GameKey.Map,
            GameKey.Inspirations,
            GameKey.Back
        };

        internal static bool IsSpecialKeyId(int id)
        {
            if (id < 0) return false;
            foreach (GameKey k in SpecialKeys)
            {
                if (KeyId(k) == id) return true;
            }
            return false;
        }

        /// <summary>原始手柄按钮扫描（0~19）。同一帧多个键、或一个都没有时返回 -1。</summary>
        internal static int RawButtonDown()
        {
            int found = -1;
            try
            {
                for (int b = 0; b < 20; b++)
                {
                    if (UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + b)))
                    {
                        if (found >= 0) return -1;
                        found = b;
                    }
                }
            }
            catch { }
            return found;
        }

        /// <summary>把一次功能键按键补还给游戏（收手后隔几帧放行，那时游戏已经能处理了）。</summary>
        internal static void ReplayKey(int keyId)
        {
            if (keyId < 0) return;
            ReplayKeyId = keyId;
            ReplayKeyAt = Time.time + 0.06f;
        }

        private static int _lastLearnFrame = -1;

        // 用来判断「这一帧是不是刚按下」——上一帧就已经按着的键不参与学习，
        // 否则会把「按住 A 的同时按 B」错配成 B -> A 对应的 GameKey。
        private static int _learnFrame = -1;
        private static HashSet<int> KeysDownThisFrame = new HashSet<int>();
        private static HashSet<int> KeysDownPrevFrame = new HashSet<int>();

        /// <summary>可以参与学习的键盘键（字母 / 数字 / 小键盘 / 功能键 / 常用符号），启动时建一次。</summary>
        private static readonly KeyCode[] LearnKeyCandidates = BuildLearnCandidates();

        private static KeyCode[] BuildLearnCandidates()
        {
            List<KeyCode> list = new List<KeyCode>();
            CollectRange(list, KeyCode.A, KeyCode.Z);
            CollectRange(list, KeyCode.Alpha0, KeyCode.Alpha9);
            CollectRange(list, KeyCode.Keypad0, KeyCode.Keypad9);
            CollectRange(list, KeyCode.F1, KeyCode.F12);

            KeyCode[] extra =
            {
                KeyCode.Escape, KeyCode.Tab, KeyCode.Space, KeyCode.Return, KeyCode.BackQuote,
                KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.Minus, KeyCode.Equals, KeyCode.LeftBracket, KeyCode.RightBracket,
                KeyCode.Semicolon, KeyCode.Quote, KeyCode.Comma, KeyCode.Period,
                KeyCode.Slash, KeyCode.Backslash
            };
            foreach (KeyCode k in extra)
            {
                if (Enum.IsDefined(typeof(KeyCode), k)) list.Add(k);
            }
            return list.ToArray();
        }

        private static void CollectRange(List<KeyCode> list, KeyCode from, KeyCode to)
        {
            for (int i = (int)from; i <= (int)to; i++)
            {
                KeyCode k = (KeyCode)i;
                if (Enum.IsDefined(typeof(KeyCode), k)) list.Add(k);
            }
        }

        /// <summary>这一帧按下的候选键盘键（恰好一个才返回，否则 None）。</summary>
        private static KeyCode KeyboardKeyDown()
        {
            KeyCode hit = KeyCode.None;
            int count = 0;
            foreach (KeyCode kc in LearnKeyCandidates)
            {
                if (!UnityEngine.Input.GetKeyDown(kc)) continue;
                if (++count > 1) return KeyCode.None;      // 同一帧多个键，放弃
                hit = kc;
            }
            return (count == 1) ? hit : KeyCode.None;
        }

        /// <summary>
        /// 学习「输入 -> GameKey」的对应关系（手柄按钮 + 键盘键）。
        /// 只在正常游玩时学得到 —— 干活期间游戏不采集按键，GetKeyDown 全是 false。
        /// 每帧最多学一次。
        /// </summary>
        internal static void LearnMapping(GameKey key)
        {
            try
            {
                if (!IsSpecialKeyId(KeyId(key))) return;

                int id = KeyId(key);

                // 「新按下」判定：上一帧就已经按着的 GameKey 不参与学习 ——
                // 避免把一个一直按着的键错配给这一帧新按下的键。
                if (Time.frameCount != _learnFrame)
                {
                    _learnFrame = Time.frameCount;
                    KeysDownPrevFrame = KeysDownThisFrame;
                    KeysDownThisFrame = new HashSet<int>();
                }
                bool wasDown = KeysDownPrevFrame.Contains(id);
                KeysDownThisFrame.Add(id);
                if (wasDown) return;

                if (Time.frameCount == _lastLearnFrame) return;   // 本帧已经扫过一次
                _lastLearnFrame = Time.frameCount;

                int raw = RawButtonDown();
                if (raw >= 0)
                {
                    int oldPad;
                    if (RawButtonToGameKey.TryGetValue(raw, out oldPad) && oldPad == id) return;
                    RawButtonToGameKey[raw] = id;
                    Log($"[map] 学到 手柄按钮 {raw} -> {IdName(id)} / learned raw button {raw} -> {IdName(id)}");
                    return;
                }

                KeyCode kc = KeyboardKeyDown();
                if (kc == KeyCode.None) return;

                int oldKey;
                if (KeyCodeToGameKey.TryGetValue(kc, out oldKey) && oldKey == id) return;
                KeyCodeToGameKey[kc] = id;
                Log($"[map] 学到 键盘键 {kc} -> {IdName(id)} / learned key {kc} -> {IdName(id)}");
            }
            catch { }
        }

        private void Awake()
        {
            try
            {
                Instance = this;

                Enabled = Config.Bind("1. General 总开关", "Enabled", true,
                    "是否启用。判据是「该动作会不会显示进度条」——会显示进度条的动作" +
                    "（砍树 / 挖矿 / 施工 / 制作 / 种植 / 施肥 / 打水 …）会被持续化；" +
                    "开门、开箱、拿取这类没有进度条的动作不受影响。\n" +
                    "设为 false 时行为完全等同原版，无需卸载。\n" +
                    "Enable the mod. Any action that shows a progress bar is made continuous.\n" +
                    "Set to false to behave exactly like vanilla, no uninstall needed.");

                // 「原始手柄按钮 -> GameKey」的映射。出厂默认就是实测到的两个键：
                //   6 = Back/Select -> CharacterWindow（角色 / 背包）
                //   7 = Start/Menu  -> InGameMenu（暂停界面）
                // 干活期间游戏不采集按键，只能靠这张表把按键递回给游戏，
                // 所以它是功能的一部分 —— 键位不一样时改这里即可（也可以在游戏里正常按一次让它自己学）。
                GamepadMap = Config.Bind("2. 功能键映射 / Function key map",
                    "RawButtonToGameKey",
                    "",
                    "手柄原始按钮编号 -> GameKey 名称，多个用逗号分隔（例：6=CharacterWindow）。\n" +
                    "通常留空即可 —— 对应关系直接读游戏自己的键位绑定（你在游戏里改键也自动跟着变）。\n" +
                    "本表只用于手工补充 / 覆盖：这里写的条目优先于游戏绑定表。\n" +
                    "Format: rawButton=GameKeyName, comma separated. Leave empty to use the game's own bindings.");

                // 键盘同理。出厂默认就是游戏自带的五个面板键 + Esc：
                //   I = 背包/角色   T = 科技树   P = 灵感   Q = 任务   M = 地图   Esc = 暂停
                KeyboardKeyMap = Config.Bind("2. 功能键映射 / Function key map",
                    "KeyboardKeyToGameKey",
                    "",
                    "键盘按键 -> GameKey 名称，多个用逗号分隔。通常留空即可，同上：\n" +
                    "对应关系直接读游戏自己的键位绑定，你在游戏里改键也自动跟着变；此处仅用于手工覆盖。\n" +
                    "Format: KeyCode=GameKeyName, comma separated. Leave empty to use the game's own bindings.");

                // 配置里那两张表只是可选补充；键位 -> GameKey 的主体来自游戏自己的绑定表，
                // 但那时游戏还没初始化好，所以放在 Update 里重试（见 Patch_GetKey）。
                LoadMap();

                int actionId = KeyId(GameKey.Action);
                if (actionId >= 0) ActionKeyId = actionId;

                int interactId = KeyId(GameKey.Interaction);
                if (interactId >= 0) InteractionKeyId = interactId;


                Harmony harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(ToggleInteractPlugin).Assembly);

                // WorkPlayerState 是 internal 类型，只能反射挂；单独包一层，
                // 万一没挂上也不会牵连上面那批补丁。
                try
                {
                    Type workStateType = FindType("WorkPlayerState");
                    MethodInfo onEnter = workStateType != null
                        ? AccessTools.Method(workStateType, "OnEnter")
                        : null;

                    if (onEnter != null)
                    {
                        harmony.Patch(onEnter,
                            postfix: new HarmonyMethod(typeof(WorkStateHook), nameof(WorkStateHook.Postfix)));
                    }
                    else
                    {
                        Logger.LogWarning($"{PluginName}: 未找到 WorkPlayerState.OnEnter，工作状态接管不可用");
                    }
                }
                catch (Exception hookEx)
                {
                    Logger.LogWarning($"{PluginName}: 挂 WorkPlayerState.OnEnter 失败: {hookEx.Message}");
                }

                // 挂「界面接管操作」——游戏打开菜单 / 窗口时调用
                //     PlayerInteractionComponent.SetPauseState(PlayerInteractionPauseType.ByControl, true)
                // 实测日志：
                //     Player SetPauseState:[ByControl] isPaused:[True]
                // 而干活时用的是 [ByWork]，两者能区分，
                // 所以 ByControl 是「按了菜单/暂停键」最可靠的判据（不依赖具体键码）。
                try
                {
                    Type pauseType = FindType("PlayerInteractionPauseType");
                    MethodInfo pauseSetter = pauseType != null
                        ? FindMethodWithParams("SetPauseState", pauseType, typeof(bool))
                        : null;

                    if (pauseSetter != null)
                    {
                        harmony.Patch(pauseSetter, postfix: new HarmonyMethod(
                            typeof(PauseStateHook), nameof(PauseStateHook.Postfix)));
                        Logger.LogInfo($"{PluginName}: 已挂载 {pauseSetter.DeclaringType?.Name}.SetPauseState(…, bool)");
                    }
                    else
                    {
                        Logger.LogWarning($"{PluginName}: 未找到 SetPauseState(PlayerInteractionPauseType, bool)，菜单接管判定不可用");
                    }
                }
                catch (Exception pauseEx)
                {
                    Logger.LogWarning($"{PluginName}: 挂 SetPauseState 失败: {pauseEx.Message}");
                }

                // 订阅游戏自己的「资源不足」事件（体力 / 精神）。
                // 反编译确认 PlayerHPActivity / PlayerCraftActivity.IsEnoughEnergy：
                //     若 PlayerEnergyGameResSystem.GetSystem().IsEnoughValue(energyPerTick) 为 false
                //     就 Invoke OnNotEnoughResOccurred("energy")，UI 的提示也靠它。
                // 这是最可靠的「干不下去了」信号 —— 之前体力耗尽时我们会一直按着工作键空转。
                try { PlayerHPActivity.OnNotEnoughResOccurred += OnResShortage; } catch { }
                try { PlayerCraftActivity.OnNotEnoughResOccurred += OnResShortage; } catch { }

                GameObject host = new GameObject("ToggleInteractController");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<InteractBehaviour>();

                Logger.LogInfo($"{PluginName} v{PluginVersion} 已加载 / loaded (放行 {HintHelper.KeyList})");
            }
            catch (Exception ex)
            {
                Logger.LogError($"{PluginName} 加载失败 / failed: {ex}");
            }
        }

        internal static float IdleSeconds => IdleSecondsConst;

        internal static float LostTargetGraceSeconds => LostTargetGraceSecondsConst;

        internal static float Radius => RadiusConst;

        internal static float StallSeconds => StallSecondsConst;

        internal static bool VerboseMode => VerboseConst;

        internal static void Log(string message) => Instance?.Logger.LogInfo(message);
        internal static void LogError(string message) => Instance?.Logger.LogError(message);

        /// <summary>游戏抛「资源不足」时的回调。记下时刻，让 Behaviour 立刻收手。</summary>
        private static void OnResShortage(string resId)
        {
            try
            {
                ResShortageTime = Time.time;
                ResShortageId = resId;
                Log($"[res-shortage] 资源不足 / not enough resources: {resId}");
            }
            catch { }
        }

        /// <summary>GameKey 带 value 字段，直接 ToString 只有类型名。</summary>
        internal static int KeyId(GameKey key)
        {
            try
            {
                Type t = typeof(GameKey);
                PropertyInfo p = t.GetProperty("value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return (int)p.GetValue(key, null);

                FieldInfo f = t.GetField("value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? (int)f.GetValue(key) : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 按数值反查 GameKey 的名字（如 "CharacterWindow"）。
        /// GameKey 的 ToString() 只会给出类型名，日志里没法看，所以遍历它的静态实例反查。
        /// </summary>
        internal static string IdName(int id)
        {
            try
            {
                foreach (FieldInfo f in typeof(GameKey).GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(GameKey)) continue;
                    object v = f.GetValue(null);
                    if (v == null) continue;
                    if (KeyId((GameKey)v) == id) return f.Name;
                }
            }
            catch { }
            return "GameKey#" + id;
        }

        internal static string KeyName(GameKey key) => IdName(KeyId(key));

        /// <summary>包装类型（GameKey / GamepadButton 等）的 value 字段。</summary>
        internal static int WrapperValue(object wrapper)
        {
            try
            {
                if (wrapper == null) return -1;
                Type t = wrapper.GetType();
                PropertyInfo p = t.GetProperty("value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return (int)p.GetValue(wrapper, null);

                FieldInfo f = t.GetField("value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? (int)f.GetValue(wrapper) : -1;
            }
            catch { return -1; }
        }

        /// <summary>按数值反查包装类型的静态实例名（GameKey / GamepadButton 通用）。</summary>
        internal static string WrapperName(object wrapper, Type wrapperType)
        {
            try
            {
                int id = WrapperValue(wrapper);
                if (id < 0) return null;
                foreach (FieldInfo f in wrapperType.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != wrapperType) continue;
                    object v = f.GetValue(null);
                    if (v == null) continue;
                    if (WrapperValue(v) == id) return f.Name;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Unity 传统输入里 Xbox 手柄按钮的物理编号。
        /// 注意：这是**硬件布局**（A 永远在 0 号位），不是游戏的键位绑定 ——
        /// "哪个按钮对应哪个 GameKey" 完全来自游戏自己的绑定表，见 LoadGameBindings()。
        /// </summary>
        private static int JoystickIndexByName(string buttonName)
        {
            switch (buttonName)
            {
                case "A": return 0;
                case "B": return 1;
                case "X": return 2;
                case "Y": return 3;
                case "LB": return 4;
                case "RB": return 5;
                case "Back": return 6;
                case "Start": return 7;
                case "LStick": return 8;
                case "RStick": return 9;
                default: return -1;     // 十字键 / 扳机在传统输入里走的是轴，不在 JoystickButton 里
            }
        }

        /// <summary>
        /// ★ 功能键映射 = 直接读游戏自己的键位绑定表（不写死任何键位）。
        ///     LazyInput.GameBindings.keyBindings[i]     -> keyCode / additionalKeyCodes / gameKey
        ///     LazyInput.GameBindings.gamepadBindings[i] -> gamepadButton / gameKey
        /// 用户在游戏里改了键位，这里自动跟着变；配置里那两张表只是可选的手工补充。
        /// </summary>
        internal static void LoadGameBindings()
        {
            try
            {
                GameBindings bindings = LazyInput.GameBindings;
                if (bindings == null) return;      // 还没准备好，交给外面重试

                int keyCount = 0;
                int padCount = 0;

                List<KeyBinding> kbList = bindings.keyBindings;
                if (kbList != null)
                {
                    foreach (KeyBinding kb in kbList)
                    {
                        if (kb == null) continue;
                        int id = KeyId(kb.gameKey);
                        if (id < 0) continue;

                        // ★ 只收功能键。同一个物理键可能同时绑着别的 GameKey
                        //   （实测：Q 既绑 QuestTree 又绑 ItemCountWindow_Cancel），
                        //   全收进来的话，后写入的会把功能键覆盖掉 —— 那正是
                        //   「按 Q 不认、按 6 不认」的原因。
                        if (!IsSpecialKeyId(id)) continue;

                        if (kb.keyCode != KeyCode.None)
                        {
                            KeyCodeToGameKey[kb.keyCode] = id;
                            keyCount++;
                        }
                        if (kb.additionalKeyCodes != null)
                        {
                            foreach (KeyCode extra in kb.additionalKeyCodes)
                            {
                                if (extra == KeyCode.None) continue;
                                KeyCodeToGameKey[extra] = id;
                                keyCount++;
                            }
                        }
                    }
                }

                List<GamepadBinding> padList = bindings.gamepadBindings;
                if (padList != null)
                {
                    foreach (GamepadBinding gb in padList)
                    {
                        if (gb == null) continue;
                        int id = KeyId(gb.gameKey);
                        if (id < 0) continue;

                        // ★ 同上：只收功能键。实测 CheatButton 也绑在 Back 键上，
                        //   全收会把 CharacterWindow 覆盖掉。
                        if (!IsSpecialKeyId(id)) continue;

                        string buttonName = WrapperName(gb.gamepadButton, typeof(GamepadButton));
                        int joy = JoystickIndexByName(buttonName);
                        if (joy < 0) continue;

                        RawButtonToGameKey[joy] = id;
                        padCount++;
                    }
                }

                BindingsLoaded = true;
                Log($"[map] 已读游戏绑定表 / loaded from game bindings: 键盘 {keyCount} 条, 手柄 {padCount} 条");

                // 诊断：把功能键的真实绑定打出来（手柄那列中间是 GamepadButton 的名字，
                // 最后是我换算出的物理编号，-1 表示没换算出来）。
                Log("[map] 键盘功能键 / keyboard special bindings: " + KeyBindingsText(bindings));
                Log("[map] 手柄功能键 / gamepad special bindings: " + PadBindingsText(bindings));
                Log("[map] 当前内存表 / current map: 手柄 " + GamepadMapText() + " | 键盘 " + KeyboardKeyMapText());
            }
            catch (Exception ex)
            {
                // 插件 Awake 时游戏还没准备好（GameBindings 的 getter 会现场构造
                // GamepadController），所以失败是正常的 —— 只报一次，之后靠外面重试。
                if (!_bindingsErrorLogged)
                {
                    _bindingsErrorLogged = true;
                    LogError("[map] 读绑定表失败（会继续重试）/ load bindings failed, will retry: " + ex.Message);
                }
            }
        }

        /// <summary>按名字找 GameKey 的数值（"CharacterWindow" 等），找不到返回 -1。</summary>
        internal static int IdFromName(string name)
        {
            try
            {
                foreach (FieldInfo f in typeof(GameKey).GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(GameKey)) continue;
                    if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    object v = f.GetValue(null);
                    if (v == null) continue;
                    return KeyId((GameKey)v);
                }
            }
            catch { }
            return -1;
        }

        /// <summary>启动时把配置里的两张映射表（手柄 + 键盘）读进内存。</summary>
        internal static void LoadMap()
        {
            try
            {
                // ★ 这里【不要】Clear()：两张表的主体来自游戏绑定表（LoadGameBindings），
                //   本方法只是在它上面叠配置里那几条可选覆盖。
                //   曾经因为先 Clear 再叠空的配置，把刚读到的绑定表整个清空，
                //   现象就是「日志里表是好的，运行时 TryGetValue 却取不到」。

                string pad = (GamepadMap != null) ? GamepadMap.Value : null;
                if (!string.IsNullOrEmpty(pad))
                {
                    foreach (string part in pad.Split(','))
                    {
                        string s = part.Trim();
                        if (s.Length == 0) continue;

                        string[] kv = s.Split('=');
                        if (kv.Length != 2) continue;

                        int button;
                        if (!int.TryParse(kv[0].Trim(), out button)) continue;

                        int id = IdFromName(kv[1].Trim());
                        if (id < 0)
                        {
                            Log($"[map] 手柄表里的 GameKey 名不认识，已跳过 / unknown name, skipped: {kv[1].Trim()}");
                            continue;
                        }
                        RawButtonToGameKey[button] = id;
                    }
                }

                string keys = (KeyboardKeyMap != null) ? KeyboardKeyMap.Value : null;
                if (!string.IsNullOrEmpty(keys))
                {
                    foreach (string part in keys.Split(','))
                    {
                        string s = part.Trim();
                        if (s.Length == 0) continue;

                        string[] kv = s.Split('=');
                        if (kv.Length != 2) continue;

                        KeyCode kc;
                        if (!TryParseKeyCode(kv[0].Trim(), out kc))
                        {
                            Log($"[map] 键盘表里的键名不认识，已跳过 / unknown key, skipped: {kv[0].Trim()}");
                            continue;
                        }

                        int id = IdFromName(kv[1].Trim());
                        if (id < 0)
                        {
                            Log($"[map] 键盘表里的 GameKey 名不认识，已跳过 / unknown name, skipped: {kv[1].Trim()}");
                            continue;
                        }
                        KeyCodeToGameKey[kc] = id;
                    }
                }

                Log("[map] 已从配置载入 / loaded from config: 手柄 "
                    + (GamepadMapText().Length == 0 ? "(空)" : GamepadMapText())
                    + " | 键盘 "
                    + (KeyboardKeyMapText().Length == 0 ? "(空)" : KeyboardKeyMapText()));
            }
            catch (Exception ex)
            {
                LogError("[map] 读配置失败 / load map failed: " + ex.Message);
            }
        }

        private static bool TryParseKeyCode(string name, out KeyCode code)
        {
            code = KeyCode.None;
            try
            {
                KeyCode parsed = (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
                if (!Enum.IsDefined(typeof(KeyCode), parsed)) return false;
                code = parsed;
                return true;
            }
            catch { return false; }
        }

        /// <summary>把内存里的两张映射表写回配置，下次启动直接生效。</summary>
        private static void SaveMap()
        {
            try
            {
                if (Instance == null) return;
                bool changed = false;

                if (GamepadMap != null)
                {
                    string text = GamepadMapText();
                    if (GamepadMap.Value != text) { GamepadMap.Value = text; changed = true; }
                }
                if (KeyboardKeyMap != null)
                {
                    string text = KeyboardKeyMapText();
                    if (KeyboardKeyMap.Value != text) { KeyboardKeyMap.Value = text; changed = true; }
                }
                if (!changed) return;                          // 没变化就不写盘

                Instance.Config.Save();
                Log("[map] 已写回配置 / saved map to config: 手柄 " + GamepadMapText()
                    + " | 键盘 " + KeyboardKeyMapText());
            }
            catch (Exception ex)
            {
                LogError("[map] 写配置失败 / save map failed: " + ex.Message);
            }
        }

        /// <summary>诊断：功能键各自绑在哪个键盘键上。</summary>
        private static string KeyBindingsText(GameBindings bindings)
        {
            try
            {
                if (bindings == null || bindings.keyBindings == null) return "(无)";
                List<string> parts = new List<string>();
                foreach (KeyBinding kb in bindings.keyBindings)
                {
                    if (kb == null) continue;
                    int id = KeyId(kb.gameKey);
                    if (!IsSpecialKeyId(id)) continue;
                    parts.Add(IdName(id) + "=" + kb.keyCode);
                }
                return parts.Count == 0 ? "(功能键没有键盘绑定)" : string.Join(", ", parts.ToArray());
            }
            catch (Exception ex) { return "(异常 " + ex.Message + ")"; }
        }

        /// <summary>诊断：功能键各自绑在哪个手柄按键上，以及我换算出的物理编号。</summary>
        private static string PadBindingsText(GameBindings bindings)
        {
            try
            {
                if (bindings == null || bindings.gamepadBindings == null) return "(无)";
                List<string> parts = new List<string>();
                foreach (GamepadBinding gb in bindings.gamepadBindings)
                {
                    if (gb == null) continue;
                    int id = KeyId(gb.gameKey);
                    if (!IsSpecialKeyId(id)) continue;
                    string name = WrapperName(gb.gamepadButton, typeof(GamepadButton));
                    parts.Add(IdName(id) + "=" + (name ?? "?")
                              + "/raw" + WrapperValue(gb.gamepadButton)
                              + "->idx" + JoystickIndexByName(name));
                }
                return parts.Count == 0 ? "(功能键没有手柄绑定)" : string.Join(", ", parts.ToArray());
            }
            catch (Exception ex) { return "(异常 " + ex.Message + ")"; }
        }

        internal static string GamepadMapText()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, int> kv in RawButtonToGameKey)
            {
                parts.Add(kv.Key + "=" + IdName(kv.Value));
            }
            return string.Join(",", parts.ToArray());
        }

        internal static string KeyboardKeyMapText()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<KeyCode, int> kv in KeyCodeToGameKey)
            {
                parts.Add(kv.Key + "=" + IdName(kv.Value));
            }
            return string.Join(",", parts.ToArray());
        }

        /// <summary>按类名找类型（先 AccessTools，再遍历已加载程序集），用于 internal 类型。</summary>
        internal static Type FindType(string name)
        {
            try
            {
                Type t = AccessTools.TypeByName(name);
                if (t != null) return t;
            }
            catch { }

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type x in asm.GetTypes())
                    {
                        if (x.Name == name) return x;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>在已加载程序集里找「指定名字 + 指定参数」的方法（目标类型可能是 internal）。</summary>
        internal static MethodInfo FindMethodWithParams(string name, params Type[] parameters)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    try
                    {
                        MethodInfo m = t.GetMethod(name,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, parameters, null);
                        if (m != null) return m;
                    }
                    catch { }
                }
            }
            return null;
        }

        internal static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(obj, null);
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? f.GetValue(obj) : null;
            }
            catch { return null; }
        }

        internal static bool TakeOverRequested;
        internal static float LastStopTime = -10f;   // 上次收手的时间，防止刚停就被立刻接管
        internal static bool NoTargetNow;            // 当前手上没有可工作目标（转化间隙）

        /// <summary>
        /// 按键当下直接接管（由 Patch_Interact/Interact2 调用，先于 Behaviour.Update）。
        /// 只负责打开开关让 GetKey(Action) 立刻返回 true；目标的锁定、锚点、
        /// 计时等初始化由 Behaviour 在下一帧看到 TakeOverRequested 时补做。
        /// </summary>
        internal static void RequestTakeOver()
        {
            if (WorkActive) return;

            WorkActive = true;
            TriggerGameKeyId = -1;
            SuppressWhileInWork = 0f;
            ForceReleaseUntil = 0f;
            TakeOverRequested = true;
            Log("[auto-work ON] 按键直接接管 / takeover on key press");
        }

        internal static void BeginWork(int triggerGameKeyId)
        {
            WorkActive = true;
            TriggerGameKeyId = triggerGameKeyId;
            SuppressWhileInWork = 0f;
            ResShortageTime = 0f;        // 上一次的「资源不足」不带进这一次
            UiTookControlTime = 0f;      // 上一次的「界面接管」也不带进来
            Log($"[auto-work ON] 触发键 gameKey={triggerGameKeyId}");
        }

        internal static void EndWork(string reason) => EndWork(reason, forceRelease: true);

        /// <summary>
        /// forceRelease = true   强制让游戏看到「工作键已松开」（默认，自己收手时用）
        /// forceRelease = false  「界面接管 / 菜单键」这类不要用：
        ///                       它会把之后 0.3 秒内的 Action 键一起压掉，
        ///                       表现就是"停完之后有一段时间按 X 没反应"。
        ///                       这种收手是主动 StopInteraction 退状态的，本来就立刻生效。
        /// </summary>
        internal static void EndWork(string reason, bool forceRelease)
        {
            if (!WorkActive) return;
            WorkActive = false;
            TriggerGameKeyId = -1;
            LastStopTime = Time.time;
            NoTargetNow = false;
            // 只需要让游戏看到「键已松开」一瞬即可退出工作状态。
            ForceReleaseUntil = forceRelease ? Time.time + 0.3f : 0f;
            Log($"[auto-work OFF] {reason}");
        }

        /// <summary>主动退出工作状态，避免玩家被锁住。</summary>
        internal static void ForceStopInteraction()
        {
            try
            {
                PlayerController pc = MainGame.PlayerController;
                if (pc == null) return;
                object comp = ReadMember(pc, "PlayerWorkComponent");
                if (comp == null) return;
                MethodInfo mi = comp.GetType().GetMethod("StopInteraction",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                mi?.Invoke(comp, null);
            }
            catch { }
        }
    }

    /// <summary>
    /// 判定「当前交互是不是放行名单里的动作」。
    /// 做法：读游戏自己生成的交互提示文本，比对本地化后的 hint 文本（兜底用）。
    /// </summary>
    internal static class HintHelper
    {
        /// <summary>要持续化的动作（提示文本键）。加一项即可扩展。</summary>
        private static readonly string[] AllowedKeys =
        {
            "hint_work",        // 工作：砍树 / 挖矿 / 挖黏土沙 / 耕地 / 收割 / 工地施工
            "hint_draw_water",  // 打水
            "hint_fertilize",   // 施肥
            "hint_plant",       // 种植
            "hint_grave",       // 坟墓
            "hint_build",       // 建造（打开建筑窗口，只触发一次）
            "hint_craft"        // 制作
        };

        /// <summary>只打开界面、不能重复触发的动作（重复调用会反复弹窗）。</summary>
        private static readonly string[] UiOnlyKeys =
        {
            "hint_build"
        };

        private static readonly List<string> _allowedTexts = new List<string>();
        private static readonly List<string> _uiOnlyTexts = new List<string>();
        private static bool _ready;

        internal static int LoadedHintCount => _allowedTexts.Count;

        internal static string KeyList => string.Join(", ", AllowedKeys);

        private static void EnsureHints()
        {
            if (_ready) return;
            try
            {
                // 用游戏自己的本地化接口取文本（含 <nobr> 等标签，绝不硬编码中文字符串）
                foreach (string key in AllowedKeys)
                {
                    string text = LLBase.L(key);
                    if (!string.IsNullOrEmpty(text) && !_allowedTexts.Contains(text))
                    {
                        _allowedTexts.Add(text);
                    }
                }
                foreach (string key in UiOnlyKeys)
                {
                    string text = LLBase.L(key);
                    if (!string.IsNullOrEmpty(text) && !_uiOnlyTexts.Contains(text))
                    {
                        _uiOnlyTexts.Add(text);
                    }
                }
                if (_allowedTexts.Count > 0) _ready = true;
            }
            catch { }
        }

        /// <summary>
        /// 读取该 handler 当前的全部提示文本。
        /// 返回 false 表示「读不到」（反射失败），和「读到了但是空」是两回事——
        /// 后者常见于制作台类：CraftInteractionHandler 没有覆写 FormInteractionInfo，
        /// 走基类时若目标没有事件/任务就返回空列表，但工作起来照样有进度条。
        /// </summary>
        private static bool TryGetHintTexts(WGOInteractionHandlerBase handler, out List<string> texts)
        {
            texts = new List<string>();
            if (handler == null) return false;
            try
            {
                object infos = InvokeGetInteractionInfos(handler);
                if (infos == null) return false;

                FieldInfo listField = infos.GetType().GetField("list",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IEnumerable list = listField != null ? listField.GetValue(infos) as IEnumerable : null;
                if (list == null) return false;

                foreach (object info in list)
                {
                    if (info == null) continue;
                    FieldInfo textField = info.GetType().GetField("text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    string text = textField != null ? textField.GetValue(info) as string : null;
                    if (!string.IsNullOrEmpty(text)) texts.Add(text);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>该 handler 当前的交互提示是否在放行名单内。</summary>
        internal static bool IsAllowedAction(WGOInteractionHandlerBase handler)
        {
            EnsureHints();
            if (!_ready) return false;

            List<string> texts;
            if (!TryGetHintTexts(handler, out texts)) return false;

            foreach (string text in texts)
            {
                foreach (string hint in _allowedTexts)
                {
                    if (text.Contains(hint)) return true;
                }
            }
            return false;
        }

        /// <summary>是不是「只开界面、不能重复触发」的动作（建造窗口）。</summary>
        internal static bool IsUiOnlyAction(WGOInteractionHandlerBase handler)
        {
            EnsureHints();
            if (!_ready) return false;

            List<string> texts;
            if (!TryGetHintTexts(handler, out texts)) return false;

            foreach (string text in texts)
            {
                foreach (string hint in _uiOnlyTexts)
                {
                    if (text.Contains(hint)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 放行判定：名单命中，或者「完全没有提示文本」。
        /// 后者覆盖制作台/工作站这类走基类 FormInteractionInfo 的目标——
        /// 它们没有事件、没有任务时提示列表为空，但干起来有进度条。
        /// 真正的开界面/拿取类都带自己的提示（hint_open / hint_put …），不会落到这一支。
        /// </summary>
        internal static bool IsAllowedOrSilent(WGOInteractionHandlerBase handler, out bool silent)
        {
            silent = false;
            if (IsAllowedAction(handler)) return true;

            EnsureHints();
            if (!_ready) return false;

            List<string> texts;
            if (!TryGetHintTexts(handler, out texts)) return false;
            if (texts.Count == 0)
            {
                silent = true;
                return true;
            }
            return false;
        }

        private static object InvokeGetInteractionInfos(WGOInteractionHandlerBase handler)
        {
            Type type = handler.GetType();

            MethodInfo mi = type.GetMethod("GetInteractionInfos",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null) return mi.Invoke(handler, null);

            // 显式接口实现的情况
            foreach (Type itf in type.GetInterfaces())
            {
                MethodInfo m = itf.GetMethod("GetInteractionInfos");
                if (m != null) return m.Invoke(handler, null);
            }
            return null;
        }
    }

    /// <summary>
    /// 游戏把控制权交给界面（打开菜单 / 窗口 / 进入剧情）时，会调用
    ///     PlayerInteractionComponent.SetPauseState(PlayerInteractionPauseType.ByControl, true)
    /// 实测日志确认，且干活时用的是 [ByWork]，两者可区分。
    ///
    /// 这是「按了菜单/暂停键」最可靠的判据：不依赖具体键码，
    /// 任何方式打开界面都会命中，也不受「工作期间游戏暂停了交互输入」的影响
    /// （之前用 LazyInput.GetKeyDown 读不到按键，就是这个原因）。
    ///
    /// 参数用 object 接收、按名字比较，避免依赖那个嵌套枚举的具体声明位置。
    /// </summary>
    internal static class PauseStateHook
    {
        internal static void Postfix(object type, bool isPaused)
        {
            try
            {
                if (!isPaused) return;
                if (type == null) return;
                if (type.ToString() != "ByControl") return;

                ToggleInteractPlugin.UiTookControlTime = Time.time;
                ToggleInteractPlugin.UiTookControlReason = "ByControl";
                ToggleInteractPlugin.Log("[ui-took-control] 界面接管了操作 / UI took control (ByControl)");
            }
            catch { }
        }
    }

    /// <summary>
    /// ★ 真正的接管入口：玩家进入「用工具干活」状态的那一刻。
    ///
    /// 之前挂在 WGOInteractionHandlerBase.Interact2 上，但实测纺车这类目标
    /// 根本不走 Interact2 —— 玩家按工作键后，是 SSM 看到
    ///     WorkPlayerState.CanEnter => IsActive && CanWork()
    /// 成立就直接进状态，压根不调用任何 handler 方法，
    /// 所以那条路径上一条日志都打不出来（现象就是「按一下挪一下」）。
    ///
    /// WorkPlayerState 是 internal 类，不能直接 typeof，用 AccessTools 反射挂。
    /// </summary>
    internal static class WorkStateHook
    {
        internal static void Postfix()
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null || !ToggleInteractPlugin.Enabled.Value) return;
                if (ToggleInteractPlugin.WorkActive) return;

                // 刚被玩家取消/刚收手，短时间内别立刻又接管回去
                // （0.2 秒够挡住"同一次按键"的重复接管，又不至于让重按的玩家等太久）
                if (Time.time - ToggleInteractPlugin.LastStopTime < 0.2f) return;

                ToggleInteractPlugin.RequestTakeOver();
                if (ToggleInteractPlugin.VerboseMode)
                {
                    ToggleInteractPlugin.Log("[work-action] WorkPlayerState.OnEnter -> 接管 / takeover");
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 玩家按下「干活」键（Action）时【同帧】接管。
    ///
    /// 为什么必须在这里接管、不能等 Behaviour.Update：
    ///     WorkPlayerState.IsActive => LazyInput.GetKey(GameKey.Action) || WorkIsTookControl
    ///   短按松开后两者都为 false，状态会在进入后立刻退出
    ///   （日志里的 EnterState: WorkPlayerState 紧接着 ExitState 就是这个）。
    ///   等下一帧 Update 时状态已经没了，也来不及钉住。
    ///   在这里同帧把 WorkActive 打开，Patch_GetKey 随即让 GetKey(Action) 返回 true，
    ///   工作状态就被钉住了，角色才能走到位、进度条才能继续走。
    /// </summary>
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), "Interact2", new[] { typeof(PlayerController) })]
    internal static class Patch_Interact2
    {
        private static void Prefix(WGOInteractionHandlerBase __instance)
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null || !ToggleInteractPlugin.Enabled.Value) return;
                if (HintHelper.IsUiOnlyAction(__instance)) return;      // 开窗型：绝不重复触发

                bool silent;
                if (!HintHelper.IsAllowedOrSilent(__instance, out silent)) return;

                ToggleInteractPlugin.LastWorkActionTime = Time.time;
                if (!ToggleInteractPlugin.WorkActive)
                {
                    ToggleInteractPlugin.RequestTakeOver();
                    if (ToggleInteractPlugin.VerboseMode)
                    {
                        ToggleInteractPlugin.Log(
                            $"[work-action] {__instance.GetType().Name} via Interact2 " +
                            (silent ? "(无提示文本 / no hint text)" : "(名单命中 / hint matched)"));
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Interaction 键上的干活目标（坟地等）。这里只认名单，
    /// 不放开「无提示文本」这一支，避免把开门/拿取之类的空提示交互也接管。
    /// </summary>
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), "Interact", new[] { typeof(PlayerController) })]
    internal static class Patch_Interact
    {
        private static void Prefix(WGOInteractionHandlerBase __instance)
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null || !ToggleInteractPlugin.Enabled.Value) return;
                if (HintHelper.IsUiOnlyAction(__instance)) return;
                if (!HintHelper.IsAllowedAction(__instance)) return;

                ToggleInteractPlugin.LastWorkActionTime = Time.time;
                if (!ToggleInteractPlugin.WorkActive)
                {
                    ToggleInteractPlugin.RequestTakeOver();
                    if (ToggleInteractPlugin.VerboseMode)
                    {
                        ToggleInteractPlugin.Log($"[work-action] {__instance.GetType().Name} via Interact");
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>记录玩家按下的 GameKey，用于「同键取消」。</summary>
    [HarmonyPatch(typeof(LazyInput), "GetKeyDown", new[] { typeof(GameKey) })]
    internal static class Patch_KeyDown
    {
        private static void Postfix(GameKey key, ref bool __result)
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null) return;
                if (!ToggleInteractPlugin.Enabled.Value) return;

                // 待补还的交互键超时了就放弃，别在几百毫秒后突然凭空触发一次交互
                if (ToggleInteractPlugin.ReplayInteractionPending
                    && Time.time - ToggleInteractPlugin.ReplayInteractionAt > 0.5f)
                {
                    ToggleInteractPlugin.ReplayInteractionPending = false;
                }


                // 把「按 A 收手」时被吃掉的那一次交互键补还给游戏。
                // 等到状态已退出（ReplayInteractionAt 之后）再放行，游戏才会真的处理它。
                if (!__result
                    && ToggleInteractPlugin.ReplayInteractionPending
                    && ToggleInteractPlugin.InteractionKeyId >= 0
                    && ToggleInteractPlugin.KeyId(key) == ToggleInteractPlugin.InteractionKeyId
                    && Time.time >= ToggleInteractPlugin.ReplayInteractionAt)
                {
                    __result = true;
                    ToggleInteractPlugin.ReplayInteractionPending = false;
                    return;
                }

                // 把「功能键」补还给游戏（暂停 / 角色 / 科技树 / 地图 …）——
                // 与上面补还交互键同一招，只是补的是别的 GameKey。
                // 收手后玩家立刻回到 FreePlayerState，UpdateInput() 就会跑起来，
                // 我们只要让这一次 GetKeyDown 返回 true，游戏就按它自己的逻辑处理这次按键。
                if (ToggleInteractPlugin.ReplayKeyId >= 0
                    && Time.time > ToggleInteractPlugin.ReplayKeyAt + 0.5f)
                {
                    ToggleInteractPlugin.ReplayKeyId = -1;      // 一直没人查就算了
                }
                if (!__result
                    && ToggleInteractPlugin.ReplayKeyId >= 0
                    && ToggleInteractPlugin.KeyId(key) == ToggleInteractPlugin.ReplayKeyId
                    && Time.time >= ToggleInteractPlugin.ReplayKeyAt)
                {
                    __result = true;
                    ToggleInteractPlugin.ReplayKeyId = -1;
                    return;
                }

                // 顺手学习「输入 -> GameKey」（手柄按钮 / 键盘键，只在正常游玩时学得到）
                if (__result) ToggleInteractPlugin.LearnMapping(key);

                if (!__result) return;
                if (!ToggleInteractPlugin.WorkActive) return;

                // 正在持续工作时，按回开启时那个键 -> 取消
                if (ToggleInteractPlugin.TriggerGameKeyId >= 0
                    && ToggleInteractPlugin.KeyId(key) == ToggleInteractPlugin.TriggerGameKeyId)
                {
                    ToggleInteractPlugin.ForceStopInteraction();
                    ToggleInteractPlugin.EndWork("同键取消 / same key cancelled");
                }
            }
            catch { }
        }
    }

    /// <summary>维持工作：把 GameKey.Action 伪装成一直按住。</summary>
    [HarmonyPatch(typeof(LazyInput), "GetKey", new[] { typeof(GameKey) })]
    internal static class Patch_GetKey
    {
        private static void Postfix(GameKey key, ref bool __result)
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null || !ToggleInteractPlugin.Enabled.Value) return;

                // 游戏绑定表在插件 Awake 时还没准备好（LazyInput.GameBindings 的 getter
                // 会现场构造 GamepadController，太早调用必然 NRE），所以在这里持续重试，
                // 读到之后再把配置里那两张可选覆盖表重新叠上去。
                if (!ToggleInteractPlugin.BindingsLoaded
                    && Time.time >= ToggleInteractPlugin.NextBindingsRetry)
                {
                    ToggleInteractPlugin.NextBindingsRetry = Time.time + 1f;
                    ToggleInteractPlugin.LoadGameBindings();
                    if (ToggleInteractPlugin.BindingsLoaded) ToggleInteractPlugin.LoadMap();
                }

                if (ToggleInteractPlugin.KeyId(key) != ToggleInteractPlugin.ActionKeyId) return;

                if (Time.time < ToggleInteractPlugin.ForceReleaseUntil)
                {
                    __result = false;    // 停止后强制「松开」，让游戏立刻退出工作状态
                    return;
                }

                if (ToggleInteractPlugin.WorkActive)
                {
                    if (ToggleInteractPlugin.NoTargetNow)
                    {
                        // 待命期（树倒下、等树桩这类间隙）。这里有个陷阱：
                        //   游戏只在【进入工作状态】时才会 FindWgoToWork 找目标
                        //   （WorkPlayerState.OnEnter 里那一句）。如果一直松手，
                        //   状态退出后就再也没人去找树桩了 —— 会死锁，永远等不到。
                        // 所以做成脉冲：大部分时间松手（玩家能动、不卡手），
                        // 每隔一小段按住几帧，借游戏自己的 OnEnter 去发现下一形态。
                        // 找到了 Wgo 就恢复常按，衔接完成；找不到就继续脉冲等超时。
                        float phase = Time.time % 0.36f;
                        __result = phase < 0.06f;
                        return;
                    }

                    __result = true;     // 维持工作
                }
            }
            catch { }
        }
    }

    /// <summary>主管：检测工作状态、接管、判定停止、原生输入的同键取消。</summary>
    internal class InteractBehaviour : MonoBehaviour
    {
        private static readonly KeyCode[] Watch =
        {
            KeyCode.F, KeyCode.E, KeyCode.K, KeyCode.Q, KeyCode.R, KeyCode.G, KeyCode.T,
            KeyCode.C, KeyCode.Space, KeyCode.LeftShift, KeyCode.Tab, KeyCode.Return,
            KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2,
            KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9
        };

        private int _lastJoy = -1;
        private float _lastJoyTime;
        private int _triggerJoy = -1;
        private KeyCode _lastKey = KeyCode.None;
        private float _lastKeyTime;
        private KeyCode _triggerKey = KeyCode.None;

        private float _idleSince;
        private float _noWorkSince;
        private object _lockedTarget;
        private string _lockedId;
        private Vector3 _anchorPos;
        private bool _hasAnchor;
        private bool _hasWorked;      // 是否真正开工过（进度条走过），用于放行「走过去」的寻路阶段
        private float _hpZeroSince;   // 目标 HP 归零的时刻（用于判断「这一次的活干完了」）
        private float _nextProbe;
        private int _errorBudget = 5;   // 异常日志限流（带上堆栈，只报前几次）

        private void Update()
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null) return;
                if (ToggleInteractPlugin.Enabled == null || !ToggleInteractPlugin.Enabled.Value) return;

                ScanNativeInput();

                // ---------- 0. 补做「按键直接接管」的初始化 ----------
                if (ToggleInteractPlugin.WorkActive && ToggleInteractPlugin.TakeOverRequested)
                {
                    ToggleInteractPlugin.TakeOverRequested = false;
                    CaptureTriggerInputs();
                    _lockedTarget = GetWorkTarget();
                    _lockedId = GetIdOf(_lockedTarget);
                    SetAnchor(_lockedTarget);
                    _idleSince = 0f;
                    _noWorkSince = 0f;
                    _hasWorked = false;
                }

                // ---------- 1. 同键取消（原生输入，工作期间游戏输入被暂停时也能用）----------
                if (ToggleInteractPlugin.WorkActive)
                {
                    if (_triggerJoy >= 0 &&
                        UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + _triggerJoy)))
                    {
                        CancelBySameKey("手柄同键 / same gamepad button");
                        return;
                    }
                    if (_triggerKey != KeyCode.None && UnityEngine.Input.GetKeyDown(_triggerKey))
                    {
                        CancelBySameKey("键盘同键 / same key");
                        return;
                    }
                }

                // ---------- 1.5 交互键 -> 收手 ----------
                // 坟墓移除墓碑这类操作做完后：目标（grave_ground）还在、wip 也一直接着为 true
                // （我们按着键，游戏就一直让你挥铲），没有任何「这个动作做完了」的信号可读，
                // 所以没法自动识别结束。但只要你按下交互键（想重新选操作），就立刻松手，
                // 不再让输入被 ByWork 锁住 —— 例如移完墓碑接着按 A 移栅栏。
                if (ToggleInteractPlugin.WorkActive)
                {
                    try
                    {
                        if (LazyInput.GetKeyDown(GameKey.Interaction))
                        {
                            // 停止并退出工作状态后，把这一次交互键补还给游戏。
                            // 否则它会白按一下：游戏在工作状态里把交互暂停了
                            // （SetPauseState(ByWork, true)），那帧的按键收不到，
                            // 而退出状态又要等到下一帧 —— 表现为「按 A 被吞一次」。
                            ToggleInteractPlugin.ReplayInteractionPending = true;
                            ToggleInteractPlugin.ReplayInteractionAt = Time.time + 0.06f;
                            Stop("玩家按了交互键 / interaction key pressed");
                            return;
                        }
                    }
                    catch { }
                }

                // ---------- 1.6 菜单 / 暂停 / 角色键 ----------
                // ★ 为什么这些界面不会自己开（反编译确认）：
                //   读按键的 PlayerInputHandler.UpdateInput() 只在该玩家状态的 IsActive
                //   （=> playerController.IsControlsEnabled）为真时被调用（唯一调用点 77187）。
                //   干活时玩家在 WorkPlayerState，IsControlsEnabled 是 false（ByWork 暂停），
                //   所以 UpdateInput() 整段不执行 —— 里面 74331 行起那一整串按键处理都跑不到。
                //   （这也是为什么 ByControl 不出现：ByWork 已经先拿走控制权了。）
                //
                //   LazyInput 自己每帧照样把按键记进 pressedKeys（LBT 24918），与玩家状态无关，
                //   所以 GetKeyDown 读得到 —— 被拦住的只是「玩家的按键处理」那一段，
                //   因此可以按 GameKey 精确区分各个键。
                //
                // ★ 这里【只收手，不替游戏做任何事】：
                //   收手后玩家立刻退出 WorkPlayerState、控制权恢复，
                //   游戏自己的按键处理（UpdateInput，源码 74331~74375 行）当帧就会正常执行 ——
                //   该开暂停界面就开暂停界面，该开角色/科技树/地图就开它们，全由游戏决定。
                //
                //   实测依据：v1.0.3 日志里出现过【两套】ByControl + Set systems pause state to True，
                //   而当时 mod 只开了一个窗口 —— 另一个就是游戏自己开的，
                //   说明收手之后游戏确实能收到这一次按键。
                //
                //   反过来「由 mod 照源码逐条代开界面」是错的方向：
                //   既得猜每个键该开什么，又把游戏行为复制了一遍容易走偏
                //   （曾经按角色键弹出暂停界面）。功能键归游戏，mod 只管收手。
                // ★ 这里【只收手，并把这次按键递还给游戏】：
                //   收手后玩家立刻退出 WorkPlayerState、控制权恢复，UpdateInput() 随之跑起来。
                //   我们再把那次 GameKey 伪造回去（GetKeyDown 返回一次 true），
                //   游戏就按它自己的逻辑处理 —— 开什么界面、页签解没解锁，全由游戏决定，
                //   mod 不再复制任何界面逻辑。
                //
                //   注意：干活期间输入层不采集按键（实测按钮 6 连 CharacterWindow /
                //   Inventory 都读不到，即 GetKeyDown 对所有键都是 false），
                //   所以只能靠原生输入认出「原始手柄按钮」，再用正常游玩时学到的
                //   对应关系（RawButtonToGameKey）把它翻成 GameKey。
                if (ToggleInteractPlugin.WorkActive)
                {
                    // ① 先按 GameKey 直接认（正常游玩时读得到）
                    foreach (GameKey special in ToggleInteractPlugin.SpecialKeys)
                    {
                        if (!LazyInput.GetKeyDown(special)) continue;
                        ToggleInteractPlugin.ReplayKey(ToggleInteractPlugin.KeyId(special));
                        StopForUi($"玩家按了功能键 / function key pressed ({ToggleInteractPlugin.KeyName(special)})");
                        return;
                    }

                    // ② 原生输入兜底。干活期间游戏不采集按键（GetKeyDown 对所有 GameKey
                    //    都是 false），所以只能自己看物理输入 —— 但「哪个键 = 哪个 GameKey」
                    //    全部来自游戏自己的绑定表（LoadGameBindings），没有任何写死键位。
                    foreach (KeyValuePair<KeyCode, int> kb in ToggleInteractPlugin.KeyCodeToGameKey)
                    {
                        if (!ToggleInteractPlugin.IsSpecialKeyId(kb.Value)) continue;
                        if (!UnityEngine.Input.GetKeyDown(kb.Key)) continue;
                        ToggleInteractPlugin.ReplayKey(kb.Value);
                        StopForUi($"玩家按了功能键 / function key pressed (键盘 {kb.Key} -> {ToggleInteractPlugin.IdName(kb.Value)})");
                        return;
                    }

                    int raw = ToggleInteractPlugin.RawButtonDown();
                    if (raw >= 0)
                    {
                        int mapped;
                        if (ToggleInteractPlugin.RawButtonToGameKey.TryGetValue(raw, out mapped)
                            && ToggleInteractPlugin.IsSpecialKeyId(mapped))
                        {
                            ToggleInteractPlugin.ReplayKey(mapped);
                            StopForUi($"玩家按了功能键 / function key pressed (手柄按钮 {raw} -> {ToggleInteractPlugin.IdName(mapped)})");
                        }
                        else
                        {
                            // 这个按钮在游戏的绑定表里不是功能键：只收手，不开任何界面
                            StopForUi($"玩家按了手柄按钮 {raw}（非功能键）/ gamepad button {raw} (not a function key)");
                        }
                        return;
                    }
                }

                // ---------- 2. 玩家状态 ----------
                PlayerController pc = SafeController();
                object cur = null;
                try { cur = pc != null ? pc.Ssm.CurState : null; } catch { }
                string stateName = cur != null ? cur.GetType().Name : string.Empty;
                bool inWork = stateName.IndexOf("Work", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!inWork)
                {
                    ToggleInteractPlugin.SuppressWhileInWork = 0f;
                }

                // ---------- 3. 接管：进入了工作状态，且「进度条正在走」 ----------
                if (!ToggleInteractPlugin.WorkActive && inWork)
                {
                    if (ToggleInteractPlugin.SuppressWhileInWork > 0f) return;

                    object workComp0 = GetWorkComponent();
                    object wip0 = workComp0 != null ? ToggleInteractPlugin.ReadMember(workComp0, "WorkInProgress") : null;
                    bool progressRunning = wip0 is bool w0 && w0;                                    // 进度条在走
                    bool recentHint = Time.time - ToggleInteractPlugin.LastWorkActionTime <= 0.8f;   // 兜底

                    if (!progressRunning && !recentHint) return;

                    CaptureTriggerInputs();
                    ToggleInteractPlugin.BeginWork(ToggleInteractPlugin.TriggerGameKeyId);

                    _lockedTarget = GetWorkTarget();
                    _lockedId = GetIdOf(_lockedTarget);
                    SetAnchor(_lockedTarget);
                    _idleSince = 0f;
                    _noWorkSince = 0f;
                    _hasWorked = false;     // 先允许角色自动走到工作点，不因移动而中断
                    _progressKey = null;    // 停滞计时重新开始
                    _progressSince = 0f;
                    _movedSinceProgress = 0f;
                    _hasPos = false;
                    if (ToggleInteractPlugin.VerboseMode)
                    {
                        ToggleInteractPlugin.Log($"[take-over] progress={progressRunning} hint={recentHint} anchor={PosText(_anchorPos)}");
                    }
                    return;
                }

                if (!ToggleInteractPlugin.WorkActive) return;

                // ---------- 4. 停止判定 ----------
                object work = GetWorkComponent();
                object target = work != null ? ToggleInteractPlugin.ReadMember(work, "Wgo") : null;
                object wip = work != null ? ToggleInteractPlugin.ReadMember(work, "WorkInProgress") : null;
                bool working = wip is bool w && w;
                bool hasTarget = !IsUnityNull(target);

                // 手上没目标时松开按键：转化间隙（树倒下等树桩、矿脉换形态）里
                // 让玩家能自由行动，同时保持在待命状态，新形态出现即自动接着干。
                ToggleInteractPlugin.NoTargetNow = !hasTarget;

                if (working) _hasWorked = true;

                // 4.0 玩家想移动 -> 收手
                //     · 已开工：干活中途想走（坟地/建造这类目标长期存在的交互靠这条脱身）
                //     · 待命期（目标消失、等下一形态）：树倒下等树桩时按键是松开的、人本来就能动，
                //       这时只要一走动，就说明不想继续了；不加这条的话，
                //       等树桩一出现又会被拉回去接着挖。
                //     唯独「还没开工的走位阶段」不判定——原版对着需要站到特定位置的工作台
                //     按一下会自动走过去（TryStartInteraction -> StartMovement），
                //     那段路上一开始就判停会变成「按一下挪一步」。
                if (_hasWorked || ToggleInteractPlugin.NoTargetNow)
                {
                    Vector2 moveDir;
                    if (WantsToMove(out moveDir))
                    {
                        Stop($"玩家想移动 / player wants to move (dir {moveDir.x:F2},{moveDir.y:F2}, " +
                             $"standby={ToggleInteractPlugin.NoTargetNow})");
                        return;
                    }
                }

                if (hasTarget)
                {
                    _idleSince = 0f;

                    // 4.1 目标发生变化：判断是「同一格上的形态转化」还是「换到旁边的另一个资源」
                    string newId = GetIdOf(target);
                    if (newId != _lockedId)
                    {
                        Vector3 newPos = GetPositionOf(target);
                        float dist = _hasAnchor ? Vector3.Distance(newPos, _anchorPos) : 0f;
                        string oldFamily = FamilyOf(_lockedId);
                        string newFamily = FamilyOf(newId);

                        // 判定「同一资源点的形态转化」两条路取其一：
                        //   ① 位置几乎没动（同一格）——最干净的转化
                        //   ② 资源族名相同、且没有跑远 —— 树 -> 树桩这种会挪一点点
                        //      （实测偏移可达 1.4，纯距离判据会把正常转化也挡掉）
                        // 而旁边那棵小树族名是 fir_clr1_xs，与原树 fir_clr1_l 不同，天然被排除。
                        bool sameTile = !_hasAnchor
                                        || dist <= 0.15f
                                        || (!string.IsNullOrEmpty(oldFamily)
                                            && oldFamily == newFamily
                                            && dist <= ToggleInteractPlugin.Radius);

                        if (!sameTile)
                        {
                            if (_hasWorked)
                            {
                                Stop($"转化结束 / left the tile ({_lockedId} -> {newId}, " +
                                     $"dist {dist:F2} > {ToggleInteractPlugin.Radius:F2}, " +
                                     $"family '{oldFamily}' -> '{newFamily}')");
                                return;
                            }

                            // 还没真正开工：这是「走向工作点」的过程中游戏重新磁吸了目标
                            // （TryStartInteraction 每帧都会 FindWgoToWork）。
                            // 此时不能停，否则表现为「按一下挪一步，还没走到就断了」。
                            if (ToggleInteractPlugin.VerboseMode)
                            {
                                ToggleInteractPlugin.Log(
                                    $"[re-target] 开工前目标变化，跟随 / follow before start " +
                                    $"({_lockedId} -> {newId}, dist {dist:F2})");
                            }
                        }

                        if (ToggleInteractPlugin.VerboseMode)
                        {
                            ToggleInteractPlugin.Log(
                                $"[transform] {_lockedId} -> {newId} 同格转化 / same tile " +
                                $"(dist {dist:F2}, family '{newFamily}') 继续");
                        }
                        _lockedTarget = target;
                        _lockedId = newId;
                        SetAnchor(target);      // 锚点跟随，支持多阶段转化
                        _hpZeroSince = 0f;      // 换了新形态，重新开始看它的进度
                    }

                    // 4.1.5 这一次的活干完了：进度条（HP）走完，而且目标没有换成新形态。
                    //       典型就是坟墓拆部件 —— 拆完墓碑后 grave_ground 还是它自己，
                    //       游戏不给任何「完成」信号，只能靠 HP 归零 + 目标未变来判断。
                    //       砍树/挖矿走到这一步时目标会换形态（ID 已变、计时已重置），
                    //       所以不会被误判成「结束」。
                    int hpNow, hpMax;
                    if (TryGetHp(target, out hpNow, out hpMax))
                    {
                        if (hpNow > 0)
                        {
                            _hpZeroSince = 0f;
                        }
                        else
                        {
                            // 等待窗口按目标类型分开给：
                            //   · 资源类（tree_ / stump_ / rock_ / ore_ / bush_ …）会变形态或消失，
                            //     被砍倒后对象还要躺 1~2 秒播倒下动画，窗口给足 2 秒，
                            //     否则会把「树 -> 树桩」误判成结束（v22 的回归）；
                            //   · 其他（坟墓拆部件这类）目标从头到尾都是它自己，拆完就该走人，
                            //     窗口压到 0.4 秒，自动收手才不拖沓。
                            // 两种情况只要目标真的变了/消失了，计时都会被取消，所以短窗口也安全。
                            float hpGrace = IsResourceLike(_lockedId) ? 2.0f : 0.4f;
                            if (_hpZeroSince <= 0f) _hpZeroSince = Time.time;
                            else if (Time.time - _hpZeroSince > hpGrace)
                            {
                                Stop($"进度条已完成 / progress finished (hp {hpNow}/{hpMax})");
                                return;
                            }
                        }
                    }
                    else
                    {
                        _hpZeroSince = 0f;
                    }
                }
                else
                {
                    // 4.2 目标被清掉（树倒下等树桩出现 / 矿脉换形态 / 真的挖空了）。
                    //     这段是待命：按键已松开（NoTargetNow），玩家能动，不会卡手；
                    //     新形态一出现 hasTarget 就会变 true，立刻恢复按住接着干。
                    //     只有等满 LostTargetGraceMs 仍无目标，才算真的结束。
                    float grace = ToggleInteractPlugin.LostTargetGraceSeconds;

                    if (_idleSince <= 0f) _idleSince = Time.time;
                    else if (Time.time - _idleSince > grace)
                    {
                        Stop($"目标已消失 / nothing left to work on " +
                             $"(waited {grace:F2}s, started={_hasWorked})");
                        return;
                    }
                }

                // 4.3 离开工作状态太久（被界面、剧情、控制锁定打断）
                if (inWork)
                {
                    _noWorkSince = 0f;
                }
                else
                {
                    if (_noWorkSince <= 0f) _noWorkSince = Time.time;
                    else if (Time.time - _noWorkSince > ToggleInteractPlugin.IdleSeconds)
                    {
                        Stop("已离开工作状态 / left work state");
                        return;
                    }
                }

                // 4.4 资源不足（体力 / 精神）—— 游戏自己抛的事件，最可靠
                if (ToggleInteractPlugin.ResShortageTime > 0f
                    && Time.time - ToggleInteractPlugin.ResShortageTime <= 1.0f)
                {
                    Stop($"资源不足 / not enough resources ({ToggleInteractPlugin.ResShortageId})");
                    return;
                }

                // 4.5 进度停滞（缺材料 / 燃料不足这类游戏不抛事件的情况）
                if (UpdateStall(working, hasTarget))
                {
                    Stop($"进度停滞 / progress stalled ({ToggleInteractPlugin.StallSeconds:F1}s)");
                    return;
                }

                // 4.6 工作台缺材料 —— 精确判定（就是工作台上需求物品左下角那个「叉」的状态）
                string craftStatus;
                if (TryGetCraftNotEnough(_lockedTarget, out craftStatus))
                {
                    Stop($"材料不足 / not enough materials (CraftStatus={craftStatus})");
                    return;
                }

                // 4.7 界面接管了操作（菜单 / 暂停 / 剧情窗口已经打开）
                //     完全被动收手：只停止伪造工作键，绝不碰游戏自己的状态 ——
                //     主动 StopInteraction、或强制压键，都会干扰界面刚打开的流程
                //     （实测表现为"持续化停了但界面没开"）。
                if (ToggleInteractPlugin.UiTookControlTime > 0f)
                {
                    string reason = ToggleInteractPlugin.UiTookControlReason;
                    ToggleInteractPlugin.UiTookControlTime = 0f;
                    StopForUi($"界面接管了操作 / UI took control ({reason})");
                    return;
                }

                Probe(working, hasTarget);
            }
            catch (Exception ex)
            {
                // 只报前几次，避免每帧刷屏；带上完整堆栈，便于定位是 1.006 里哪个 API 变了
                if (_errorBudget > 0)
                {
                    _errorBudget--;
                    ToggleInteractPlugin.LogError("behaviour error: " + ex);
                }
            }
        }

        // ---------------- 辅助 ----------------

        /// <summary>
        /// 玩家是否想移动。两条路都试：游戏自己的输入封装 + Unity 原生轴，
        /// 因为工作状态下游戏可能暂停部分输入通道，原生那路更不容易被挡住。
        /// </summary>
        private static bool WantsToMove(out Vector2 dir)
        {
            dir = Vector2.zero;

            try
            {
                dir = LazyInput.GetDirection();
                if (dir.sqrMagnitude > 0.09f) return true;   // 死区约 0.3
            }
            catch { }

            try
            {
                float h = UnityEngine.Input.GetAxisRaw("Horizontal");
                float v = UnityEngine.Input.GetAxisRaw("Vertical");
                if (Mathf.Abs(h) > 0.3f || Mathf.Abs(v) > 0.3f)
                {
                    dir = new Vector2(h, v);
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 读取目标的 HP（进度条数值）。字段名来自反编译确认：
        ///     hpComponent.hp / hpComponent.maxHpValue
        /// 「用工具削减目标」类的工作（砍树、挖矿、拆部件）进度条就是这个 HP。
        /// </summary>
        private static bool TryGetHp(object wgo, out int hp, out int max)
        {
            hp = -1;
            max = -1;
            try
            {
                if (IsUnityNull(wgo)) return false;

                object comp = ToggleInteractPlugin.ReadMember(wgo, "hpComponent");
                if (comp == null)
                {
                    object data = ToggleInteractPlugin.ReadMember(wgo, "Data");
                    if (data != null) comp = ToggleInteractPlugin.ReadMember(data, "hpComponent");
                }
                if (comp == null) return false;

                int maxV = ToInt(ToggleInteractPlugin.ReadMember(comp, "maxHpValue"));
                int hpV = ToInt(ToggleInteractPlugin.ReadMember(comp, "hp"));
                if (maxV <= 0 || hpV == int.MinValue) return false;

                max = maxV;
                hp = hpV;
                return true;
            }
            catch { return false; }
        }

        private static int ToInt(object o)
        {
            if (o is int i) return i;
            if (o is float f) return Mathf.RoundToInt(f);
            if (o is double d) return (int)Math.Round(d);
            return int.MinValue;
        }

        private void SetAnchor(object wgo)
        {
            if (IsUnityNull(wgo)) { _hasAnchor = false; return; }
            _anchorPos = GetPositionOf(wgo);
            _hasAnchor = true;
        }

        private static Vector3 GetPositionOf(object wgo)
        {
            try
            {
                UnityEngine.Component c = wgo as UnityEngine.Component;
                if (c != null) return c.transform.position;
            }
            catch { }
            return Vector3.zero;
        }

        private static string PosText(Vector3 v) => $"({v.x:F1},{v.z:F1})";

        private void ScanNativeInput()
        {
            for (int i = 0; i < 20; i++)
            {
                if (UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + i)))
                {
                    _lastJoy = i;
                    _lastJoyTime = Time.time;
                }
            }
            foreach (KeyCode kc in Watch)
            {
                if (UnityEngine.Input.GetKeyDown(kc))
                {
                    _lastKey = kc;
                    _lastKeyTime = Time.time;
                }
            }
        }

        // ---- 诊断用：把持续化期间按下的键各记一次 ----
        private static readonly HashSet<string> LoggedKeys = new HashSet<string>();

        private static readonly KeyCode[] DiagKeys =
        {
            KeyCode.Escape, KeyCode.Tab, KeyCode.BackQuote,
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6,
            KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
            KeyCode.I, KeyCode.J, KeyCode.M, KeyCode.P, KeyCode.O, KeyCode.U, KeyCode.H,
            KeyCode.Return, KeyCode.Space,
            KeyCode.LeftShift, KeyCode.LeftControl, KeyCode.LeftAlt,
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow
        };

        /// <summary>
        /// 持续化期间按下的键，每个只记一次。
        /// 目的是把「那个按键到底对应哪个 GameKey」找出来（现在已由游戏绑定表直接提供）。
        /// 确认完可以删掉这一段。
        /// </summary>
        private void LogOtherKeyOnce()
        {
            try
            {
                if (LoggedKeys.Count > 80) return;

                for (int i = 0; i < 20; i++)
                {
                    if (UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + i)))
                    {
                        string n = "JoystickButton" + i;
                        if (LoggedKeys.Add(n))
                        {
                            ToggleInteractPlugin.Log(
                                $"[key] 持续化期间按下手柄键 {n}" +
                                (i == 6 || i == 7 ? "  ← 菜单/返回键" : "（不是菜单键可忽略）"));
                        }
                    }
                }

                foreach (KeyCode kc in DiagKeys)
                {
                    if (UnityEngine.Input.GetKeyDown(kc) && LoggedKeys.Add(kc.ToString()))
                    {
                        ToggleInteractPlugin.Log($"[key] 持续化期间按下 {kc}");
                    }
                }
            }
            catch { }
        }

        private void CaptureTriggerInputs()
        {
            if (Time.time - _lastJoyTime < 0.5f) { _triggerJoy = _lastJoy; }
            else { _triggerJoy = -1; }

            if (Time.time - _lastKeyTime < 0.5f) { _triggerKey = _lastKey; }
            else { _triggerKey = KeyCode.None; }
        }

        private void CancelBySameKey(string reason)
        {
            ToggleInteractPlugin.ForceStopInteraction();
            ToggleInteractPlugin.EndWork(reason);
            ToggleInteractPlugin.SuppressWhileInWork = 1f;
            _triggerJoy = -1;
            _triggerKey = KeyCode.None;
            ToggleInteractPlugin.TakeOverRequested = false;
        }

        private void Stop(string reason) => Stop(reason, forceExit: true, forceRelease: true);

        private void Stop(string reason, bool forceExit)
            => Stop(reason, forceExit, forceRelease: true);

        /// <summary>
        /// forceExit   = true 主动调 StopInteraction() 立刻退出工作状态
        /// forceRelease= true 强制让游戏看到「工作键已松开」（会把之后 0.3 秒的
        ///                    Action 键一起压掉，所以"菜单键收手"这种要用 false，
        ///                    否则停完之后一段时间按 X 没反应）
        /// </summary>
        private void Stop(string reason, bool forceExit, bool forceRelease)
        {
            if (forceExit) ToggleInteractPlugin.ForceStopInteraction();
            ToggleInteractPlugin.EndWork(reason, forceRelease);
            ToggleInteractPlugin.SuppressWhileInWork = 1f;
            _triggerJoy = -1;
            _triggerKey = KeyCode.None;
            ToggleInteractPlugin.TakeOverRequested = false;
        }

        /// <summary>
        /// 「界面 / 功能键」触发的收手：放手之后把两个防抖标记一起清掉。
        ///
        /// 为什么：SuppressWhileInWork 和 LastStopTime 是为「玩家用同一个键取消」准备的
        /// —— 那次按键必须被吃掉，否则刚停就被同一次按键又接管回来。
        /// 但界面 / 功能键不是同一个键，不存在这个问题；留着它们反而会挡住
        /// 「关掉界面后立刻按 X 重新开干」——表现就是只做一下、要等一会儿才恢复持续化。
        /// </summary>
        private void StopForUi(string reason)
        {
            Stop(reason, forceExit: false, forceRelease: false);
            ToggleInteractPlugin.SuppressWhileInWork = 0f;
            ToggleInteractPlugin.LastStopTime = -10f;
        }

        // ---- 停滞判定（缺材料等）----
        private string _progressKey;        // 进度指纹（目前只用目标 HP）
        private float _progressSince;       // 指纹最后一次变化的时刻
        private Vector3 _lastPos;           // 上一帧玩家位置
        private bool _hasPos;
        private float _movedSinceProgress;  // 上次进度变化以来玩家累计位移（走位时不判停滞）

        /// <summary>
        /// 进度有没有在动。返回 true 表示判定为「卡住了」。
        ///
        /// 为什么需要：材料 / 燃料不足这类情况，游戏**不会**抛 OnNotEnoughResOccurred
        /// （反编译确认那个事件只有 "energy" 和 "insanity" 两种），
        /// 它就是把玩家留在工作状态里空转：目标没消失、也没离开工作状态，
        /// 现有规则一条都不满足，于是我们会一直按着工作键不放。
        ///
        /// 指纹只取「目标 HP」——那是真进度（砍树 / 挖矿 / 拆部件，每命中一次就变）。
        ///
        /// ★ 曾经拿「工具是否在动作」当制作台类的指纹，结果误触发：
        ///   那个标志在动作间隙、以及 craft 自己推进时都是 false，
        ///   指纹长时间不变就被判成停滞，把正常的制作也打断了。
        ///   制作台/工地现在交给专门的「材料不足」精确判定（见 TryGetCraftNotEnough），
        ///   所以这里读不到 HP 就干脆不判。
        ///
        /// 走位用「累计位移」而不是逐帧阈值：逐帧位移很小，阈值稍大就会漏判，
        /// 导致走过去的过程被算成停滞（这也是之前的误触发来源之一）。
        /// </summary>
        private bool UpdateStall(bool working, bool hasTarget)
        {
            int hp, max;
            bool hasHp = TryGetHp(_lockedTarget, out hp, out max);

            if (!hasHp)
            {
                _progressSince = Time.time;
                _movedSinceProgress = 0f;
                return false;
            }

            string key = "hp:" + hp;

            Vector3 pos = Vector3.zero;
            try
            {
                PlayerController pc = SafeController();
                if (pc != null) pos = pc.transform.position;
            }
            catch { }

            if (_hasPos) _movedSinceProgress += Vector3.Distance(pos, _lastPos);
            _lastPos = pos;
            _hasPos = true;

            if (key != _progressKey || _movedSinceProgress > 0.5f || !hasTarget)
            {
                _progressKey = key;
                _progressSince = Time.time;
                _movedSinceProgress = 0f;
                return false;
            }

            if (_progressSince <= 0f)
            {
                _progressSince = Time.time;
                return false;
            }

            return Time.time - _progressSince > ToggleInteractPlugin.StallSeconds;
        }

        /// <summary>
        /// 目标的工作台是不是「缺材料」。
        ///
        /// ★ 这一条精确对应你在工作台上看到的那个「打叉」标记。反编译确认：
        ///     enum CraftStatus { ... NotEnoughResources = 1, ... }
        ///     ... return CraftStatus.NotEnoughResources;        // 材料不够时状态就返回它
        ///     CraftStatus.NotEnoughResources => GetSprite("craft_status_not_enough_items");
        ///   所以读 CraftElementsQueue[0].CraftStatus 就能确切知道缺料，
        ///   不用再靠「进度停滞」去猜（那正是误触发的来源）。
        /// </summary>
        private static bool TryGetCraftNotEnough(object wgo, out string statusName)
        {
            statusName = null;
            try
            {
                if (IsUnityNull(wgo)) return false;

                // wgo 是 Wgo，数据在 .Data；也兼容直接传 WgoData 的情况
                object craft = ToggleInteractPlugin.ReadMember(wgo, "CraftComponent");
                if (craft == null)
                {
                    object data = ToggleInteractPlugin.ReadMember(wgo, "Data");
                    if (data == null) return false;
                    craft = ToggleInteractPlugin.ReadMember(data, "CraftComponent");
                }
                if (craft == null) return false;

                object queue = ToggleInteractPlugin.ReadMember(craft, "CraftElementsQueue");
                if (queue == null) return false;

                object element = null;
                System.Collections.IList list = queue as System.Collections.IList;
                if (list != null)
                {
                    if (list.Count == 0) return false;
                    element = list[0];
                }
                else
                {
                    // 自定义队列：走索引器 Item[0]
                    PropertyInfo item = queue.GetType().GetProperty("Item",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, null, new[] { typeof(int) }, null);
                    if (item == null) return false;
                    element = item.GetValue(queue, new object[] { 0 });
                }
                if (element == null) return false;

                object status = ToggleInteractPlugin.ReadMember(element, "CraftStatus");
                if (status == null) return false;

                statusName = status.ToString();
                return Convert.ToInt32(status) == 1;   // CraftStatus.NotEnoughResources = 1
            }
            catch { return false; }
        }

        private void Probe(bool working, bool hasTarget)
        {
            if (!ToggleInteractPlugin.VerboseMode) return;
            if (Time.time < _nextProbe) return;
            _nextProbe = Time.time + 1f;

            int hp, max;
            bool hasHp = TryGetHp(_lockedTarget, out hp, out max);
            ToggleInteractPlugin.Log(
                $"[probe] id={_lockedId} target={hasTarget} wip={working} " +
                $"hp={(hasHp ? hp + "/" + max : "n/a")} anchor={PosText(_anchorPos)}");
        }

        private static object GetWorkComponent()
        {
            try
            {
                PlayerController pc = SafeController();
                return pc != null ? ToggleInteractPlugin.ReadMember(pc, "PlayerWorkComponent") : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 安全取 PlayerController。
        /// 它是 `MainGame.PlayerController => Instance.playerController`，
        /// 内部若 MainGame 实例还没就绪就会抛空引用，所以不能裸调。
        /// </summary>
        private static PlayerController SafeController()
        {
            try { return MainGame.PlayerController; }
            catch { return null; }
        }

        private static object GetWorkTarget()
        {
            object comp = GetWorkComponent();
            return comp != null ? ToggleInteractPlugin.ReadMember(comp, "Wgo") : null;
        }

        private static string GetIdOf(object wgo)
        {
            try
            {
                if (IsUnityNull(wgo)) return null;
                object data = ToggleInteractPlugin.ReadMember(wgo, "Data");
                return data != null ? ToggleInteractPlugin.ReadMember(data, "id") as string : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 资源类型前缀：同一棵树改形态时会变的部分。
        ///   tree_fir_clr1_l_v1  ->  stump_fir_clr1_l_v1   （前缀 tree_ -> stump_）
        /// 去掉它之后剩下的就是「族名」，用来判断是不是同一个资源点。
        /// </summary>
        private static readonly string[] KindPrefixes =
        {
            "tree_", "stump_", "bush_", "rock_", "stone_", "ore_", "ores_",
            "log_", "trunk_", "branch_", "grass_", "flower_", "plant_", "resource_"
        };

        /// <summary>
        /// 是不是「资源类」目标（树、矿、灌木…）：这类被采完之后会换形态或消失，
        /// 对象会先躺着播一段倒地动画，所以判定「干完了」要给它更长的窗口。
        /// </summary>
        private static bool IsResourceLike(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string s = id.ToLowerInvariant();
            foreach (string prefix in KindPrefixes)
            {
                if (s.StartsWith(prefix)) return true;
            }
            return false;
        }

        /// <summary>
        /// 取资源族名：去掉类型前缀与末尾形态/版本号。
        ///   tree_fir_clr1_l_v1  -> fir_clr1_l
        ///   stump_fir_clr1_l_v1 -> fir_clr1_l     （相同 -> 同一资源点，属于转化）
        ///   tree_fir_clr1_xs_v1 -> fir_clr1_xs    （不同 -> 旁边另一棵树，应当停止）
        /// </summary>
        private static string FamilyOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string s = id.ToLowerInvariant();

            foreach (string prefix in KindPrefixes)
            {
                if (s.StartsWith(prefix))
                {
                    s = s.Substring(prefix.Length);
                    break;
                }
            }

            s = System.Text.RegularExpressions.Regex.Replace(s, @"_v\d+$", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_\d+$", "");
            return s;
        }

        private static bool IsUnityNull(object o)
        {
            if (o == null) return true;
            UnityEngine.Object uo = o as UnityEngine.Object;
            return uo == null;
        }
    }
}
