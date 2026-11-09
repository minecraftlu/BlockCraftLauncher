using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Stage 12.8 cross-checks two explicitly supplied China Edition client
    /// build strings against PUBLIC reference candidates only. The screenshot
    /// is a Windows client, not proof of the phone host's exact version.
    /// No lobby or actual game protocol field is inferred or transmitted.
    /// </summary>
    public static class Stage128ClientBuildReference
    {
        private const string Candidate =
            "STAGE12_8_CHINA_310_REFERENCE_CANDIDATES_UNVERIFIED_GAME_NOT_JOINED";
        private const string Unknown =
            "STAGE12_8_CHINA_BUILD_REFERENCE_UNKNOWN_GAME_NOT_JOINED";

        public static bool IsAvailable
        {
            get { return typeof(Stage128ClientBuildReference).Assembly.GetManifestResourceInfo(
                Stage12OfflineBridge.WorkerResource) != null; }
        }

        public static bool TryNormalizeBuild(string build, out string normalized)
        {
            normalized = null;
            if (build == null || build.Length < 11 || build.Length > 24) return false;
            string[] parts = build.Split('.');
            if (parts.Length != 4 || parts[3].Length != 6 || parts[2].Length > 3)
                return false;
            for (int i=0; i<parts.Length; i++)
            {
                if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0'))
                    return false;
                foreach (char ch in parts[i]) if (ch<'0' || ch>'9') return false;
            }
            int major, minor, patch;
            if (!Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                    out major) ||
                !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                    out minor) ||
                !Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                    out patch) || patch > 999)
                return false;
            if (major != 3 || minor != 10) return false;
            normalized = build;
            return true;
        }

        public static string Classify(string gameBuild, string patchBuild,
            int exitCode, string output)
        {
            string game, patch;
            if (!TryNormalizeBuild(gameBuild,out game) || !TryNormalizeBuild(patchBuild,out patch))
                return "STAGE12_8_VERSION_INPUT_INVALID：请填写网易 3.10 客户端的完整版本号和补丁版本号。"
                    + "不接受房间号、协议号或账号信息。";
            string token = (output ?? "").TrimEnd('\r','\n');
            if (exitCode==0 && String.Equals(token,Candidate,StringComparison.Ordinal))
                return "STAGE12_8_CHINA_310_CANDIDATES_897_898_UNVERIFIED_GAME_NOT_JOINED："
                    + "输入版本属于网易 3.10 客户端系列；公开资料将该系列与 Bedrock 1.21.130 联系起来。"
                    + "标准协议 897（资料库版本表）和 898（正式版协议表）存在资料差异，"
                    + "它们只是候选，不是当前网易手机房间的实测协议。"
                    + "未找到已确认的 897/898 网易专用游戏编解码器；"
                    + "手机房主版本、游戏认证和进入世界均未验证。";
            if (exitCode==0 && String.Equals(token,Unknown,StringComparison.Ordinal))
                return "STAGE12_8_BUILD_REFERENCE_UNKNOWN_GAME_NOT_JOINED："
                    + "当前公开参考没有这组客户端版本的可靠映射；无法判断游戏协议。";
            return "STAGE12_8_BUILD_REFERENCE_FAILED_GAME_NOT_JOINED："
                + "本机版本资料检查失败，没有进行网易游戏登录。";
        }

        public static async Task<string> RunAsync(string gameBuild,string patchBuild,
            CancellationToken cancellationToken)
        {
            string game, patch;
            if (!TryNormalizeBuild(gameBuild,out game) || !TryNormalizeBuild(patchBuild,out patch))
                return Classify(gameBuild,patchBuild,-1,"");
            if (!IsAvailable)
                return "STAGE12_8_BUILD_REFERENCE_UNAVAILABLE：缺少内置离线版本检查组件。";
            if (cancellationToken.IsCancellationRequested)
                return "STAGE12_8_BUILD_REFERENCE_CANCELLED：检查已取消。";
            string directory=Path.Combine(Path.GetTempPath(),"BlockCraft-Stage128-"
                +Guid.NewGuid().ToString("N"));
            Process process=null;
            try
            {
                Directory.CreateDirectory(directory);
                string executable=Path.Combine(directory,"Stage12Worker.exe");
                using (Stream resource=Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(Stage12OfflineBridge.WorkerResource))
                {
                    if (resource==null)return Classify(game,patch,-1,"");
                    using (var file=new FileStream(executable,FileMode.CreateNew,
                        FileAccess.Write,FileShare.None))
                        resource.CopyTo(file);
                }
                process=new Process {StartInfo=new ProcessStartInfo {
                    FileName=executable,
                    Arguments="-offline-china-build-reference "+game+" "+patch,
                    WorkingDirectory=directory,UseShellExecute=false,
                    CreateNoWindow=true,RedirectStandardOutput=true,
                    RedirectStandardError=false
                }};
                if (!process.Start())return Classify(game,patch,-1,"");
                Process running=process;
                using (cancellationToken.Register(()=>Stop(running)))
                {
                    Task<string> read=running.StandardOutput.ReadToEndAsync();
                    bool exited=await Task.Run(()=>running.WaitForExit(10000));
                    if (!exited)
                    {
                        Stop(running);
                        return "STAGE12_8_BUILD_REFERENCE_TIMEOUT_GAME_NOT_JOINED：本机检查超时。";
                    }
                    if (cancellationToken.IsCancellationRequested)
                        return "STAGE12_8_BUILD_REFERENCE_CANCELLED：检查已取消。";
                    string output=await read;
                    if (output.Length>256)return Classify(game,patch,-1,"");
                    return Classify(game,patch,running.ExitCode,output);
                }
            }
            catch (Exception)
            {
                return Classify(game,patch,-1,"");
            }
            finally
            {
                if (process!=null){Stop(process);process.Dispose();}
                try{if(Directory.Exists(directory))Directory.Delete(directory,true);}
                catch(Exception){}
            }
        }

        private static void Stop(Process process)
        {
            try{if(process!=null && !process.HasExited)process.Kill();}
            catch(InvalidOperationException){}
            catch(System.ComponentModel.Win32Exception){}
        }
    }
}
