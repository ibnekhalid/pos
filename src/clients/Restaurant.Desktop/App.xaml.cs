namespace Restaurant.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Counter screens are at least 1024 wide; the POS layout needs room for menu + cart.
        return new Window(new MainPage())
        {
            Title = "Restaurant POS",
            MinimumWidth = 1024,
            MinimumHeight = 640
        };
    }
}
