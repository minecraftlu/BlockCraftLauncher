$ErrorActionPreference = 'Stop'
$service = Join-Path $PSScriptRoot '../src/BlockCraftLauncher.Wpf/Services/LauncherSearchCommand.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('blockcraft-search-command-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
try {
    $harness = Join-Path $directory 'Check.cs'
    @'
using System;
using BlockCraftLauncher.Wpf.Services;
class Check {
    static int count;
    static void Case(string input, LauncherSearchKind kind, bool install, string query,
                     string version = "", string loader = "", string instance = "", string source = "", string resourceType = "") {
        LauncherSearchCommand c;
        if (!LauncherSearchCommand.TryParse(input, out c) || c.Kind != kind || c.Install != install ||
            c.Query != query || c.GameVersion != version || c.Loader != loader ||
            c.InstanceName != instance || c.SourceName != source || c.ResourceType != resourceType)
            throw new Exception(input + " => " + (c == null ? "chat" : c.Kind + "|" + c.Install + "|" + c.Query +
                "|" + c.GameVersion + "|" + c.Loader + "|" + c.InstanceName + "|" + c.SourceName + "|" + c.Error));
        count++;
    }
    static void Reject(string input, bool ordinaryQuestion) {
        LauncherSearchCommand c;
        bool parsed = LauncherSearchCommand.TryParse(input, out c);
        if (ordinaryQuestion ? parsed : !parsed || c.Kind != LauncherSearchKind.Invalid || string.IsNullOrWhiteSpace(c.Error))
            throw new Exception("Unexpected interpretation: " + input);
        count++;
    }
    static void Main() {
        const LauncherSearchKind G = LauncherSearchKind.Game, M = LauncherSearchKind.Mod, R = LauncherSearchKind.ModRecommendation;
        Case("下载 Minecraft 1.21.1", G, true, "1.21.1", "1.21.1");
        Case("请帮我下载安装游戏 1.20.1 Forge", G, true, "1.20.1", "1.20.1", "Forge");
        Case("找 Minecraft 1.21.1", G, false, "1.21.1", "1.21.1");
        Case("install Minecraft 1.20.1 with Fabric", G, true, "1.20.1", "1.20.1", "Fabric");
        Case("search Minecraft versions", G, false, "");
        Case("下载最新正式版的我的世界", G, true, "latest");
        Case("搜索 24w14a", G, false, "24w14a", "24w14a");
        Case("帮我下载 Minecraft 1.21-pre1", G, true, "1.21-pre1", "1.21-pre1");
        Case("给当前实例装钠", M, true, "Sodium");
        Case("把钠装到当前实例", M, true, "Sodium");
        Case("帮我搜索并下载钠", M, true, "Sodium");
        Case("直接在启动器里搜索下载钠", M, true, "Sodium");
        Case("我想下载Minecraft 1.21.1", G, true, "1.21.1", "1.21.1");
        Case("给生存实例安装钠", M, true, "Sodium", "", "", "生存");
        Case("给实例生存安装钠", M, true, "Sodium", "", "", "生存");
        Case("下载模组 JEI 到实例「Creative Pack」", M, true, "JEI", "", "", "Creative Pack");
        Case("search mods JEI for instance Creative Pack", M, false, "JEI", "", "", "Creative Pack");
        Case("从 CurseForge 搜索模组 苹果皮", M, false, "AppleSkin", "", "", "", "CurseForge");
        Case("搜索模组 Sodium from Modrinth", M, false, "Sodium", "", "", "", "Modrinth");
        Case("安装 1.21.1 Fabric 模组 钠", M, true, "Sodium", "1.21.1", "Fabric");
        Case("搜索模组 Sodium for Fabric", M, false, "Sodium", "", "Fabric");
        Case("下载模组 Fabric API 1.21.1", M, true, "Fabric API", "1.21.1");
        Case("搜索模组 Forge Config API Port", M, false, "Forge Config API Port");
        Case("搜索模组 Minecraft Comes Alive", M, false, "Minecraft Comes Alive");
        Case("安装模组 Sodium Extra", M, true, "Sodium Extra");
        Case("安装 Fabric API", M, true, "Fabric API");
        Case("下载 JourneyMap", M, true, "JourneyMap");
        Case("安装 JEI", M, true, "JEI");
        Case("下载模组 优化大师", M, true, "优化大师");
        Case("安装模组「提升帧率」", M, true, "提升帧率");
        Case("让千问帮我找个提高帧率的模组", R, false, "提高帧率");
        Case("帮我找个能显示血量的模组", R, false, "能显示血量");
        Case("帮我推荐一个提升帧率的模组然后安装", R, true, "提升帧率");
        Case("帮我装一个提升帧率的模组", R, true, "提升帧率");
        Case("帮我下载一个提升帧率的模组", R, true, "提升帧率");
        Case("安装提升帧率的模组", R, true, "提升帧率");
        Case("给生存实例下个小地图模组", R, true, "小地图", "", "", "生存");
        Case("给我找个小地图模组", R, false, "小地图");
        Case("推荐个小地图模组并帮我安装到生存实例", R, true, "小地图", "", "", "生存");
        Case("install a mod that boosts FPS", R, true, "that boosts FPS");
        Case("find an inventory sorting mod", R, false, "inventory sorting");
        Case("recommend and install an adventure mod", R, true, "adventure");
        Case("I want to download an FPS mod", R, true, "FPS");
        const LauncherSearchKind A = LauncherSearchKind.Resource;
        Case("下载资源包 faithful-32x", A, true, "faithful-32x", resourceType: "resourcepack");
        Case("搜索资源包 faithful-32x", A, false, "faithful-32x", resourceType: "resourcepack");
        Case("下载光影 complementary-reimagined", A, true, "complementary-reimagined", resourceType: "shader");
        Case("给生存实例装材质包 Faithful 32x", A, true, "Faithful 32x", instance: "生存", resourceType: "resourcepack");
        Case("把光影包 BSL Shaders 装到当前实例", A, true, "BSL Shaders", resourceType: "shader");
        Case("下载光影包「MakeUp - Ultra Fast」", A, true, "MakeUp - Ultra Fast", resourceType: "shader");
        Case("下载 Modrinth 的整合包 Fabulously Optimized", A, true, "Fabulously Optimized", source: "Modrinth", resourceType: "modpack");
        Case("下载模组包 Fabulously Optimized", A, true, "Fabulously Optimized", resourceType: "modpack");
        Case("install the Minecraft resource pack Faithful 32x for 1.20.1", A, true, "Faithful 32x", "1.20.1", resourceType: "resourcepack");
        Case("find modpacks", A, false, "", resourceType: "modpack");
        Case("install 1.20.1 Fabric modpack Fabulously Optimized", A, true, "Fabulously Optimized", "1.20.1", "Fabric", resourceType: "modpack");
        Case("下载模组「Iris Shaders」", M, true, "Iris Shaders");
        Case("下载 Minecraft「1.21.1」", G, true, "1.21.1", "1.21.1");
        Case("下载整合包「Better MC」", A, true, "Better MC", resourceType: "modpack");
        Case("install modpack \"Better MC [FABRIC] BMC1\"", A, true, "Better MC [FABRIC] BMC1", resourceType: "modpack");
        Case("下载资源包『Faithless 1.20』", A, true, "Faithless 1.20", resourceType: "resourcepack");
        Case("安装模组「Forge Config API Port」", M, true, "Forge Config API Port");
        Case("安装资源包“Fabric and Forge 1.20” for 1.21.1 from Modrinth", A, true,
            "Fabric and Forge 1.20", "1.21.1", source: "Modrinth", resourceType: "resourcepack");
        Case("给实例「Fabric 1.21」安装 1.20.1 Forge 模组「Modrinth MC 1.19」", M, true,
            "Modrinth MC 1.19", "1.20.1", "Forge", "Fabric 1.21");
        Reject("安装模组「A」和「B」", false);
        Reject("安装光影包「https://example.net/a.zip」", false);
        Reject("安装资源包「bad.cmd」", false);
        Reject("给当前实例安装整合包 Fabulously Optimized", false);
        Reject("安装整合包 Fabulously Optimized 到当前实例", false);
        Reject("安装材质包 https://example.org/texture.zip", false);
        Reject("下载光影 A 和 B", false);
        Reject("别给我安装一个提升帧率的模组", true);
        Reject("安装一个提升帧率的模组大概要多久", true);
        Reject("安装一个提升帧率的模组会不会影响存档", true);
        Reject("find a mod but don't install it", true);
        Reject("怎样下载资源包？", true);
        Reject("怎么下载模组", true);
        Reject("搜索 Minecraft 版本失败怎么办", true);
        Reject("安装模组 Sodium 以后闪退了", true);
        Reject("有什么好用的优化模组？", true);
        Reject("不要安装模组 Sodium", true);
        Reject("what is Sodium?", true);
        Reject("搜索东京天气", true);
        Reject("下载 Minecraft 1.20.1 和 1.21.1", false);
        Reject("给生存实例安装钠到实例创造", false);
        Reject("下载安装模组 A 和 B", false);
        Reject("安装模组 https://example.com/file.jar", false);
        Reject("安装模组 A; shutdown", false);
        Reject("下载模组 Sodium 然后运行命令", false);
        Reject("下载游戏1.21.1，安装模组钠", false);
        Reject("给生存实例下载 Minecraft 1.21.1", false);
        Console.WriteLine("PASS: " + count + " launcher catalog command, filter and non-action cases.");
    }
}
'@ | Set-Content -LiteralPath $harness -Encoding UTF8
    $program = Join-Path $directory 'Check.exe'
    & $compiler /nologo /codepage:65001 "/out:$program" $service $harness
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile command checks' }
    & $program
    if ($LASTEXITCODE -ne 0) { throw 'Command checks failed' }
} finally {
    if (Test-Path (Join-Path $directory 'Check.exe')) { [IO.File]::Delete((Join-Path $directory 'Check.exe')) }
    if (Test-Path (Join-Path $directory 'Check.cs')) { [IO.File]::Delete((Join-Path $directory 'Check.cs')) }
    [IO.Directory]::Delete($directory, $false)
}
