using EchoDiar.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// The model and language menus of the AI panel's Chat and Summary tabs and of the library chat (macOS
/// <c>AIModelMenu</c> and <c>AILanguageMenu</c>, shared by AIPanelView.swift and GlobalChatView.swift), and the setup hint
/// shown while no model can run. Built when they open, from the providers model.
/// </summary>
internal static class AIMenus
{
    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    /// <summary>The model button's label: an in-app model's name, else the picked model, else the provider's name.</summary>
    public static string ModelTitle(ProvidersViewModel providers)
    {
        var current = providers.CurrentAIProvider;
        if (current is null) return Loc.T("No AI model");
        if (current.Kind == AIProviderKind.InApp) return current.Name;
        return providers.AIModelFor(current) is { Length: > 0 } model ? model : current.Name;
    }

    /// <summary>Why Windows AI cannot run, under the setup hint; null when it is hidden or ready.</summary>
    public static string? WindowsAIHint(ProvidersViewModel providers) =>
        providers.WindowsAI is { Hidden: false, Reason: { } reason } ? Loc.T(reason) : null;

    /// <summary>
    /// Every provider with the models it offers (macOS <c>AIModelMenu</c>): on this PC first, then the hosts, then the
    /// ones whose data leaves this PC; a provider without a model says to choose one in Settings, one that cannot run now
    /// is off with why; then "Choose Models…" (<paramref name="chooseModels"/>: Settings › AI).
    /// </summary>
    public static void FillModelMenu(MenuFlyout menu, ProvidersViewModel providers, Action chooseModels)
    {
        menu.Items.Clear();
        var all = providers.AIProviders;
        var current = providers.CurrentAIProvider;
        var currentModel = current is null ? null : providers.AIModelFor(current);
        var inApp = all.Where(p => p.Kind == AIProviderKind.InApp).ToList();
        var hosts = all.Where(p => p.Kind != AIProviderKind.InApp && !p.LeavesPC).Concat(all.Where(p => p.LeavesPC)).ToList();
        if (inApp.Count > 0)
        {
            menu.Items.Add(Header(Loc.T("On this PC")));
            foreach (var provider in inApp) menu.Items.Add(ModelItem(providers, provider, provider.DefaultModel, provider.Name, current, currentModel));
        }
        foreach (var provider in hosts)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Header(provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", provider.Name) : provider.Name));
            if (provider.Models.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No model: choose one in Settings"), IsEnabled = false });
            foreach (var id in provider.Models)
            {
                menu.Items.Add(ModelItem(providers, provider, id, id.Length == 0 ? Loc.T("Default") : id, current, currentModel));
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var choose = new MenuFlyoutItem { Text = Loc.T("Choose Models…") };
        choose.Click += (_, _) => chooseModels();
        menu.Items.Add(choose);
    }

    /// <summary>Auto, English, French, Vietnamese, and a hand-edited code (macOS <c>AILanguageMenu</c>).</summary>
    public static void FillLanguageMenu(MenuFlyout menu, ProvidersViewModel providers, string selection)
    {
        menu.Items.Clear();
        var choices = Languages.Select(l => (l.Code, Name: Loc.T(l.Name))).ToList();
        if (choices.All(c => c.Code != selection)) choices.Add((selection, selection));
        foreach (var (code, name) in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "SummaryLanguage", IsChecked = code == selection };
            item.Click += (_, _) =>
            {
                if (providers.SummaryLanguage != code) providers.SummaryLanguage = code;
            };
            menu.Items.Add(item);
        }
    }

    private static MenuFlyoutItem Header(string text) => new() { Text = text, IsEnabled = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static RadioMenuFlyoutItem ModelItem(
        ProvidersViewModel providers, AIProviderInfo provider, string id, string title, AIProviderInfo? current, string? currentModel)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = title,
            GroupName = "AIModel",
            IsChecked = current?.Id == provider.Id && currentModel == id,
            IsEnabled = provider.UnavailableReason is null,
        };
        if (provider.UnavailableReason is { } reason) ToolTipService.SetToolTip(item, reason);
        item.Click += (_, _) => providers.SetAIModel(id, provider);
        return item;
    }
}
