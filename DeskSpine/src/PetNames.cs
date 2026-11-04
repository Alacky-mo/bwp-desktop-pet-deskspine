using System;
using System.Collections.Generic;
using System.IO;

namespace DeskSpine
{
    /// <summary>
    /// 骨骼文件名到中文角色名的对照表, 切换角色菜单靠它显示中文名。
    /// 表里没有的就退回文件名, 不会瞎猜。
    /// </summary>
    public static class PetNames
    {
        private static readonly Dictionary<string, string> ChineseNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buzhihuo_1127"] = "不知火",
            ["qianji_0552"] = "千姬",
            ["maozhanggui_sp_yihua_3754"] = "猫掌柜焕宴",
            ["xieji_0481"] = "蟹姬",
        };

        public static string Display(string? skelPath)
        {
            if (string.IsNullOrEmpty(skelPath)) return "(空)";
            var key = Path.GetFileNameWithoutExtension(skelPath);
            return ChineseNames.TryGetValue(key, out var name) ? name : key;
        }
    }
}
