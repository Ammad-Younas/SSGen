namespace StoreShot.App.Services;

public enum PageKey
{
    Home,
    Emulator,
    Agent,
    Gallery,
    Editor,
    Export,
    Setup,
    Settings,
}

/// <summary>Lets view models request navigation without referencing the shell.</summary>
public interface INavigationService
{
    event EventHandler<PageKey>? NavigationRequested;

    void NavigateTo(PageKey page);
}

public sealed class NavigationService : INavigationService
{
    public event EventHandler<PageKey>? NavigationRequested;

    public void NavigateTo(PageKey page) => NavigationRequested?.Invoke(this, page);
}
