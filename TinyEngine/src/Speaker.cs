using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TinyEngine
{
    /// <summary>
    /// 语音播放器: 动画切到哪张脸, 就从**该骨架旁边的** voice/_manifest.tsv 里抽一句这句脸对应的台词播。
    /// 包由 bwp-assets/voice_pack.py 从联系表生成(硬链 wav + manifest); 没有包就完全不出声, 不报错。
    /// 素材是 vgmstream 解出来的单声道 16bit PCM wav, 所以直接用 winmm 的 PlaySound, 不引第三方库。
    /// </summary>
    public class Speaker : Renderable
    {
        [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string? pszSound, IntPtr hModule, uint dwFlags);

        private const uint SND_ASYNC = 0x00000001;
        private const uint SND_NODEFAULT = 0x00000002;
        private const uint SND_FILENAME = 0x00020000;

        private sealed record Line(string File, string Text);

        /// <summary>骨架目录 -> (表情动画名 -> 台词池); 没包的骨架也存一条空表, 免得每次交互都重读磁盘</summary>
        private readonly Dictionary<string, Dictionary<string, List<Line>>> packs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个「骨架+表情」一轮没播完的顺序, 播完才重新洗, 所以不会连着听同一句</summary>
        private readonly Dictionary<string, Queue<Line>> rotating = new();

        private readonly Random rnd = new();

        /// <summary>总开关, 关掉只停新台词, 已经响着的那句让它说完</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 播一句属于该表情的台词。animation 是骨架实际的动画名, 所以素材里 idle1/idle 这种写法差异在这里抹平。
        /// 一次只放一句: 后一句会把前一句掐掉(表情语音才 1~2 秒, 连点时宁可打断也不要叠成一团)。
        /// </summary>
        public void Say(string animation, string? skelPath)
        {
            if (!Enabled || string.IsNullOrEmpty(skelPath)) return;
            var pool = Pack(Path.GetDirectoryName(skelPath));
            string? mood = null;
            foreach (var name in pool.Keys)
                if (name == animation || BwpHero.Normalize(name) == BwpHero.Normalize(animation)) { mood = name; break; }
            if (mood is null || pool[mood].Count == 0) return;

            var key = $"{skelPath}|{mood}";
            if (!rotating.TryGetValue(key, out var q) || q.Count == 0)
                rotating[key] = q = new Queue<Line>(Shuffle(pool[mood]));
            PlaySound(q.Dequeue().File, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }

        /// <summary>停掉当前这句(窗口隐藏/入睡时用)</summary>
        public void Stop() => PlaySound(null, IntPtr.Zero, SND_FILENAME | SND_ASYNC);

        public override void VisibleChange(bool visible)
        {
            base.VisibleChange(visible);
            if (!visible) Stop();
        }

        public override void SleepStateChange(bool sleep)
        {
            base.SleepStateChange(sleep);
            if (sleep) Stop();
        }

        private Dictionary<string, List<Line>> Pack(string? dir)
        {
            if (dir is null) return new();
            if (packs.TryGetValue(dir, out var hit)) return hit;
            var pool = new Dictionary<string, List<Line>>(StringComparer.OrdinalIgnoreCase);
            var man = Path.Combine(dir, "voice", "_manifest.tsv");
            if (File.Exists(man))
                foreach (var line in File.ReadAllLines(man, Encoding.UTF8).Skip(1))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3) continue;
                    var wav = Path.Combine(dir, "voice", f[2]);
                    if (!File.Exists(wav)) continue;
                    if (!pool.TryGetValue(f[0], out var list)) pool[f[0]] = list = [];
                    list.Add(new Line(wav, f.Length > 3 ? f[3] : ""));
                }
            packs[dir] = pool;
            return pool;
        }

        private List<Line> Shuffle(List<Line> lines)
        {
            var a = new List<Line>(lines);
            for (int i = a.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
            return a;
        }
    }
}
