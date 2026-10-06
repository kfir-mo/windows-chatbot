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
        LoginOverlay.Visibility = Visibility.Collapsed;
        Title = "עוזר AI · אבטיפוס מקומי";
        ExpandPanel(this, new());
        await ActivateTypeAsync(ChatType.GeneralId, true);
        if (Storage.Load<Config>("local-settings.bin") == null) await OpenLocalSettingsAsync();
    }
    private async Task OpenLocalSettingsAsync()
    {
        if (request != null || indexing || switchingType) { Status.Text = "המתינו לסיום הפעולה הנוכחית."; return; }
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        var previousFocus = System.Windows.Input.Keyboard.FocusedElement;
        var container = (System.Windows.Controls.Grid)SettingsOverlay.Parent;
        var controls = container.Children.Cast<UIElement>().Where(child => child != SettingsOverlay).Select(child => (child, child.IsEnabled)).ToList();
        try
        {
            var dialog = new LocalSettingsPanel(config);
            var completion = new TaskCompletionSource<Config?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Completed += value => completion.TrySetResult(value);
            foreach (var (child, _) in controls) child.IsEnabled = false;
            SettingsHost.Content = dialog;
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsHost.MaxHeight = Math.Max(100, container.ActualHeight - 16);
            dialog.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 160 : 1)));
            var result = await completion.Task;
            if (result != null)
            {
                config = result;
                await ActivateTypeAsync(activeTypeId, true);
            }
        }
        catch (Exception ex) { Status.Text = "בדקו את ai.config.json: " + ex.Message; }
        finally
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsHost.Content = null;
            foreach (var (child, enabled) in controls) child.IsEnabled = enabled;
            if (previousFocus != null) System.Windows.Input.Keyboard.Focus(previousFocus); else Question.Focus();
        }
    }
}
