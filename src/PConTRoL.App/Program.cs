using Velopack;

namespace PConTRoL.App;

/// <summary>
/// Eigener Einstiegspunkt: Velopack muss vor allem anderen laufen (Installation, Update-Hooks,
/// beim Start ein bereits heruntergeladenes Update einspielen).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
