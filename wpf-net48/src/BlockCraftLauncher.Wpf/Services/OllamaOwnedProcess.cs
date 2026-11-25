using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace BlockCraftLauncher.Wpf.Services
{
    // A job closes the Ollama server and its inference runners even if the launcher crashes.
    internal sealed class OllamaOwnedProcess : IDisposable
    {
        readonly ChildProcessJob job;
        internal Uri Address { get; }
        internal string Root { get; }
        internal bool IsRunning => !job.Process.HasExited;

        OllamaOwnedProcess(ChildProcessJob job, Uri address, string root)
        {
            this.job = job;
            Address = address;
            Root = root;
        }

        internal static OllamaOwnedProcess Start(string executable, string root, string proxy)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
            string host = "127.0.0.1:" + port;
            var start = new ProcessStartInfo(executable, "serve") {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true
            };
            var variables = start.EnvironmentVariables;
            // Ollama reads its registry identity from the Windows user profile even in local mode.
            // Give this managed server its own identity, apart from an existing Ollama installation.
            foreach (string file in new[] { "id_ed25519", "id_ed25519.pub" })
                VerifiedModelDownload.RejectReparsePoints(Path.Combine(root, ".ollama", file));
            variables["USERPROFILE"] = root;
            variables["OLLAMA_HOST"] = host;
            variables["OLLAMA_MODELS"] = Path.Combine(root, "models");
            variables["OLLAMA_NO_CLOUD"] = "1";
            variables["OLLAMA_NOPRUNE"] = "1";
            variables.Remove("OLLAMA_ORIGINS");
            if (!string.IsNullOrWhiteSpace(proxy)) variables["HTTPS_PROXY"] = proxy;
            return new OllamaOwnedProcess(ChildProcessJob.Start(start), new Uri("http://" + host + "/"), root);
        }

        public void Dispose() => job.Dispose();
    }
}
