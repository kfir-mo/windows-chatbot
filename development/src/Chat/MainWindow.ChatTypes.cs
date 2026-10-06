using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Knowledge.Core;

namespace Chat;
public partial class MainWindow
{
    private List<ChatType> chatTypes = [];
    private string activeTypeId = "default";
    private bool switchingType;
    private Config activeConfig = null!;
    private async Task ActivateTypeAsync(string id, bool restoreHistory)
    {
        if (!loggedIn || request != null || indexing || switchingType) return;
        var type = chatTypes.First(t => t.Id == id);
        switchingType = true;
        ChatTypePicker.IsEnabled = SendButton.IsEnabled = HistoryTab.IsEnabled = NewChatButton.IsEnabled = false;
        try
        {
            var typeConfig = type.Apply(config);
            var session = SessionIndexFor(id, typeConfig);
            var nextIndex = await session.Loading.WaitAsync(lifetime.Token);
            activeTypeId = id; activeConfig = typeConfig; index = nextIndex; activeSession = session; indexedFiles = null;
            ChatTypeName.Text = type.Name;
            current = restoreHistory ? conversations.LastOrDefault(c => c.ChatTypeId == id) ?? new() { ChatTypeId = id } : new() { ChatTypeId = id };
            Question.Clear(); ShowPage("chat"); await RenderAsync();
            Storage.Save("active-chat-type.bin", id);
            await IndexAsync(false);
        }
        finally { switchingType = false; ChatTypePicker.IsEnabled = SendButton.IsEnabled = HistoryTab.IsEnabled = NewChatButton.IsEnabled = true; }
    }
    /// <summary>Document indexes stay loaded for the whole session (they vanish when the app closes), so switching
    /// assistants or starting a new chat doesn't decrypt, re-tokenize and rescan the folders again. The persistent,
    /// encrypted cache in %LOCALAPPDATA% still makes the first load after a restart fast.</summary>
    private sealed class SessionIndex(string identity, Task<DocumentIndex> loading)
    {
        public string Identity { get; } = identity;
        public Task<DocumentIndex> Loading { get; } = loading;
        public DateTimeOffset? Refreshed { get; set; }
    }
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, SessionIndex> sessionIndexes = [];
    private SessionIndex? activeSession;
    private SessionIndex SessionIndexFor(string id, Config typeConfig)
    {
        // Folder or limit changes (an edited assistant) build a new index.
        var identity = System.Text.Json.JsonSerializer.Serialize(typeConfig.Documents);
        if (sessionIndexes.TryGetValue(id, out var cached) && cached.Identity == identity && !cached.Loading.IsFaulted && !cached.Loading.IsCanceled) return cached;
        var created = new SessionIndex(identity, Task.Run(() => new DocumentIndex(typeConfig), lifetime.Token));
        sessionIndexes[id] = created;
        return created;
    }
    /// <summary>After startup, load and refresh the other assistants in the background, one at a time.</summary>
    private async Task PreloadAssistantsAsync()
    {
        foreach (var type in chatTypes.Where(t => !t.IsGeneral && !t.IsManaged && t.Paths.Count > 0).ToList())
        {
            try
            {
                await Task.Delay(1500, lifetime.Token);
                if (type.Id == activeTypeId || !chatTypes.Contains(type)) continue;
                var session = SessionIndexFor(type.Id, type.Apply(config));
                if (session.Refreshed != null) continue;
                var loaded = await session.Loading.WaitAsync(lifetime.Token);
                await loaded.RefreshAsync(false, null, lifetime.Token);
                session.Refreshed = DateTimeOffset.Now;
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
        MemoryTrim.Schedule(Dispatcher, expanded ? IdleTrimSeconds : 1.5);
    }
    private static string FolderName(string path) { var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)); return string.IsNullOrEmpty(name) ? path : name; }
    private void OpenChatTypeDialog(ChatType? type)
    {
        if (!loggedIn || !personalAllowed || type?.IsGeneral == true || type?.IsManaged == true) return;
        if (request != null || indexing || switchingType) { Status.Text = "המתינו לסיום הפעולה הנוכחית."; return; }
        // The form is a full-panel screen; its color and symbol are kept only when the assistant is saved.
        var editor = new ChatTypesPage(type, type == null ? NewAssistantLook() : LookOf(type), config.Documents.SearchSubfolders);
        editor.SaveRequested = async saved =>
        {
            var previous = SavedLooks.TryGetValue(saved.Id, out var look) ? look : null;
            SavedLooks[saved.Id] = editor.Look;
            try { await SaveChatTypeAsync(saved); SaveLooks(); ApplyTypeLook(); }
            catch { if (previous == null) SavedLooks.Remove(saved.Id); else SavedLooks[saved.Id] = previous; ApplyTypeLook(); throw; }
        };
        if (type != null) editor.ConversationCount = conversations.Count(c => c.ChatTypeId == type.Id && c.HasUserMessages);
        editor.DeleteRequested = async (id, deleteHistory) => { await DeleteChatTypeAsync(id, deleteHistory); if (SavedLooks.Remove(id)) SaveLooks(); };
        editor.Closed += () => CloseScreen(editor);
        timer?.Stop();
        editor.Unloaded += (_, _) => timer?.Start();
        ShowScreen(editor);
    }
    /// <summary>Removes an assistant. Its conversations leave the history only when the user asked for it; otherwise they stay, read-only.</summary>
    private async Task DeleteChatTypeAsync(string id, bool deleteHistory = false)
    {
        if (!personalAllowed || id == ChatType.GeneralId || id.StartsWith("org:")) throw new InvalidOperationException("לא ניתן למחוק את הצ׳אט הכללי.");
        var next = chatTypes.Where(t => t.Id != id).ToList();
        // Keep a usable, empty chat when the final configured type is removed.
        if (next.Count == 0) next.Add(ChatType.General());
        Storage.Save("chat-types.bin", next.Where(t=>!t.IsManaged).ToList()); chatTypes = next;
        sessionIndexes.Remove(id);
        if (deleteHistory) conversations.RemoveAll(c => c.ChatTypeId == id);
        else foreach (var kept in conversations.Where(c => c.ChatTypeId == id)) kept.KeptAfterDelete = true;
        Save();
        if (activeTypeId == id) await ActivateTypeAsync(next[0].Id, false);
    }
    private async Task SaveChatTypeAsync(ChatType type)
    {
        if (!personalAllowed || type.IsGeneral || type.IsManaged) throw new InvalidOperationException("הצ׳אט הכללי אינו ניתן לעריכה.");
        if (!loggedIn || request != null || indexing || switchingType) throw new InvalidOperationException("המתינו לסיום הפעולה הנוכחית.");
        if (chatTypes.Any(t => t.Id != type.Id && t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("כבר קיים עוזר בשם זה.");
        var next = chatTypes.Where(t => t.Id != type.Id).Append(type).ToList();
        Storage.Save("chat-types.bin", next.Where(t=>!t.IsManaged).ToList()); chatTypes = next;
        await ActivateTypeAsync(type.Id, false);
    }
}


