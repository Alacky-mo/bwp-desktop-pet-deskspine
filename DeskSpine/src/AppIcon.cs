namespace DeskSpine
{
    /// <summary>
    /// 应用图标那颗：本机是 res\image\appicon_buzhihuo.ico —— 从不知火立绘打成白+红两色剪影再坐进深红圆盘，
    /// 母图和命令都记在 bwp-assets/app_icon_mark.py（--emit=4）+ png_to_ico.py。
    /// 那是网易/画师素材的版权衍生件，按裁定不进版本库（deskspine/.gitignore 挡着），所以干净克隆上没这个文件 ——
    /// 那种情况下退回调用方给的占位图（resx 里那两枚上游剪影），行为和换图前一模一样。
    /// 只在第一次调用时探一次文件，之后复用同一个 Icon：托盘和两张标题栏加起来要取三回，别每次重开句柄。
    /// </summary>
    internal static class AppIcon
    {
        private static Icon? derived;
        private static bool probed;

        /// <param name="resDir">
        /// 基准目录，默认就是装机那个 res。留成参数只为一件事：判据进程是 bwp-animator-test，
        /// 它跑起来时 <see cref="Program.ProgramResourceDirectory"/> 指到测试自己的 bin 里去了，
        /// 不传就没法验「那张图真被取上了」这一腿（和无头判据里比图标路径用的是同一个理由）。
        /// </param>
        internal static Icon? Resolve(Icon? placeholder, string? resDir = null)
        {
            if (!probed)
            {
                probed = true;
                try
                {
                    string path = Path.Combine(resDir ?? Program.ProgramResourceDirectory, @"image\appicon_buzhihuo.ico");
                    if (File.Exists(path)) derived = new Icon(path);
                }
                catch
                {
                    derived = null;
                }
            }
            return derived ?? placeholder;
        }
    }
}
