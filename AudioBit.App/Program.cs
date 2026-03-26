using Velopack;

namespace AudioBit.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(true)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
