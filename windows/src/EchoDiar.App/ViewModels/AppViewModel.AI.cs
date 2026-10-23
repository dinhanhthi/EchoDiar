namespace EchoDiar.App.ViewModels;

/// <summary>The AI state of the app (macOS AppModel's AI providers): the providers model Settings › AI and the AI pickers share.</summary>
public sealed partial class AppViewModel
{
    private ProvidersViewModel? _providersModel;

    /// <summary>What Settings › AI binds to, and the AI pickers' providers; one for the app's lifetime.</summary>
    public ProvidersViewModel ProvidersModel => _providersModel ??= new ProvidersViewModel(this, _services);
}
