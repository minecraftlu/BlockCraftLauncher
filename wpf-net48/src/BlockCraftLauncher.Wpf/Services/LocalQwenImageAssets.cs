using System;
using System.Collections.Generic;
using System.IO;

namespace BlockCraftLauncher.Wpf.Services
{
    // Asset digests and exact lengths come from the release API (executables)
    // and the git-lfs "oid sha256" and "size" of each pinned Hugging Face file.
    internal sealed class LocalQwenImageAsset
    {
        internal readonly string PathName, Sha256;
        internal readonly Uri Source;
        internal readonly long Bytes;

        LocalQwenImageAsset(string path, string source, long bytes, string digest)
        { PathName = path; Source = new Uri(source); Bytes = bytes; Sha256 = digest; }

        const string Generate = "https://huggingface.co/Comfy-Org/Qwen-Image/resolve/ad4cf4a39bcbda5be137c74bcce84c42fcd2b11d/split_files/";
        const string Edit = "https://huggingface.co/Comfy-Org/Qwen-Image-Edit/resolve/c086a97710d5d03491dc4ff0bd4a51fc3b49fae2/split_files/";

        internal static readonly LocalQwenImageAsset Portable = new LocalQwenImageAsset(
            "downloads/ComfyUI-0.46.0-cu126.7z",
            "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.46.0/ComfyUI_windows_portable_nvidia_cu126.7z",
            1976423950L, "06ef2069702a7d7db7262e29a70ec524207f86c7bc4212f9dfcb7c977bf3e9ed");
        internal static readonly LocalQwenImageAsset Extractor = new LocalQwenImageAsset(
            "downloads/7zr-26.06.exe", "https://github.com/ip7z/7zip/releases/download/26.06/7zr.exe",
            600064L, "e80db2dad715a6c5adabe292d31ed54e11f4e9b99ca349827b50573b175891bc");

        static LocalQwenImageAsset Weight(string repo, string path, long size, string digest) =>
            new LocalQwenImageAsset("models/" + path, repo + path, size, digest);

        internal static readonly LocalQwenImageAsset Encoder = Weight(Generate, "text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors",
            9384670680L, "cb5636d852a0ea6a9075ab1bef496c0db7aef13c02350571e388aea959c5c0b4");
        internal static readonly LocalQwenImageAsset Vae = Weight(Generate, "vae/qwen_image_vae.safetensors",
            253806246L, "a70580f0213e67967ee9c95f05bb400e8fb08307e017a924bf3441223e023d1f");
        internal static readonly LocalQwenImageAsset Generator = Weight(Generate, "diffusion_models/qwen_image_2512_fp8_e4m3fn.safetensors",
            20430679144L, "5dc80554d5d83390046a2f4a94ece06afb7700bf7b0aaf8bde9769793875876b");
        internal static readonly LocalQwenImageAsset Editor = Weight(Edit, "diffusion_models/qwen_image_edit_2511_fp8mixed.safetensors",
            20533762817L, "c9fdc158e46d3b61ef75f21ae866ca2fe808bf4a53643120d1c1e87c19280a4e");

        internal static IEnumerable<LocalQwenImageAsset> Weights(bool edit)
        { yield return Vae; yield return Encoder; yield return edit ? Editor : Generator; }

        internal string FilePath(string directory) => Path.Combine(directory, PathName.Replace('/', Path.DirectorySeparatorChar));
    }
}
