using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskSpine
{
    /// <summary>
    /// 首启把包内 res\spine 里的骨架登记进名册。
    /// release 附件带着素材发到别人机器上，可配置只认 %LOCALAPPDATA%\DeskSpine\config.json
    /// （骨骼路径一律 Path.GetFullPath 解析，见 Spine.cs），所以光把文件躺在那儿程序是看不见的。
    /// 只在「还没有配置」的时候做一次，路径在用户自己那台机器上算，之后他怎么改都不回来覆盖。
    /// </summary>
    public static class BundledRoster
    {
        public static string BundleRoot => Path.Combine(AppContext.BaseDirectory, "res", "spine");

        /// <summary>找到那副骨架：目录里有 .skel 且**同名 .atlas 在场**才算，缺页图集登记上也是起不来。</summary>
        public static string? FindUsableSkel(string dir)
        {
            foreach (var skel in Directory.GetFiles(dir, "*.skel").OrderBy(p => p, StringComparer.Ordinal))
                if (File.Exists(Path.ChangeExtension(skel, ".atlas")))
                    return Path.GetFullPath(skel);
            return null;
        }

        /// <summary>返回登记了几只。0 表示没东西可登记（或已经有配置了，没动它）。</summary>
        public static int Seed(string bundleRoot, string configPath)
        {
            if (File.Exists(configPath)) return 0;
            if (!Directory.Exists(bundleRoot)) return 0;

            var paths = new List<string>();
            foreach (var dir in Directory.GetDirectories(bundleRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var skel = FindUsableSkel(dir);
                if (skel == null) continue;
                paths.Add(skel);
                if (paths.Count == SpineConfig.SlotCount) break;
            }
            if (paths.Count == 0) return 0;

            var cfg = new Config();
            for (var i = 0; i < paths.Count; i++) cfg.SpineConfig.SetSkelPath(i, paths[i]);
            cfg.SpineConfig.SpineVersion = Spine.SpineVersion.V36;   // 包内这几只全是 3.6.52
            cfg.SpineConfig.InteractMode = TinyEngine.AnimatorType.BwpHero;
            cfg.SpineConfig.ActiveSlot = 0;
            cfg.BasicConfig.SpineUsePMA = true;                      // ASTC 那批是按预乘alpha出的图
            cfg.SystemConfig.Visible = true;
            cfg.Save(configPath);
            return paths.Count;
        }

        public static void SeedIfFirstRun() => Seed(BundleRoot, Program.ProgramConfigPath);
    }
}
