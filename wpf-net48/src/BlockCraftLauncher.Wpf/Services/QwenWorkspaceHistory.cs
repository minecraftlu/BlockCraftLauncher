using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BlockCraftLauncher.Wpf.Services
{
    // Keep each archive at its original path. Older launcher windows can still
    // append or delete using the archive's existing lock and generation checks.
    internal sealed class QwenWorkspaceHistory
    {
        readonly List<QwenConversationStore> stores = new List<QwenConversationStore>();
        QwenConversationStore active;

        public QwenWorkspaceHistory(string path, string legacyLocalPath)
        {
            stores.Add(new QwenConversationStore(path));
            if (!string.IsNullOrEmpty(legacyLocalPath) && File.Exists(legacyLocalPath))
                stores.Add(new QwenConversationStore(legacyLocalPath));
            SelectMostRecent();
        }

        public QwenConversation Current => active.Current;
        public bool HistoryEnabled => active.HistoryEnabled;
        public string PersistenceError => stores.Select(store => store.PersistenceError)
            .FirstOrDefault(error => !string.IsNullOrEmpty(error)) ?? "";

        public IReadOnlyList<QwenConversation> Sessions => stores.SelectMany(store => store.Sessions)
            .Where(session => session == Current || session.Turns.Count > 0)
            .OrderByDescending(session => session.Updated).ToList();

        void SelectMostRecent()
        {
            active = stores.Where(store => store.Current.Turns.Count > 0)
                .OrderByDescending(store => store.Current.Updated).FirstOrDefault()
                ?? stores.FirstOrDefault(store => store.HistoryEnabled) ?? stores[0];
        }

        public QwenConversation NewSession() => active.NewSession();

        public QwenConversation Select(string id)
        {
            var owner = stores.FirstOrDefault(store => store.Sessions.Any(session => session.Id == id));
            if (owner != null)
            {
                active = owner;
                active.Select(id);
            }
            return Current;
        }

        public void CommitTurn(QwenConversation session, string question, QwenSearchResult reply, bool replaceLast = false)
        {
            var owner = stores.FirstOrDefault(store => store.Sessions.Contains(session));
            owner?.CommitTurn(session, question, reply, replaceLast);
        }

        public void Delete(string id)
        {
            var owner = stores.FirstOrDefault(store => store.Sessions.Any(session => session.Id == id));
            if (owner == null) return;
            bool deletingCurrent = id == Current.Id;
            owner.Delete(id);
            if (deletingCurrent && owner.Current.Id != id) SelectMostRecent();
        }

        public void Clear()
        {
            foreach (var store in stores) store.Clear();
            SelectMostRecent();
        }

        public void SetHistoryEnabled(bool enabled)
        {
            foreach (var store in stores) store.SetHistoryEnabled(enabled);
        }
    }
}
