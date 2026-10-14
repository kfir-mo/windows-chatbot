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
    private int switchVersion;
    /// <summary>Switches at once: the assistant's page shows immediately and its documents load and refresh in the
    /// background, while the header, history and typing stay usable. Switching again stops the previous assistant's
    /// refresh. Only sending waits for the documents (a question sent meanwhile goes out when they are ready).</summary>
    private async Task ActivateTypeAsync(string id, bool restoreHistory)
    {
        if (!loggedIn) return;
        var version = ++switchVersion;
        sendWhenReady = false;
        if (indexTask != null) { quietCancel = true; indexRequest?.Cancel(); try { await indexTask; } catch { } quietCancel = false; }
        if (version != switchVersion) return;
        CloseNotice(this, new());
        var type = chatTypes.First(t => t.Id == id);
        var typeConfig = type.Apply(config);
        var session = SessionIndexFor(id, typeConfig);
        activeTypeId = id; activeConfig = typeConfig; activeSession = session; indexedFiles = null; sourcesReady = false;
        // Only the active assistant's index stays in memory; another one loads from its encrypted cache in a moment.
        foreach (var other in sessionIndexes.Keys.Where(k => k != id).ToList()) sessionIndexes.Remove(other);
        current = restoreHistory ? conversations.LastOrDefault(c => c.ChatTypeId == id) ?? new() { ChatTypeId = id } : new() { ChatTypeId = id };
        switchingType = true; UpdateSendState();
        Status.Text = HasDocuments(type) && !session.Loading.IsCompleted ? "טוען את המסמכים…" : IdleStatus();
        Question.Clear(); ShowPage("chat"); await RenderAsync();
        try { Storage.Save("active-chat-type.bin", id); } catch { }
        DocumentIndex nextIndex;
        try { nextIndex = await session.Loading.WaitAsync(lifetime.Token); }
        catch (Exception) when (version == switchVersion) { switchingType = false; UpdateSendState(); Status.Text = IdleStatus(); Notice("לא ניתן לטעון את המסמכים של העוזר. נסו שוב מתפריט ⋯ · רענון המסמכים."); return; }
        catch { return; }
        if (version != switchVersion) return;
        index = nextIndex; switchingType = false;
        await IndexAsync(false);
    }
    /// <summary>The active assistant's index stays loaded, so a new chat doesn't decrypt, re-tokenize and rescan its folders
    /// again; switching assistants loads the other one from the encrypted cache in %LOCALAPPDATA%.</summary>
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
    private static string FolderName(string path) { var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)); return string.IsNullOrEmpty(name) ? path : name; }
    private void OpenChatTypeDialog(ChatType? type)
    {
        if (!loggedIn || !personalAllowed || type?.IsGeneral == true || type?.IsManaged == true) return;
        if (running.Count > 0) { Notice("המתינו שהתשובות שבדרך יסתיימו.", false); return; }
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
        RemoveUnusedIndexes();
    }
    private void RemoveUnusedIndexes()
    {
        var inUse = chatTypes.Where(t => !t.IsGeneral && !t.IsManaged && t.Paths.Count > 0).Select(t => t.Apply(config)).ToList();
        _ = System.Threading.Tasks.Task.Run(() => DocumentIndex.RemoveUnusedCaches(inUse));
    }
    private async Task SaveChatTypeAsync(ChatType type)
    {
        if (!personalAllowed || type.IsGeneral || type.IsManaged) throw new InvalidOperationException("הצ׳אט הכללי אינו ניתן לעריכה.");
        if (!loggedIn || running.Count > 0) throw new InvalidOperationException("המתינו שהתשובות שבדרך יסתיימו.");
        if (chatTypes.Any(t => t.Id != type.Id && t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("כבר קיים עוזר בשם זה.");
        var next = chatTypes.Where(t => t.Id != type.Id).Append(type).ToList();
        Storage.Save("chat-types.bin", next.Where(t=>!t.IsManaged).ToList()); chatTypes = next;
        await ActivateTypeAsync(type.Id, false);
        RemoveUnusedIndexes();
    }
}


