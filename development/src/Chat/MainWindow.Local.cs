using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Knowledge.Core;

namespace Chat;
public partial class MainWindow
{
    private async Task InitializeLocalAsync()
    {
        LoginOverlay.Visibility = Visibility.Collapsed;
        config = Storage.Load<Config>("local-settings.bin") ?? new Config();
        config = LocalAiCatalog.Load().Apply(config, config.Ai.Model);
        personalAllowed = loggedIn = true;
        conversations = (Storage.Load<List<Conversation>>("conversations.bin") ?? []).Where(c => c.HasUserMessages).ToList();
        chatTypes = (Storage.Load<List<ChatType>>("chat-types.bin") ?? []).Where(t => !t.IsGeneral && !t.IsManaged).ToList();
        chatTypes.Insert(0, ChatType.General());
        // Conversations of assistants that no longer exist leave the history, except those the user chose to keep when deleting.
        if (conversations.RemoveAll(c => !c.KeptAfterDelete && !chatTypes.Any(t => t.Id == c.ChatTypeId)) > 0) Save();
        LoginOverlay.Visibility = Visibility.Collapsed;
        Title = "Chaty";
        ExpandPanel(this, new());
        // Every start opens a new, empty general chat; earlier conversations are in the history.
        await ActivateTypeAsync(ChatType.GeneralId, false);
        RemoveUnusedIndexes();
        // The first start shows the tour, which ends by opening the AI settings when there is no API key yet.
        // Later, a PC with no settings or no models (no config.json) opens the AI settings directly.
        if (Storage.Load<bool?>("tour-shown.bin") != true)
        {
            try { Storage.Save("tour-shown.bin", true); } catch { }
            UpdateLayout();
            StartTour(true);
        }
        else if (Storage.Load<Config>("local-settings.bin") == null || LocalAiCatalog.Load().Models.Count == 0) await OpenLocalSettingsAsync();
        MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds);
    }
    private async Task OpenLocalSettingsAsync()
    {
        if (running.Count > 0) { Notice("המתינו שהתשובות שבדרך יסתיימו.", false); return; }
        if (ScreenHost.Content is LocalSettingsPanel) return;
        LocalSettingsPanel? dialog = null;
        try
        {
            // A full-panel screen with a back arrow, not a dialog over the chat.
            dialog = new LocalSettingsPanel(config);
            var completion = new TaskCompletionSource<Config?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Completed += value => completion.TrySetResult(value);
            ShowScreen(dialog);
            var result = await completion.Task;
            if (result != null)
            {
                config = result;
                // Rebuild with the new model/key but stay in the conversation the user was in.
                var conversation = current;
                await ActivateTypeAsync(activeTypeId, false);
                current = conversation;
                await RenderAsync();
            }
        }
        catch (Exception ex) { Notice("לא ניתן לפתוח את הגדרות ה־AI: " + ex.Message); }
        finally { if (dialog != null) CloseScreen(dialog); }
    }
}
