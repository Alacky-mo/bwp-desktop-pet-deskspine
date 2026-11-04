using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace DeskSpine
{
    /// <summary>
    /// focus_ui 那批衍生图的装载器，浮窗和预设小窗共用一份。
    /// 图都是本机跑 bwp-assets/install_focus_ui.py 装出来的，不进版本库，所以换台机器就是空的 ——
    /// 每张都允许读不到，调用方自己有「整行退回系统字体」那条路，别在这里抛
    /// </summary>
    internal sealed class FocusAssets : IDisposable
    {
        /// <summary>数字条的字，键用 ':' 存冒号那张（库里没这个字形，是仿出来的）</summary>
        public readonly Dictionary<char, Bitmap> Digits = new();

        public readonly Bitmap? FloatFrame;
        public readonly Bitmap? Panel;
        public readonly Bitmap? RadioOn;
        public readonly Bitmap? RadioOff;
        public readonly Bitmap? ButtonNormal;
        public readonly Bitmap? ButtonPressed;
        public readonly Bitmap? Close;

        /// <summary>
        /// 右下角那一小景的叶子，键用装机文件名（和 Digits 一个路子）。
        /// 摆哪几枚、离右沿多少格写在 FocusPresetForm.DecoSpots —— 那张表要能被无头判据读到，
        /// 不然「装饰件压没压到点得着的地方」这一条只能靠肉眼看图
        /// </summary>
        public readonly Dictionary<string, Bitmap> Deco = new();

        private FocusAssets(Bitmap? floatFrame, Bitmap? panel, Bitmap? radioOn, Bitmap? radioOff,
                            Bitmap? buttonNormal, Bitmap? buttonPressed, Bitmap? close,
                            Dictionary<char, Bitmap> digits, Dictionary<string, Bitmap> deco)
        {
            FloatFrame = floatFrame;
            Panel = panel;
            RadioOn = radioOn;
            RadioOff = radioOff;
            ButtonNormal = buttonNormal;
            ButtonPressed = buttonPressed;
            Close = close;
            Digits = digits;
            Deco = deco;
        }

        public static FocusAssets Load()
        {
            var dir = Path.Combine(Program.ProgramResourceDirectory, @"image\focus_ui");
            var digits = new Dictionary<char, Bitmap>();
            foreach (var ch in "0123456789:")
            {
                var tile = ReadPng(Path.Combine(dir, "digits", ch == ':' ? "colon.png" : $"{ch}.png"));
                if (tile is not null) digits[ch] = tile;
            }
            var deco = new Dictionary<string, Bitmap>();
            foreach (var spot in FocusPresetForm.DecoSpots)
            {
                var tile = ReadPng(Path.Combine(dir, spot.File));
                if (tile is not null) deco[spot.File] = tile;
            }
            return new FocusAssets(ReadPng(Path.Combine(dir, "bg_kuang02.png")),
                ReadPng(Path.Combine(dir, "bg_tips_3.png")),
                ReadPng(Path.Combine(dir, "btn_select_on.png")),
                ReadPng(Path.Combine(dir, "btn_select_off.png")),
                ReadPng(Path.Combine(dir, "com_btn_normal.png")),
                ReadPng(Path.Combine(dir, "com_btn_press.png")),
                ReadPng(Path.Combine(dir, "com_btn_close.png")), digits, deco);
        }

        /// <summary>
        /// 读进内存再放手：直接 new Bitmap(path) 会把文件锁住，下次构建想覆盖输出目录里那张就覆盖不了
        /// </summary>
        private static Bitmap? ReadPng(string path)
        {
            try
            {
                return new Bitmap(new MemoryStream(File.ReadAllBytes(path)));
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
            {
                Debug.WriteLine($"focus_ui 素材读不到 {path}: {e.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var tile in Digits.Values) tile.Dispose();
            Digits.Clear();
            FloatFrame?.Dispose();
            Panel?.Dispose();
            RadioOn?.Dispose();
            RadioOff?.Dispose();
            ButtonNormal?.Dispose();
            ButtonPressed?.Dispose();
            Close?.Dispose();
            foreach (var tile in Deco.Values) tile.Dispose();
            Deco.Clear();
        }
    }
}
