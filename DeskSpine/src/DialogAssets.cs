using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace DeskSpine
{
    /// <summary>
    /// 一条台词：分组只在开机头一条挑「开场」那组时用，正文是带『』的显示形态。
    /// </summary>
    internal readonly struct DialogLine
    {
        public DialogLine(string group, string text)
        {
            Group = group;
            Text = text;
        }

        public string Group { get; }
        public string Text { get; }
    }

    /// <summary>
    /// 台词气泡那两张件 + 台词池的装载器。
    /// 都是本机跑 bwp-assets/install_dialog_ui.py 装出来的，衍生件和游戏原文都不进版本库，
    /// 换台机器就是空的 —— 缺件就当「这条不显示」，别抛、也别画半张。
    /// 和 <see cref="FocusAssets"/> 分开两份：番茄钟那套素材换了一批不会牵连这里，反之也一样。
    ///
    /// 镜像那一侧的图在这儿就翻好存着：运行时每换一次台词要贴哪一侧才知道，
    /// 翻两次不如一次翻完（而且 RotateFlip 会改掉原图，得先 Clone）
    /// </summary>
    internal sealed class DialogAssets : IDisposable
    {
        /// <summary>说话人名，取自台词池第一行那列；池子空就是空串</summary>
        public string Speaker { get; private set; } = "";

        public Bitmap? Bubble { get; private set; }
        public Bitmap? BubbleMirrored { get; private set; }
        public Bitmap? Crest { get; private set; }
        public Bitmap? CrestMirrored { get; private set; }
        public IReadOnlyList<DialogLine> Lines { get; private set; } = Array.Empty<DialogLine>();

        /// <summary>
        /// 气泡**看得见的墨**在画布里的位置（装机图的 alpha ≥ 200 那块 + <see cref="DialogBubbleForm.BubbleAt"/>）。
        /// 摆位用它不用画布：画布左边白给了 14 px，按画布贴就等于气泡离宠物多 14 px
        /// </summary>
        public Rectangle Ink { get; private set; }

        /// <summary>池子里有词且气泡图在，才谈得上弹（缺件时整条不弹，而不是弹一张只有字的）</summary>
        public bool Ready => Lines.Count > 0 && Bubble is not null;

        private DialogAssets() { }

        public static DialogAssets Load()
        {
            var dir = Path.Combine(Program.ProgramResourceDirectory, @"image\dialog");
            var assets = new DialogAssets
            {
                Bubble = ReadPng(Path.Combine(dir, "bubble.png")),
                Crest = ReadPng(Path.Combine(dir, "crest.png")),
            };
            assets.BubbleMirrored = Flipped(assets.Bubble);
            assets.CrestMirrored = Flipped(assets.Crest);
            if (assets.Bubble is not null)
            {
                var box = DialogBubbleForm.InkOf(assets.Bubble);
                assets.Ink = new Rectangle(DialogBubbleForm.BubbleAt.X + box.X,
                                           DialogBubbleForm.BubbleAt.Y + box.Y, box.Width, box.Height);
            }
            assets.LoadLines(Path.Combine(Program.ProgramResourceDirectory, @"dialog\lines.tsv"));
            return assets;
        }

        private static Bitmap? Flipped(Bitmap? src)
        {
            if (src is null) return null;
            var copy = (Bitmap)src.Clone();
            copy.RotateFlip(RotateFlipType.RotateNoneFlipX);
            return copy;
        }

        /// <summary>
        /// 池子坏了就整条空着（气泡宁可不弹，也别弹半截或弹错人）。
        /// 字数那一列不采信，按正文实际长度算 —— 文件里那列是装机时写的，改过台词就会对不上。
        /// </summary>
        private void LoadLines(string path)
        {
            if (!File.Exists(path)) return;
            var lines = new List<DialogLine>();
            foreach (var raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                var cols = raw.Split('\t');
                if (cols.Length < 4 || cols[0] == "说话人") continue;
                var text = cols[3].Trim();
                if (text.Length == 0) continue;
                if (Speaker.Length == 0) Speaker = cols[0].Trim();
                lines.Add(new DialogLine(cols[1].Trim(), text));
            }
            Lines = lines;
        }

        /// <summary>
        /// 读进内存再放手：直接 new Bitmap(path) 会把文件锁住，重跑装机脚本就覆盖不了
        /// </summary>
        private static Bitmap? ReadPng(string path)
        {
            try
            {
                return new Bitmap(new MemoryStream(File.ReadAllBytes(path)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            Bubble?.Dispose();
            BubbleMirrored?.Dispose();
            Crest?.Dispose();
            CrestMirrored?.Dispose();
        }
    }
}
