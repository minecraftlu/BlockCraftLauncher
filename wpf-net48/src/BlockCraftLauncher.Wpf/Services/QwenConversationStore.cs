using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace BlockCraftLauncher.Wpf.Services
{
    [DataContract]
    internal sealed class QwenConversationTurn
    {
        [DataContract]
        sealed class Link
        {
            [DataMember] public int Index;
            [DataMember] public string Title;
            [DataMember] public string Url;
        }

        [DataMember] public string Question { get; private set; }
        [DataMember] public string Answer { get; private set; }
        [DataMember] internal string Id { get; private set; }
        [DataMember(EmitDefaultValue = false)] List<Link> links;

        public IReadOnlyList<QwenSearchSource> Sources
        {
            get
            {
                var sources = new List<QwenSearchSource>();
                if (links != null)
                    foreach (var link in links.Take(48))
                        if (link != null && link.Index > 0 && !string.IsNullOrEmpty(link.Title) &&
                            link.Title.Length <= 200 && link.Url?.Length <= 4096 &&
                            Uri.TryCreate(link.Url, UriKind.Absolute, out var url) && QwenSearchService.IsSafeSource(url))
                            sources.Add(new QwenSearchSource(link.Index, link.Title, url));
                return sources;
            }
        }

        internal QwenConversationTurn(string question, QwenSearchResult reply)
        {
            Id = Guid.NewGuid().ToString("N");
            Question = question;
            Answer = reply.Answer;
            if (reply.Sources != null && reply.Sources.Count > 0)
                links = reply.Sources.Where(s => s != null && QwenSearchService.IsSafeSource(s.Url))
                    .Take(48).Select(s => new Link { Index = s.Index, Title = s.Title, Url = s.Url.AbsoluteUri }).ToList();
        }

        internal bool IsValid => Guid.TryParseExact(Id, "N", out _) &&
            !string.IsNullOrWhiteSpace(Question) && Question.Length <= 65536 &&
            !string.IsNullOrWhiteSpace(Answer) && Answer.Length <= 512 * 1024;
    }

    [DataContract]
    internal sealed class QwenConversation
    {
        [DataMember] public string Id { get; private set; }
        [DataMember] public string Title { get; private set; }
        [DataMember] internal long Updated { get; private set; }
        [DataMember] internal List<QwenConversationTurn> Messages { get; private set; }
        public IReadOnlyList<QwenConversationTurn> Turns => Messages.AsReadOnly();
        public override string ToString() => Title;

        internal QwenConversation()
        {
            Id = Guid.NewGuid().ToString("N");
            Updated = DateTime.UtcNow.Ticks;
            Title = "新对话";
            Messages = new List<QwenConversationTurn>();
        }

        public IReadOnlyList<QwenChatTurn> BuildChatTurns(string question, bool regenerating = false)
        {
            var turns = new List<QwenChatTurn>();
            int count = Messages.Count - (regenerating && Messages.Count > 0 ? 1 : 0);
            foreach (var pair in Messages.Take(count))
            {
                turns.Add(new QwenChatTurn("user", pair.Question));
                turns.Add(new QwenChatTurn("assistant", pair.Answer));
            }
            turns.Add(new QwenChatTurn("user", question));
            return turns;
        }

        internal bool IsValid => Guid.TryParseExact(Id, "N", out _) && !string.IsNullOrWhiteSpace(Title) &&
            Title.Length <= 100 && Messages != null && Messages.Count <= 64 && Messages.All(t => t?.IsValid == true);

        internal void Append(QwenConversationTurn turn, bool replace)
        {
            if (Messages.Count == 0)
            {
                string title = turn.Question.Trim().Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 36)
                    title = title.Substring(0, char.IsHighSurrogate(title[35]) ? 35 : 36) + "…";
                Title = title;
            }
            if (replace) Messages[Messages.Count - 1] = turn;
            else Messages.Add(turn);
            if (Messages.Count > 64) Messages.RemoveAt(0);
            Updated = DateTime.UtcNow.Ticks;
        }
    }

    internal sealed class QwenConversationStore
    {
        const int MaxSessions = 32;
        const int MaxBytes = 4 * 1024 * 1024;
        [DataContract]
        sealed class Archive
        {
            [DataMember] public string Generation = Guid.NewGuid().ToString("N");
            [DataMember] public List<QwenConversation> Sessions = new List<QwenConversation>();
        }

        readonly string path, mutexName;
        readonly List<QwenConversation> sessions = new List<QwenConversation>();
        readonly HashSet<string> previouslySaved = new HashSet<string>(StringComparer.Ordinal);
        string generation;
        public bool HistoryEnabled => generation != null;
        public string PersistenceError { get; private set; } = "";
        public IReadOnlyList<QwenConversation> Sessions => sessions.AsReadOnly();
        public QwenConversation Current { get; private set; }

        public QwenConversationStore(string path = null)
        {
            this.path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockCraftLauncher", "qwen-conversations.json"));
            using (var sha = SHA256.Create())
                mutexName = @"Local\BlockCraft.Qwen.History." + BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(this.path.ToUpperInvariant()))).Replace("-", "");
            WithFile(() =>
            {
                var stored = Read();
                if (stored == null) return;
                generation = stored.Generation;
                sessions.AddRange(stored.Sessions.OrderByDescending(s => s.Updated));
                previouslySaved.UnionWith(sessions.Select(s => s.Id));
            });
            Current = sessions.FirstOrDefault() ?? NewSession();
        }

        public QwenConversation NewSession()
        {
            var empty = sessions.FirstOrDefault(s => s.Messages.Count == 0);
            Current = empty ?? new QwenConversation();
            sessions.Remove(Current);
            sessions.Insert(0, Current);
            TrimSessions(sessions);
            return Current;
        }

        public QwenConversation Select(string id)
        {
            var selected = sessions.FirstOrDefault(s => s.Id == id);
            if (selected != null) Current = selected;
            return Current;
        }

        public void CommitTurn(QwenConversation session, string question, QwenSearchResult reply, bool replaceLast = false)
        {
            if (session == null || !sessions.Contains(session) || string.IsNullOrWhiteSpace(question) ||
                string.IsNullOrWhiteSpace(reply?.Answer)) return;
            var previous = session.Messages.LastOrDefault();
            if (replaceLast && previous?.Question != question) return;
            var turn = new QwenConversationTurn(question, reply);
            if (!turn.IsValid) throw new InvalidOperationException("此消息超过对话记录长度限制。");
            session.Append(turn, replaceLast);
            sessions.Remove(session);
            sessions.Insert(0, session);
            if (!HistoryEnabled) return;
            WithFile(() =>
            {
                var stored = ReadActive();
                if (stored == null) return;
                var saved = stored.Sessions.FirstOrDefault(s => s.Id == session.Id);
                if (saved == null)
                {
                    // A second window can delete this conversation while an answer is in flight.
                    if (previouslySaved.Contains(session.Id))
                    {
                        PersistenceError = "这段对话的本机历史已删除，本次回答仅保留在当前窗口。";
                        return;
                    }
                    stored.Sessions.Insert(0, session);
                }
                else
                {
                    if (replaceLast && saved.Messages.LastOrDefault()?.Id != previous.Id)
                    {
                        PersistenceError = "其他窗口已更新这段对话，本次重新生成的回答仅保留在当前窗口。";
                        return;
                    }
                    saved.Append(turn, replaceLast);
                    stored.Sessions.Remove(saved);
                    stored.Sessions.Insert(0, saved);
                }
                Write(stored);
                previouslySaved.Add(session.Id);
            });
        }

        public void Delete(string id)
        {
            var session = sessions.FirstOrDefault(s => s.Id == id);
            if (session == null) return;
            if (HistoryEnabled && !WithFile(() =>
            {
                var stored = ReadActive();
                if (stored != null && stored.Sessions.RemoveAll(s => s.Id == id) > 0) Write(stored);
            })) return;
            sessions.Remove(session);
            if (session == Current) Current = sessions.FirstOrDefault() ?? NewSession();
        }

        public void Clear()
        {
            if ((HistoryEnabled || PersistenceError.Length != 0) && !WithFile(() =>
            {
                if (HistoryEnabled && File.Exists(path))
                {
                    var reset = new Archive();
                    Write(reset);
                    generation = reset.Generation;
                }
                else
                {
                    if (File.Exists(path)) File.Delete(path);
                    generation = null;
                }
            })) return;
            previouslySaved.Clear();
            sessions.Clear();
            NewSession();
        }

        public void SetHistoryEnabled(bool enabled)
        {
            if (enabled == HistoryEnabled && PersistenceError.Length == 0) return;
            WithFile(() =>
            {
                if (!enabled)
                {
                    if (File.Exists(path)) File.Delete(path);
                    generation = null;
                    previouslySaved.Clear();
                    return;
                }
                var stored = Read() ?? new Archive();
                foreach (var session in sessions.Where(s => s.Messages.Count > 0))
                    if (!stored.Sessions.Any(s => s.Id == session.Id) && !previouslySaved.Contains(session.Id))
                        stored.Sessions.Add(session);
                Write(stored);
                generation = stored.Generation;
                previouslySaved.UnionWith(stored.Sessions.Select(s => s.Id));
            });
        }

        Archive ReadActive()
        {
            var stored = Read();
            if (stored != null && stored.Generation == generation) return stored;
            // A clear or disable in another launcher must take precedence over stale in-flight writes.
            previouslySaved.UnionWith(sessions.Select(s => s.Id));
            generation = stored?.Generation;
            PersistenceError = "本机历史已在其他窗口更改，本次操作仅对当前窗口生效。";
            return null;
        }

        bool WithFile(Action action)
        {
            PersistenceError = "";
            try
            {
                using (var mutex = new Mutex(false, mutexName))
                {
                    try
                    {
                        if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                            throw new IOException("对话历史正在由其他窗口使用。");
                    }
                    catch (AbandonedMutexException) { }
                    try { action(); }
                    finally { mutex.ReleaseMutex(); }
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                ex is SecurityException || ex is SerializationException || ex is XmlException)
            {
                PersistenceError = "无法读写本机对话历史，请检查文件或文件夹权限后重试。";
                return false;
            }
        }

        Archive Read()
        {
            if (!File.Exists(path)) return null;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (file.Length == 0 || file.Length > MaxBytes) throw new InvalidDataException();
                var archive = (Archive)new DataContractJsonSerializer(typeof(Archive)).ReadObject(file);
                if (archive?.Sessions == null || archive.Sessions.Count > MaxSessions || !Guid.TryParseExact(archive.Generation, "N", out _) ||
                    archive.Sessions.Any(s => s?.IsValid != true) || archive.Sessions.Select(s => s.Id).Distinct().Count() != archive.Sessions.Count)
                    throw new InvalidDataException();
                return archive;
            }
        }

        static void TrimSessions(List<QwenConversation> list)
        {
            if (list.Count > MaxSessions) list.RemoveRange(MaxSessions, list.Count - MaxSessions);
        }

        void Write(Archive archive)
        {
            archive.Sessions = archive.Sessions.OrderByDescending(s => s.Updated).ToList();
            TrimSessions(archive.Sessions);
            // Work on copies so pruning an oversized archive never changes an open conversation.
            var serializer = new DataContractJsonSerializer(typeof(Archive));
            using (var buffer = new MemoryStream())
            {
                while (true)
                {
                    buffer.SetLength(0);
                    serializer.WriteObject(buffer, archive);
                    if (buffer.Length <= MaxBytes) break;
                    if (archive.Sessions.Count > 1) archive.Sessions.RemoveAt(archive.Sessions.Count - 1);
                    else
                    {
                        buffer.Position = 0;
                        archive = (Archive)serializer.ReadObject(buffer);
                        var turns = archive.Sessions[0].Messages;
                        if (turns.Count <= 1) throw new InvalidDataException();
                        turns.RemoveRange(0, Math.Max(1, turns.Count / 4));
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        buffer.Position = 0;
                        buffer.CopyTo(file);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
