using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// No HTTP, account tokens, Minecraft or user data. Exercise the exact
    /// production release selector and package validators with fake releases.
    /// </summary>
    internal static class LauncherAutoUpdateSanityChecks
    {
        static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("自动更新安全自检失败：" + message);
        }

        static string Asset(string tag, string name) =>
            "https://github.com/minecraftlu/BlockCraftLauncher/releases/download/" +
            tag + "/" + name;

        static object Release(string tag, bool prerelease, bool draft,
            string name, bool checksum = true, bool evil = false)
        {
            var files = new List<object> {
                new { name, size = 2100000, browser_download_url =
                    evil ? "https://evil.example/" + name : Asset(tag, name) }
            };
            if (checksum)
                files.Add(new { name = "RELEASE-SHA256SUMS.txt", size = 240,
                    browser_download_url = Asset(tag, "RELEASE-SHA256SUMS.txt") });
            return new {
                tag_name = tag, prerelease, draft, body = "Known safe release",
                html_url = "https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/" + tag,
                assets = files
            };
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static void Run()
        {
            LauncherRelaunchSignal.RunOfflineSelfTest();
            Require(LauncherAutoUpdateService.CompareTags("v0.9.29-wpf-rc.2",
                "v0.9.29-wpf-rc.1") > 0, "同版本的新 RC 能升级");
            Require(LauncherAutoUpdateService.CompareTags("v0.9.29",
                "v0.9.29-wpf-rc.6") > 0, "正式版高于同版本 RC");
            Require(LauncherAutoUpdateService.CompareTags("v0.9.28",
                "v0.9.29-wpf-rc.1") < 0, "拒绝降级");
            var serializer = new JavaScriptSerializer();
            const string zip29 = "BlockCraftLauncher-v0.9.29-WPF-net48-Alpha-Windows-x64.zip";
            const string exe29 = "BlockCraftLauncher-v0.9.29-WPF-OneFile-Windows-x64.exe";
            const string stableZip = "BlockCraftLauncher-v0.9.29-WPF-net48-Windows-x64.zip";
            var fixture = serializer.Serialize(new object[] {
                Release("v99.0.0", false, true,
                    "BlockCraftLauncher-v99.0.0-WPF-OneFile-Windows-x64.exe"),
                Release("v0.9.99-wpf-rc.9", true, false,
                    "BlockCraftLauncher-v0.9.99-WPF-OneFile-Windows-x64.exe", true, true),
                Release("netease-roomlab-stage9-preview", true, false, "BlockCraftRoomStage9.exe"),
                Release("v0.9.32-wpf-rc.1", true, false,
                    "BlockCraftLauncher-v0.9.32-WPF-OneFile-Windows-x64.exe", false),
                Release("v0.9.30", false, false,
                    "BlockCraftLauncher-v0.9.30-WPF-OneFile-Windows-x64.exe", false),
                Release("v0.9.29-wpf-rc.1", true, false, exe29),
                Release("v0.9.29-wpf-rc.1", true, false, zip29),
                Release("v0.9.29", false, false, stableZip)
            });
            var rcOne = LauncherAutoUpdateService.SelectOffer(fixture, "preview",
                "onefile", "v0.9.28-wpf-rc.1");
            Require(rcOne != null && rcOne.Tag == "v0.9.29-wpf-rc.1" &&
                rcOne.AssetName == exe29 && rcOne.Mode == "onefile",
                "预览通道正确寻找单文件 EXE");
            var rcZip = LauncherAutoUpdateService.SelectOffer(fixture, "preview",
                "zip", "v0.9.28-wpf-rc.1");
            Require(rcZip != null && rcZip.AssetName == zip29,
                "预览通道正确寻找 Alpha ZIP");
            var stable = LauncherAutoUpdateService.SelectOffer(fixture, "stable",
                "zip", "v0.9.28-wpf-rc.1");
            Require(stable != null && stable.Tag == "v0.9.29" &&
                stable.AssetName == stableZip, "稳定版不选测试包");
            Require(LauncherAutoUpdateService.SelectOffer(fixture, "preview",
                "onefile", "v0.9.29-wpf-rc.1") == null,
                "已经是当前 RC 不重复推送");
            Require(LauncherAutoUpdateService.SelectOffer(fixture, "stable",
                "zip", "v0.9.29") == null, "已是正式最新版不重复通知");
            Require(LauncherAutoUpdateService.SelectOffer("[]", "stable",
                "onefile") == null, "无正式发布时显示空状态");

            string sample = new string('a', 64) + "  " + exe29;
            Require(LauncherAutoUpdateService.ChecksumFromManifest(sample, exe29) ==
                new string('a', 64), "严格匹配 SHA-256 清单的原始文件名");
            string workRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates");
            string validStage = Path.Combine(workRoot, "staged-" + new string('a', 32));
            string validRunner = Path.Combine(workRoot, "runner-" + new string('b', 32));
            Require(LauncherAutoUpdateService.IsOwnedUpdateWorkFolder(workRoot, validStage) &&
                LauncherAutoUpdateService.IsOwnedUpdateWorkFolder(workRoot, validRunner),
                "清理器必须只认可程序专用的更新暂存目录与更新助手目录");
            foreach (string unsafePath in new[] {
                Path.Combine(workRoot, "rollback-" + new string('c', 32)),
                Path.Combine(workRoot, "Minecraft"),
                Path.Combine(validStage, "saves"),
                Path.Combine(workRoot, "runner-OTHER"),
                Path.Combine(Path.GetTempPath(), "staged-" + new string('d', 32))
            })
                Require(!LauncherAutoUpdateService.IsOwnedUpdateWorkFolder(workRoot, unsafePath),
                    "清理器不得扫描 Minecraft、用户目录或回滚资料：" + unsafePath);

            bool rejected = false;
            try { LauncherAutoUpdateService.ChecksumFromManifest(sample + "\n" + sample, exe29); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "清单中的重复文件必须拒绝");
            rejected = false;
            try { LauncherAutoUpdateService.ChecksumFromManifest(sample, zip29); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "缺少 SHA-256 不得下载并安装");

            string folder = Path.Combine(Path.GetTempPath(), "BlockCraft-Update-Check-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string file = Path.Combine(folder, exe29);
                byte[] body = Encoding.ASCII.GetBytes("Known test data");
                File.WriteAllBytes(file, body);
                LauncherAutoUpdateService.VerifyFile(file, Hash(body));
                rejected = false;
                try { LauncherAutoUpdateService.VerifyFile(file, new string('a', 64)); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "文件散列不匹配阻止更新");

                string zipPath = Path.Combine(folder, "safe.zip");
                string baseName = "BlockCraftLauncher-v0.9.29-WPF-net48-Alpha-Windows-x64";
                byte[] fake = Encoding.ASCII.GetBytes("test EXE/DLL");
                string[] names = { "BlockCraftLauncher.Wpf.exe",
                    "BlockCraftLauncher.Updater.exe", "CmlLib.Core.dll",
                    "CmlLib.Core.Commons.dll", "Microsoft.Web.WebView2.Core.dll",
                    "System.Memory.dll", "System.Text.Json.dll" };
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var name in names)
                    {
                        var entry = zip.CreateEntry(baseName + "/" + name);
                        using (var stream = entry.Open()) stream.Write(fake, 0, fake.Length);
                    }
                    var index = zip.CreateEntry(baseName + "/SHA256SUMS.txt");
                    using (var writer = new StreamWriter(index.Open(), Encoding.ASCII))
                        foreach (var name in names)
                            writer.WriteLine(Hash(fake) + "  " + name);
                    var readme = zip.CreateEntry(baseName + "/README-ALPHA.txt");
                    using (var writer = new StreamWriter(readme.Open(), Encoding.ASCII))
                        writer.WriteLine("No Minecraft files or secrets");
                }
                string extracted = LauncherAutoUpdateService.ExtractCheckedZip(
                    zipPath, Path.Combine(folder, "extracted"));
                LauncherAutoUpdateService.VerifyZipManifest(extracted);

                var malicious = Path.Combine(folder, "bad.zip");
                using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        string name = i == 3 ? "package/../../escape.txt" :
                            "package/subfolder/file" + i + ".txt";
                        var entry = zip.CreateEntry(name);
                        using (var writer = new StreamWriter(entry.Open()))
                            writer.Write("Unsafe path test");
                    }
                }
                rejected = false;
                try {
                    LauncherAutoUpdateService.ExtractCheckedZip(malicious,
                        Path.Combine(folder, "bad-output"));
                }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && !File.Exists(Path.Combine(folder, "escape.txt")),
                    "不能通过 ZIP 目录穿越覆盖任意文件");

                // Strictly within a disposable LocalAppData staged folder:
                // direct-open must refuse tampered downloads or arbitrary EXEs.
                string localStage = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlockCraftLauncher", "updates", "staged-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(localStage);
                try
                {
                    var onePath = Path.Combine(localStage, exe29);
                    byte[] payload = Encoding.ASCII.GetBytes("Verified onefile placeholder");
                    File.WriteAllBytes(onePath, payload);
                    var preparedOne = new PreparedLauncherUpdate
                    {
                        Offer = rcOne, StagedPath = onePath,
                        Sha256 = Hash(payload), TargetPath = Path.Combine(folder, "old.exe")
                    };
                    Require(LauncherAutoUpdateService.DirectPreviewExecutable(preparedOne) == onePath,
                        "已校验的新版可直接运行，旧安装位置保持不变");
                    // Download-only must NEVER silently install. Only an
                    // explicit opt-in and the exact currently selected
                    // verified package may install when the window closes.
                    var automatic = new LauncherConfig
                    {
                        AutoCheckUpdates = true, AutoDownloadUpdates = true,
                        AutoInstallUpdatesOnExit = true
                    };
                    Require(LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                        automatic, preparedOne, rcOne, false, false),
                        "用户授权的已准备包应允许退出时原位替换");
                    automatic.AutoInstallUpdatesOnExit = false;
                    Require(!LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                        automatic, preparedOne, rcOne, false, false),
                        "默认禁止未经授权的后台安装");
                    automatic.AutoInstallUpdatesOnExit = true;
                    foreach (var block in new[] {
                        new { busy = true, suppress = false },
                        new { busy = false, suppress = true }
                    })
                        Require(!LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                            automatic, preparedOne, rcOne, block.busy, block.suppress),
                            "下载中、窗口自检和临时试用不应触发自动替换");
                    Require(!LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                        automatic, preparedOne, stable, false, false),
                        "候选版本变更后不得自动安装旧暂存包");
                    automatic.AutoDownloadUpdates = false;
                    Require(!LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                        automatic, preparedOne, rcOne, false, false),
                        "未自动下载时禁止自动安装");
                    automatic.AutoDownloadUpdates = true;
                    automatic.AutoCheckUpdates = false;
                    Require(!LauncherAutoUpdateService.ShouldAutoInstallOnExit(
                        automatic, preparedOne, rcOne, false, false),
                        "未启用检查时禁止自动安装");


                    File.AppendAllText(onePath, "tamper");
                    rejected = false;
                    try { LauncherAutoUpdateService.DirectPreviewExecutable(preparedOne); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "直接打开必须拒绝篡改后的新版 EXE");

                    var outside = new PreparedLauncherUpdate
                    {
                        Offer = rcOne, StagedPath = file,
                        Sha256 = Hash(File.ReadAllBytes(file)), TargetPath = ""
                    };
                    rejected = false;
                    try { LauncherAutoUpdateService.DirectPreviewExecutable(outside); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "直接打开不能执行任意本地文件路径");

                    string copiedZip = Path.Combine(localStage, zip29);
                    File.Copy(zipPath, copiedZip);
                    string extractedZip = LauncherAutoUpdateService.ExtractCheckedZip(
                        copiedZip, Path.Combine(localStage, "unpacked"));
                    var preparedZip = new PreparedLauncherUpdate
                    {
                        Offer = rcZip, StagedPath = extractedZip,
                        Sha256 = Hash(File.ReadAllBytes(copiedZip)), TargetPath = folder
                    };
                    string directExe = LauncherAutoUpdateService.DirectPreviewExecutable(preparedZip);
                    Require(directExe.EndsWith("BlockCraftLauncher.Wpf.exe",
                        StringComparison.OrdinalIgnoreCase) && File.Exists(directExe),
                        "完整 ZIP 必须从验证过的新版目录直接打开");
                    string unexpectedDll = Path.Combine(extractedZip, "unexpected.dll");
                    File.WriteAllText(unexpectedDll, "fake", Encoding.ASCII);
                    rejected = false;
                    try { LauncherAutoUpdateService.DirectPreviewExecutable(preparedZip); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "解压目录有多余可执行文件时拒绝直接打开");
                    File.Delete(unexpectedDll);
                    File.AppendAllText(directExe, "tamper");
                    rejected = false;
                    try { LauncherAutoUpdateService.DirectPreviewExecutable(preparedZip); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "原始 ZIP 未变化也不能执行被篡改的解压 DLL/EXE");
                    File.AppendAllText(copiedZip, "tamper");
                    rejected = false;
                    try { LauncherAutoUpdateService.DirectPreviewExecutable(preparedZip); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "ZIP 变动后不能再直接打开新版");
                }
                finally
                {
                    try { Directory.Delete(localStage, true); }
                    catch (IOException) { }
                }

                string configPath = Path.Combine(folder, "settings.wpf-preview.json");
                var store = new SettingsStore(configPath);
                var settings = new LauncherConfig {
                    AutoCheckUpdates = true, AutoDownloadUpdates = true,
                    AutoInstallUpdatesOnExit = true,
                    UpdateChannel = "preview",
                    LastUpdateCheckUtc = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)
                };
                store.Save(settings);
                var restored = store.Load();
                Require(restored.AutoCheckUpdates && restored.AutoDownloadUpdates &&
                    restored.AutoInstallUpdatesOnExit && restored.UpdateChannel == "preview" &&
                    restored.LastUpdateCheckUtc.Year == 2026,
                    "自动检查、通道与时间持久化");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
