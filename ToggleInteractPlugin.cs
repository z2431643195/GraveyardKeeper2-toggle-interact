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
        public const string PluginVersion = "1.0.0";

        internal static ToggleInteractPlugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IdleTimeoutMs;
        internal static ConfigEntry<float> LostTargetGraceMs;
        internal static ConfigEntry<float> AnchorRadius;
        internal static ConfigEntry<bool> Verbose;

        internal static bool WorkActive;              // 是否正在维持持续工作
        internal static int ActionKeyId = 101;        // GameKey.Action 的数值
        internal static int InteractionKeyId = -1;    // GameKey.Interaction 的数值
        internal static bool ReplayInteractionPending; // 需要把一次交互键补还给游戏
        internal static float ReplayInteractionAt;
        internal static float ForceReleaseUntil;      // 这段时间内强制「工作键已松开」
        internal static float SuppressWhileInWork;    // >0 表示退出工作状态前不再接管
        internal static float LastWorkActionTime;     // 最近一次「干活」动作的时间
        internal static int TriggerGameKeyId = -1;    // 开启时用的 GameKey（同键取消）

        private void Awake()
        {
            try
            {
                Instance = this;

                Enabled = Config.Bind("1. General 总开关", "Enabled", true,
                    "是否启用。判据是「该动作会不会显示进度条」——会显示进度条的动作" +
                    "（砍树 / 挖矿 / 施工 / 制作 / 种植 / 施肥 / 打水 …）会被持续化；" +
                    "开门、开箱、拿取这类没有进度条的动作不受影响。\n" +
                    "Enable the mod. Any action that shows a progress bar is made continuous.");

                IdleTimeoutMs = Config.Bind("2. Stop rules 停止判定", "IdleTimeoutMs", 3500f,
                    new ConfigDescription(
                        "离开工作状态持续多久（毫秒）后自动停止（被界面、剧情、控制锁定打断时用）。\n" +
                        "Auto-stop after being out of the work state for this many ms.",
                        new AcceptableValueRange<float>(500f, 20000f)));

                LostTargetGraceMs = Config.Bind("2. Stop rules 停止判定", "LostTargetGraceMs", 2500f,
                    new ConfigDescription(
                        "目标消失后，保持待命多久（毫秒）才真正收手。\n" +
                        "待命期里按键大部分时间是松开的（玩家能自由走动，不会卡手），" +
                        "插件只在其中周期性短暂按一下，用来让游戏重新寻找下一形态。\n" +
                        "这个值只决定「等下一形态等多久」：树倒下到树桩出现、矿脉换形态都在这个范围内；" +
                        "等满了仍无目标（真挖空/砍完）才收手。想更早收手可调小。\n" +
                        "How long to stay on standby waiting for the next form.",
                        new AcceptableValueRange<float>(200f, 8000f)));

                AnchorRadius = Config.Bind("2. Stop rules 停止判定", "AnchorRadius", 2.5f,
                    new ConfigDescription(
                        "转化判定里允许的最大偏移（结合资源族名一起用）：\n" +
                        "· 位置几乎没动（<=0.15）            -> 同一资源点的转化，继续\n" +
                        "· 资源族名相同且距离 <= 本值        -> 同一资源点的转化，继续\n" +
                        "· 其余                              -> 视为旁边的另一个资源，停止\n" +
                        "实测树 -> 树桩会挪 1.4 左右，所以默认给到 2.5（约 2 格）。\n" +
                        "Max offset for treating a new target as the same resource node transforming.",
                        new AcceptableValueRange<float>(0.1f, 10f)));

                Verbose = Config.Bind("3. Diagnostics 诊断", "Verbose", true,
                    "记录按键、命中与目标变化（排查用，稳定后可关）。\nLog keys, matches and target changes.");

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

        internal static float IdleSeconds =>
            IdleTimeoutMs != null ? IdleTimeoutMs.Value / 1000f : 3.5f;

        internal static float LostTargetGraceSeconds =>
            LostTargetGraceMs != null ? LostTargetGraceMs.Value / 1000f : 0.5f;

        internal static float Radius =>
            AnchorRadius != null ? AnchorRadius.Value : 3f;

        internal static bool VerboseMode => Verbose == null || Verbose.Value;

        internal static void Log(string message) => Instance?.Logger.LogInfo(message);
        internal static void LogError(string message) => Instance?.Logger.LogError(message);

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
            Log($"[auto-work ON] 触发键 gameKey={triggerGameKeyId}");
        }

        internal static void EndWork(string reason)
        {
            if (!WorkActive) return;
            WorkActive = false;
            TriggerGameKeyId = -1;
            LastStopTime = Time.time;
            NoTargetNow = false;
            // 只需要让游戏看到「键已松开」一瞬即可退出工作状态。
            // 之前给 1.5 秒，会把这期间玩家的按键一起压掉，表现为
            // 「刚停就按没反应，得再按一次」——0.3 秒足够且不挡手。
            ForceReleaseUntil = Time.time + 0.3f;
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
                if (Time.time - ToggleInteractPlugin.LastStopTime < 0.4f) return;

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

        private void Update()
        {
            try
            {
                if (ToggleInteractPlugin.Instance == null || !ToggleInteractPlugin.Enabled.Value) return;

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

                // ---------- 2. 玩家状态 ----------
                PlayerController pc = MainGame.PlayerController;
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

                Probe(working, hasTarget);
            }
            catch (Exception ex)
            {
                ToggleInteractPlugin.LogError("behaviour error: " + ex.Message);
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

        private void Stop(string reason)
        {
            ToggleInteractPlugin.ForceStopInteraction();
            ToggleInteractPlugin.EndWork(reason);
            ToggleInteractPlugin.SuppressWhileInWork = 1f;
            _triggerJoy = -1;
            _triggerKey = KeyCode.None;
            ToggleInteractPlugin.TakeOverRequested = false;
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
                PlayerController pc = MainGame.PlayerController;
                return pc != null ? ToggleInteractPlugin.ReadMember(pc, "PlayerWorkComponent") : null;
            }
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
