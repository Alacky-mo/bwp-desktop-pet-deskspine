using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TinyEngine
{
    /// <summary>
    /// 阴阳师百闻牌式神
    /// </summary>
    internal class BwpHero : Animator
    {
        private enum Role
        {
            Idle,       // 待机循环
            Alert,      // 站立/被拎起
            Attack,     // 出击
            Cast,       // 施法
            Entrance,   // 破壳登场
            Sleep,      // 入睡期间
        }

        /// <summary>
        /// Q 版表情骨架 (spine.npk 里的头像小人) 用的表情词, 全部按归一化后的名字精确匹配.
        /// 909 个式神骨架里一个都不命中, 所以命中就等于这是一套表情词汇而不是姿态词汇.
        /// </summary>
        private static readonly string[] MoodWords =
            ["happy", "sad", "surprise", "thinking", "smile", "angry", "serious", "emotion"];

        // 顺手纠素材里唯一的拼错件：maozhanggui_sp_yihua 的惊讶写作 suprise。
        // 不纠它进不了 MoodWords 的门，那颗头只剩 happy/sad/thinking，拖拽和单击撞脸。
        /// <summary>
        /// 抹掉数字和下划线, 让 idle1/idle/idle2/idle1_old 归一成同一个候选名
        /// </summary>
        internal static string Normalize(string name)
        {
            var letters = new string(name.ToLowerInvariant().Where(char.IsLetter).ToArray());
            return letters == "suprise" ? "surprise" : letters;
        }

        /// <summary>
        /// 一个骨架实际能提供的动作名, 缺失的动作退化成同类动作
        /// </summary>
        private sealed class Actions
        {
            public readonly string Idle;
            public readonly string Alert;
            public readonly string Attack;
            public readonly string Cast;
            public readonly string? Entrance;
            public readonly string Sleep;

            /// <summary>
            /// 表情骨架能换的表情, 供闲置时随机抽; 战斗骨架为空
            /// </summary>
            public readonly ReadOnlyCollection<string> Moods;

            public Actions(ReadOnlyCollection<string> names)
            {
                var raw = names.ToArray();
                var norm = raw.Select(Normalize).ToArray();
                Moods = new ReadOnlyCollection<string>(
                    raw.Where((_, i) => MoodWords.Contains(norm[i])).ToList());

                var roles = Moods.Count > 0 ? MoodRoles(raw, norm, Moods) : StanceRoles(raw, norm);
                Idle = roles.Idle;
                Alert = roles.Alert;
                Attack = roles.Attack;
                Cast = roles.Cast;
                Entrance = roles.Entrance;
                Sleep = roles.Sleep;
            }

            /// <summary>
            /// 表情词汇: 只有脸没有姿态, 所以把交互直接映射成表情。
            /// 分配顺序是有讲究的——先分配的手优先拿到招牌表情, 后分配的才去找没用过的脸,
            /// 表情够多的骨架四个交互就不会撞成同一张脸。
            /// </summary>
            private static (string Idle, string Alert, string Attack, string Cast, string? Entrance, string Sleep)
                MoodRoles(string[] raw, string[] norm, ReadOnlyCollection<string> moods)
            {
                var given = new HashSet<string>();

                // 按 wants 的先后次序找表情, 优先挑还没被别的交互用走的那张
                string Mood(params string[] wants)
                {
                    string? By(bool unusedOnly, string want) =>
                        moods.FirstOrDefault(m => (!unusedOnly || !given.Contains(m)) && Normalize(m) == want);

                    var hit = wants.Select(w => By(true, w)).FirstOrDefault(x => x is not null)
                        ?? wants.Select(w => By(false, w)).FirstOrDefault(x => x is not null)
                        ?? moods.FirstOrDefault(m => !given.Contains(m))
                        ?? moods[0];
                    given.Add(hit);
                    return hit;
                }

                var idle = Find(raw, norm, "idle", "idl") ?? moods[0];
                var alert = Mood("surprise", "angry");       // 被拎起来该吃惊
                var attack = Mood("happy", "smile");         // 单击是摸它, 给个好脸
                var cast = Mood("thinking", "serious");      // 双击/滚轮是让它做事, 给个认真脸
                var entrance = Mood("surprise", "happy");    // 登场/睡醒/报时: 抬头看一眼, 别给难过脸
                return (idle, alert, attack, cast, entrance, idle);
            }

            /// <summary>
            /// 战斗姿态词汇: 式神立绘的五个动作槽, 缺哪个就退化成同类动作
            /// </summary>
            private static (string Idle, string Alert, string Attack, string Cast, string? Entrance, string Sleep)
                StanceRoles(string[] raw, string[] norm)
            {
                var alert = Find(raw, norm, "battlestand", "stand", "battl");
                var cast = Find(raw, norm, "casting", "cassting", "sting", "cast");
                var attack = Find(raw, norm, "combat", "casbat", "comb") ?? cast;

                var idle = Find(raw, norm, "idle", "idle1", "idl") ?? alert ?? raw[0];
                var entrance = Find(raw, norm, "bornbreak", "born");
                return (idle, alert ?? idle, attack ?? idle, cast ?? attack ?? idle, entrance, alert ?? idle);
            }

            public string Get(Role role) => role switch
            {
                Role.Alert => Alert,
                Role.Attack => Attack,
                Role.Cast => Cast,
                Role.Entrance => Entrance ?? Idle,
                Role.Sleep => Sleep,
                _ => Idle,
            };

            private static string? Find(string[] raw, string[] norm, string exact, params string[] hints)
            {
                for (int i = 0; i < norm.Length; i++)
                    if (norm[i] == exact) return raw[i];
                foreach (var hint in hints)
                    for (int i = 0; i < norm.Length; i++)
                        if (norm[i].StartsWith(hint, StringComparison.Ordinal)) return raw[i];
                foreach (var hint in hints)
                    for (int i = 0; i < norm.Length; i++)
                        if (norm[i].Contains(hint, StringComparison.Ordinal)) return raw[i];
                return null;
            }
        }

        private const float MeanWaitingTime = 40f;

        /// <summary>
        /// 战斗场景素材自带背景图、前景和大片中性白加色光雾, 叠在游戏的深色场景上是打光,
        /// 贴在桌面上会把那一块刷成不透明白. 装载时按槽名前缀去掉场景层、压暗光层.
        /// 前缀覆盖度实测: beijing 19.6%, bj 10.0%, qianjing 9.5%, bg 6.6%, guang 6.2% (909 个骨架)
        /// </summary>
        private static readonly (string Prefix, float Alpha)[] SceneLayers =
        [
            ("beijing", 0f),
            ("qianjing", 0f),
            ("bj", 0f),
            ("bg", 0f),
            ("guang", 0.25f),
        ];

        /// <summary>
        /// 槽位上的 Spine 对象会被整体替换, 所以缓存必须连着对象一起校验
        /// </summary>
        private readonly record struct Cache(Spine.Spine Owner, Actions Actions);

        private readonly Cache?[] cache;
        private readonly Random rnd = new();
        private State state = State.Idle;
        private float randomElapsedTime = 0f;
        private float nextRandomTime = 10f;
        private volatile bool chimeRequested = false;

        public BwpHero(Spine.Spine[] slots) : base(slots)
        {
            cache = new Cache?[slots.Length];
        }

        public override void Reset()
        {
            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var act = Resolve(i);
                if (slots[i] is null || act is null) continue;
                PlayOnce(slots[i]!, act, act.Get(Role.Entrance));
            }
        }

        public override void Update(float delta)
        {
            base.Update(delta);

            bool chime = chimeRequested;
            if (chime) chimeRequested = false;

            randomElapsedTime += delta;
            bool randomTime = randomElapsedTime >= nextRandomTime;
            if (randomTime)
            {
                randomElapsedTime = 0;
                nextRandomTime = (float)(-MeanWaitingTime * Math.Log(rnd.NextSingle()));
            }

            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;

                if (chime) PlayOnce(sp, act, act.Entrance ?? act.Cast);
                else if (randomTime && state == State.Idle) PlayOnce(sp, act, PickAmbient(act));
            }
        }

        /// <summary>
        /// 整点报时只置标记, 真正的动画切换留给渲染线程
        /// </summary>
        public override void HourlyChime() => chimeRequested = true;

        /// <summary>
        /// 番茄钟换阶段就换张脸: 开工给沉思脸, 到点休息给好脸, 休息完回来和做完一整轮就抬头看一眼。
        /// 语音不用管 —— PlayOnce 里已经接好「表情→句池」, 切脸等于开口, 也只有出场那格开口。
        /// </summary>
        public override void FocusPhase(PomodoroPhase from, PomodoroPhase to)
        {
            if (to == PomodoroPhase.Off) return;    // 中止是用户自己点的菜单, 不该再蹦一句

            var role = to switch
            {
                PomodoroPhase.Working => from == PomodoroPhase.Off ? Role.Cast : Role.Entrance,
                PomodoroPhase.LongBreak => Role.Entrance,
                _ => Role.Attack,
            };

            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;
                PlayOnce(sp, act, act.Get(role));
            }
        }

        public override void Click(SFML.Window.Mouse.Button button)
        {
            base.Click(button);
            if (button != SFML.Window.Mouse.Button.Left) return;

            bool waking = state == State.Sleeping;
            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;
                PlayOnce(sp, act, waking ? act.Get(Role.Entrance) : act.Get(Role.Attack));
            }
        }

        public override void DoubleClick(SFML.Window.Mouse.Button button)
        {
            base.DoubleClick(button);
            if (button != SFML.Window.Mouse.Button.Left) return;

            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;
                PlayOnce(sp, act, act.Get(Role.Cast));
            }
        }

        public override void Scroll(SFML.Window.Mouse.Wheel wheel, float delta)
        {
            base.Scroll(wheel, delta);

            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null || sp.CurrentAnimation == act.Cast) continue;
                PlayOnce(sp, act, act.Cast);
            }
        }

        public override void DragBegin(SFML.Window.Mouse.Button button)
        {
            base.DragBegin(button);

            state = State.Dragging;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;
                sp.CurrentAnimation = act.Alert;
            }
        }

        public override void DragEnd(SFML.Window.Mouse.Button button)
        {
            base.DragEnd(button);

            state = State.Idle;
            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;
                sp.CurrentAnimation = act.Idle;
            }
        }

        public override void SleepStateChange(bool sleep)
        {
            base.SleepStateChange(sleep);

            if (sleep)
            {
                if (state != State.Idle) return;
                state = State.Sleeping;
            }
            else if (state != State.Sleeping)
            {
                return;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                var sp = slots[i];
                var act = Resolve(i);
                if (sp is null || act is null) continue;

                if (sleep) sp.CurrentAnimation = act.Get(Role.Sleep);
                else PlayOnce(sp, act, act.Get(Role.Entrance));
            }
        }

        /// <summary>
        /// 取该槽位当前骨架的动作表, 骨架换过就重新解析并重挂场景层过滤
        /// </summary>
        private Actions? Resolve(int index)
        {
            var sp = slots[index];
            if (sp is null) return null;
            if (cache[index] is Cache hit && hit.Owner == sp) return hit.Actions;
            var act = new Actions(sp.AnimationNames);
            sp.SlotAlphaScale.Clear();
            foreach (var (prefix, alpha) in SceneLayers)
                sp.SlotAlphaScale[prefix] = alpha;
            cache[index] = new Cache(sp, act);
            return act;
        }

        /// <summary>
        /// 闲着没事干时的自我表演: 表情骨架换一张脸, 战斗骨架按原来的姿态概率抽
        /// </summary>
        private string PickAmbient(Actions act) => act.Moods.Count > 0
            ? act.Moods[rnd.Next(act.Moods.Count)]
            : act.Get(rnd.NextSingle() switch
            {
                < 0.5f => Role.Alert,
                < 0.8f => Role.Cast,
                _ => Role.Attack,
            });

        /// <summary>
        /// 播一遍该动画, 播完自动排回待机, 顺手让这张脸说一句属于它的话(只给出场的那格开口)。
        /// 专注段静音时脸照换, 只是这一句不配 —— 表情是状态, 语音才是打扰
        /// </summary>
        private void PlayOnce(Spine.Spine sp, Actions act, string name)
        {
            sp.CurrentAnimation = name;
            sp.AddAnimation(act.Idle);
            if (OnStage(sp) && !Muted) Speaker?.Say(name, sp.SkelPath);
        }
    }
}
