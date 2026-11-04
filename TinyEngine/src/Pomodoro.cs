using System;

namespace TinyEngine
{
    /// <summary>
    /// 番茄钟阶段
    /// </summary>
    public enum PomodoroPhase
    {
        Off,        // 没在计时
        Working,    // 专注中
        ShortBreak, // 短休息
        LongBreak,  // 做完一整轮之后的长休息
    }

    /// <summary>
    /// 番茄钟: 只管时钟和阶段推进, 长什么脸交给动画控制器。
    /// 时间一律读 <see cref="Clock"/>, 不累加帧 delta —— 窗口藏着的时候 Update 根本不跑, 用 delta 会漏时间。
    /// 但挂钟单独用又会把「合盖睡掉的六小时」当成「专注了六小时」, 所以再配一只只计工作状态的
    /// <see cref="Mono"/>, 两只一比就知道中间睡了多久, 睡掉的那段从这一段里退回去 (见 <see cref="NoteAway"/>).
    /// </summary>
    public class Pomodoro : Renderable
    {
        public int WorkMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int LongBreakMinutes { get; set; } = 15;

        /// <summary>连做几个番茄才给一次长休息</summary>
        public int CyclesPerLongBreak { get; set; } = 4;

        /// <summary>专注期间顶不顶掉闲置入睡, 设置窗给关掉留的口子</summary>
        public bool BlockSleepWhileWorking { get; set; } = true;

        /// <summary>一段专注跑完之后要不要自动接休息。关掉就停在休息那一格, 等用户自己按开始</summary>
        public bool AutoStartBreak { get; set; } = true;

        /// <summary>休息跑完之后要不要自动开下一段专注。和上面那颗分开, 因为「到点该歇」和「歇完该干」是两回事</summary>
        public bool AutoStartWork { get; set; } = true;

        /// <summary>
        /// 挂钟, 无头验收换成假时钟就能快进
        /// </summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        /// <summary>
        /// 第二只钟: 机器睡着时它不涨。快进 <see cref="Clock"/> 的验收要么给它一只同步走的假钟 (差值恒为 0),
        /// 要么就让它落后 —— 落后就是「合盖了」, 判据靠这个差值来验休眠平移
        /// </summary>
        public Func<TimeSpan> Mono { get; set; } = Win32.GetUnbiasedElapsedTime;

        /// <summary>
        /// 两只钟差多少才当睡过觉。挂钟粒度约 16ms、帧间隔 33ms, 两秒之外就不可能是抖动了
        /// </summary>
        private static readonly TimeSpan AwayIsSleep = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 阶段切换要落到骨骼上, 而骨骼只能在渲染线程碰, 所以这里只放目标槽位
        /// </summary>
        public SpineSlot? Target { get; set; }

        private readonly object gate = new();
        private PomodoroPhase phase = PomodoroPhase.Off;
        private DateTime phaseEnd;
        private TimeSpan pausedLeft = TimeSpan.Zero;
        private bool paused = false;
        private int worksInARow = 0;

        private int completedToday = 0;
        private DateTime completedDay = DateTime.MinValue;

        // 上一次观察时的两只钟读数, 用来算「挂钟走了多少 vs 机器实际醒着多久」
        private bool anchored = false;
        private DateTime wallAnchor;
        private TimeSpan monoAnchor;

        // 渲染线程每帧取走的最新一次阶段切换; 只留最新, 追补过来的旧脸没意义
        private bool pending = false;
        private PomodoroPhase pendingFrom, pendingTo;

        /// <summary>
        /// 每次阶段推进都自增, 托盘拿它和上次的差值决定要不要弹气泡
        /// </summary>
        public volatile int TransitionId = 0;

        public PomodoroPhase Phase { get { lock (gate) return phase; } }

        public bool IsPaused { get { lock (gate) return paused; } }

        /// <summary>正在专注就别让宠物睡着, 不然「干活的脸」睡没了番茄钟等于没做</summary>
        public bool BlockingSleep
        {
            get { lock (gate) return BlockSleepWhileWorking && phase == PomodoroPhase.Working && !paused; }
        }

        public TimeSpan Remaining
        {
            get
            {
                lock (gate)
                {
                    if (phase == PomodoroPhase.Off) return TimeSpan.Zero;
                    return Bound(paused ? pausedLeft : phaseEnd - Clock());
                }
            }
        }

        public int CompletedToday { get { lock (gate) { var now = Clock(); RollDay(now); return completedToday; } } }

        /// <summary>一轮几段专注才给一次长休息。配置可能被改成 0, 这里兜住下限, 别让取余炸或每段都算收尾</summary>
        public int RoundsPerLongBreak { get { lock (gate) return Math.Max(1, CyclesPerLongBreak); } }

        /// <summary>
        /// 本轮第几段专注 (从 1 起, 配 <see cref="RoundsPerLongBreak"/> 念成「2/4」)。
        /// 专注段报「正在做的那一段」, 休息段报「刚做完的那一段」—— 所以长假那一格读得出 4/4, 不是 0/4
        /// </summary>
        public int WorkRoundNumber { get { lock (gate) return RoundPosition(); } }

        /// <summary>现在这一段该有多长, 托盘气泡报数用, 省得界面那边再抄一遍阶段到分钟的对应</summary>
        public int PhaseMinutes { get { lock (gate) return (int)SpanOf(phase).TotalMinutes; } }

        /// <summary>开始; 已暂停就接着跑, 没在跑就开一段专注</summary>
        public void Start()
        {
            lock (gate)
            {
                var now = Clock();
                RollDay(now);
                // 有人按下开始就先给两只钟对一次表: 窗口藏着时 Update 不跑,
                // 不对表的话「按下之后合盖睡到明天」会被当成正常流逝, 一帧补出十几个番茄
                if (!anchored) Anchor(now, Mono());
                if (paused)
                {
                    phaseEnd = now + pausedLeft;
                    paused = false;
                    return;
                }
                if (phase != PomodoroPhase.Off) return;
                Begin(PomodoroPhase.Working, now);
            }
        }

        /// <summary>按下先冻住剩余时间</summary>
        public void Pause()
        {
            lock (gate)
            {
                if (phase == PomodoroPhase.Off || paused) return;
                pausedLeft = Bound(phaseEnd - Clock());
                paused = true;
            }
        }

        /// <summary>中止不排阶段切换: 这一下是用户自己点的, 再蹦一句语音算打扰</summary>
        public void Abort()
        {
            lock (gate)
            {
                phase = PomodoroPhase.Off;
                paused = false;
                pausedLeft = TimeSpan.Zero;
                worksInARow = 0;
            }
        }

        /// <summary>手动结束当前这一段, 直接进下一段。跳过的那段不计一个番茄, 但轮内位置照样往前走</summary>
        public void Skip()
        {
            lock (gate)
            {
                if (phase == PomodoroPhase.Off) return;
                Advance(Clock(), skipped: true);
            }
        }

        public override void Update(float delta)
        {
            base.Update(delta);

            PomodoroPhase from = default, to = default;
            var jumped = false;
            var quiet = false;
            lock (gate)
            {
                var now = Clock();
                // 顺序不能反: 先把睡掉的那段从当前这一段里退出去, 再逐段补。
                // 反过来就成了「先按挂钟补完十几个番茄, 再把已经作废的 deadline 往前挪」
                NoteAway(now);
                CatchUp(now);
                if (pending)
                {
                    pending = false;
                    from = pendingFrom;
                    to = pendingTo;
                    jumped = true;
                }
                quiet = phase == PomodoroPhase.Working;
            }

            var target = Target;
            if (target is null) return;

            // 专注段闭嘴, 但边界那句照响: 顺序是「先解锁 → 换脸 → 再按当前阶段静音」。
            // 这样开工/到点/回来这三种提醒都听得见, 只有"段内自言自语"被按住;
            // 而中止是不排 pending 的, 靠最后一行才解得开(窗口藏着时 Update 不跑, 回来第一帧就补上)
            if (jumped)
            {
                target.Muted = false;
                target.FocusPhase(from, to);
            }
            target.Muted = quiet;
        }

        private void Begin(PomodoroPhase target, DateTime now)
        {
            phase = target;
            phaseEnd = now + SpanOf(target);
            Queue(PomodoroPhase.Off, target);
        }

        /// <summary>
        /// 走到下一段。<paramref name="skipped"/> 分的是「跑满了」还是「用户跳过的」:
        /// 只有跑满一段才进今日计数, 但轮内位置两种都往前走 —— 跳过不等于那一段没排过, 只是它不算数
        /// </summary>
        private void Advance(DateTime start, bool skipped)
        {
            var from = phase;
            PomodoroPhase to;
            if (from == PomodoroPhase.Working)
            {
                if (!skipped) completedToday++;
                worksInARow++;
                to = worksInARow % Math.Max(1, CyclesPerLongBreak) == 0
                    ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
            }
            else to = PomodoroPhase.Working;

            phase = to;
            phaseEnd = start + SpanOf(to);
            // 自动开始关着: 这一段装好但不走。用「已暂停 + 剩余 = 整段」来表达「等用户点一下」,
            // 引擎里就不必多开一种状态, 出口层读到的还是它已经认识的那两个数(按开始正好从整段的头接上)
            paused = !(from == PomodoroPhase.Working ? AutoStartBreak : AutoStartWork);
            pausedLeft = paused ? SpanOf(to) : TimeSpan.Zero;
            Queue(from, to);
        }

        /// <summary>本轮第几段的算法, 见 <see cref="WorkRoundNumber"/></summary>
        private int RoundPosition()
        {
            var cycle = Math.Max(1, CyclesPerLongBreak);
            var done = worksInARow % cycle;            // 这一轮里已经做完几段
            if (phase == PomodoroPhase.Off) return 1;  // 中止/没开始过都念「第 1 段」
            if (phase == PomodoroPhase.Working) return done + 1;
            return done == 0 ? cycle : done;           // 休息段: 刚做完那一段是第几段
        }

        /// <summary>
        /// 窗口藏着时 Update 不跑, 露出来可能已经欠了好几段: 按排定时刻逐段补, 只把最新那张脸交出去
        /// </summary>
        private void CatchUp(DateTime now)
        {
            RollDay(now);
            int guard = 0;
            // 补过来的每一段都算「自然跑完」——跳过只有用户点的那一下, 藏着的时候跳不了
            while (phase != PomodoroPhase.Off && !paused && now > phaseEnd && guard++ < 64)
                Advance(phaseEnd, skipped: false);
            // 补过头(藏了一整天)就从此刻重新给这一段计时, 不无限追
            if (phase != PomodoroPhase.Off && !paused && now > phaseEnd) phaseEnd = now + SpanOf(phase);
        }

        /// <summary>
        /// 两只钟一比就知道中间有没有睡过觉: 挂钟走得多、单调钟走得少, 差的那一截就是合盖的时长。
        /// 把它从当前这一段的终点里退掉, 等于「睡着的那会儿根本没在计时」,
        /// 于是合盖六小时回来还停在原来那一段, 而不是补出一串番茄。
        /// 暂停中那一段本来就不吃时间, 所以只挪走着的 deadline, 不去碰冻住的剩余。
        /// </summary>
        private void NoteAway(DateTime now)
        {
            var mono = Mono();
            if (anchored && mono >= monoAnchor && phase != PomodoroPhase.Off && !paused)
            {
                var away = (now - wallAnchor) - (mono - monoAnchor);
                if (away > AwayIsSleep) phaseEnd += away;
            }
            // 挂钟被拨到过去(改时区、NTP、手动改表)时 deadline 能跑到「整段」之外, 封顶在这一段本身
            if (phase != PomodoroPhase.Off)
            {
                if (paused) pausedLeft = Bound(pausedLeft);
                else if (phaseEnd > now + SpanOf(phase)) phaseEnd = now + SpanOf(phase);
            }
            Anchor(now, mono);
        }

        private void Anchor(DateTime now, TimeSpan mono)
        {
            wallAnchor = now;
            monoAnchor = mono;
            anchored = true;
        }

        /// <summary>剩余不许出格: 负数说不出口, 比整段还长是演给托盘看的假数</summary>
        private TimeSpan Bound(TimeSpan left)
        {
            var span = SpanOf(phase);
            if (left < TimeSpan.Zero) return TimeSpan.Zero;
            return left > span ? span : left;
        }

        private TimeSpan SpanOf(PomodoroPhase p) => TimeSpan.FromMinutes(p switch
        {
            PomodoroPhase.Working => Math.Max(1, WorkMinutes),
            PomodoroPhase.LongBreak => Math.Max(1, LongBreakMinutes),
            PomodoroPhase.Off => 0,
            _ => Math.Max(1, BreakMinutes),
        });

        private void Queue(PomodoroPhase from, PomodoroPhase to)
        {
            pendingFrom = from;
            pendingTo = to;
            pending = true;
            TransitionId++;
        }

        private void RollDay(DateTime now)
        {
            if (completedDay.Date == now.Date) return;
            completedDay = now.Date;
            completedToday = 0;
            worksInARow = 0;
        }
    }
}
